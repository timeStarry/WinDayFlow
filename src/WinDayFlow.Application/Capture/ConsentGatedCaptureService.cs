using WinDayFlow.Application.Settings;

namespace WinDayFlow.Application.Capture;

public sealed class ConsentGatedCaptureService : ICaptureService, IDisposable
{
    private const int RuntimeResumeRetryLimit = 3;
    private const string ConsentRequiredDetail =
        "请先在设置中确认录制授权。";
    private const string ConsentStopFailedDetail =
        "录制已关闭或授权已失效，但自动停止失败。请立即使用停止操作。";
    private static readonly TimeSpan RuntimeResumeRetryDelay =
        TimeSpan.FromMilliseconds(50);

    private readonly object _sync = new();
    private readonly ICaptureBackend _backend;
    private readonly AppSettingsService _settings;
    private readonly ICaptureRuntimeAuthorization _runtimeAuthorization;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CaptureStatus _status;
    private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;
    private int _consentStopScheduled;
    private int _runtimeResumeScheduled;
    private int _runtimeResumeWakePending;
    private long _pendingRuntimeInvalidationGeneration;
    private long _handledRuntimeInvalidationGeneration;
    private long _runtimePauseOwnedGeneration;
    private long _userIntentVersion;
    private CaptureUserIntent _userIntent;
    private CancellationTokenSource? _runtimeResumeCancellation;
    private bool _disposed;

    public ConsentGatedCaptureService(
        ICaptureBackend backend,
        AppSettingsService settings,
        ICaptureRuntimeAuthorization runtimeAuthorization)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runtimeAuthorization = runtimeAuthorization
            ?? throw new ArgumentNullException(nameof(runtimeAuthorization));
        _status = ProjectStatus(_backend.CurrentStatus, current: null);
        _userIntent = InferInitialUserIntent(_backend.CurrentStatus);
        var initialInvalidationGeneration =
            _runtimeAuthorization.InvalidationGeneration;
        _pendingRuntimeInvalidationGeneration = initialInvalidationGeneration;
        _handledRuntimeInvalidationGeneration =
            _runtimeAuthorization.IsCaptureAuthorized
                ? initialInvalidationGeneration
                : Math.Max(0, initialInvalidationGeneration - 1);

        _backend.StatusChanged += OnBackendStatusChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        _runtimeAuthorization.AuthorizationChanged += OnRuntimeAuthorizationChanged;
        ObserveRuntimeInvalidation(
            _runtimeAuthorization.InvalidationGeneration,
            _backend.CurrentStatus);
        ScheduleConsentStopIfRequired(_backend.CurrentStatus);
        SignalRuntimeResumeReconciliation();
    }

    public CaptureStatus CurrentStatus
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
    {
        add
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _statusChanged += value;
            }
        }
        remove
        {
            lock (_sync)
            {
                _statusChanged -= value;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        InvokeAuthorizedBackendAsync(
            CaptureAdmissionOperation.Start,
            _backend.StartAsync,
            cancellationToken);

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        SetStickyUserIntent(CaptureUserIntent.Paused);
        await InvokeBackendAsync(
                _backend.PauseAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        InvokeAuthorizedBackendAsync(
            CaptureAdmissionOperation.Resume,
            _backend.ResumeAsync,
            cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        SetStickyUserIntent(CaptureUserIntent.Stopped);
        await InvokeBackendAsync(
                token => ShouldInitiateConsentStop(_backend.CurrentStatus.State)
                    ? _backend.StopAsync(token)
                    : Task.CompletedTask,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _statusChanged = null;
        }

        _backend.StatusChanged -= OnBackendStatusChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
        _runtimeAuthorization.AuthorizationChanged -= OnRuntimeAuthorizationChanged;
        _lifetimeCancellation.Cancel();
    }

    private async Task InvokeBackendAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _lifecycleGate
            .WaitAsync(linkedCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await operation(linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            UpdateStatus(_backend.CurrentStatus);
            _lifecycleGate.Release();
        }
    }

    private async Task InvokeAuthorizedBackendAsync(
        CaptureAdmissionOperation admissionOperation,
        Func<ICaptureRuntimeAdmissionStamp, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _lifecycleGate
            .WaitAsync(linkedCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var expectedIntentVersion = ReadUserIntentVersion();
            if (!HasPersistentCaptureAuthorization())
            {
                throw new RecordingConsentRequiredException();
            }

            var admissionStamp = await _runtimeAuthorization
                .TryIssueAdmissionAsync(admissionOperation, linkedCancellation.Token)
                .ConfigureAwait(false);
            if (admissionStamp is null)
            {
                throw new RecordingConsentRequiredException();
            }

            ThrowIfDisposed();
            linkedCancellation.Token.ThrowIfCancellationRequested();
            if (!HasCaptureAuthorization()
                || admissionStamp.InvalidationGeneration
                    != _runtimeAuthorization.InvalidationGeneration)
            {
                throw new RecordingConsentRequiredException();
            }

            if (!TrySetRecordingIntent(
                    expectedIntentVersion,
                    admissionStamp.InvalidationGeneration,
                    out var appliedIntentVersion))
            {
                throw new CaptureRuntimeAdmissionRejectedException();
            }

            if (!IsRecordingIntentCurrent(appliedIntentVersion)
                || !HasCaptureAuthorization()
                || admissionStamp.InvalidationGeneration
                    != _runtimeAuthorization.InvalidationGeneration)
            {
                throw new CaptureRuntimeAdmissionRejectedException();
            }

            await operation(admissionStamp, linkedCancellation.Token)
                .ConfigureAwait(false);
            AdvanceHandledRuntimeInvalidation(
                admissionStamp.InvalidationGeneration);
        }
        finally
        {
            UpdateStatus(_backend.CurrentStatus);
            _lifecycleGate.Release();
        }
    }

    private void OnBackendStatusChanged(
        object? sender,
        CaptureStatusChangedEventArgs eventArgs)
    {
        UpdateStatus(eventArgs.Current);
        if (DoesNotRetainResumableRun(eventArgs.Current.State))
        {
            RelinquishRuntimePauseOwnership();
        }

        ScheduleConsentStopIfRequired(eventArgs.Current);
        SignalRuntimeResumeReconciliation();
    }

    private void OnSettingsChanged(
        object? sender,
        AppSettingsChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        if (!HasPersistentCaptureAuthorization())
        {
            SetStickyUserIntent(CaptureUserIntent.Stopped);
        }

        var backendStatus = _backend.CurrentStatus;
        UpdateStatus(backendStatus);
        ScheduleConsentStopIfRequired(backendStatus);
        SignalRuntimeResumeReconciliation();
    }

    private void OnRuntimeAuthorizationChanged(
        object? sender,
        CaptureRuntimeAuthorizationChangedEventArgs eventArgs)
    {
        _ = sender;
        var backendStatus = _backend.CurrentStatus;
        if (!eventArgs.IsCaptureAuthorized)
        {
            ObserveRuntimeInvalidation(
                eventArgs.InvalidationGeneration,
                backendStatus);
        }

        UpdateStatus(backendStatus);
        SignalRuntimeResumeReconciliation();
    }

    private void UpdateStatus(CaptureStatus backendStatus)
    {
        EventHandler<CaptureStatusChangedEventArgs>? handler;
        CaptureStatus previous;
        CaptureStatus current;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            previous = _status;
            current = ProjectStatus(backendStatus, previous);
            if (current == previous)
            {
                return;
            }

            _status = current;
            handler = _statusChanged;
        }

        if (handler is null)
        {
            return;
        }

        var eventArgs = new CaptureStatusChangedEventArgs(previous, current);
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<CaptureStatusChangedEventArgs>)subscriber)(
                    this,
                    eventArgs);
            }
            catch (Exception)
            {
            }
        }
    }

    private CaptureStatus ProjectStatus(
        CaptureStatus backendStatus,
        CaptureStatus? current)
    {
        ArgumentNullException.ThrowIfNull(backendStatus);

        if (!HasCaptureAuthorization()
            && MayRetainCaptureResources(backendStatus.State)
            && current?.Sequence == backendStatus.Sequence
            && string.Equals(
                current.Detail,
                ConsentStopFailedDetail,
                StringComparison.Ordinal))
        {
            return backendStatus with { Detail = ConsentStopFailedDetail };
        }

        if (backendStatus.State is CaptureState.Unavailable or CaptureState.Faulted
            || HasCaptureAuthorization()
            || MayRetainCaptureResources(backendStatus.State))
        {
            return backendStatus;
        }

        if (_settings.HasValidRecordingConsent
            && !_settings.Current.CaptureEnabled)
        {
            return backendStatus;
        }

        if (current?.State == CaptureState.BlockedByConsent)
        {
            return current;
        }

        return new CaptureStatus(
            CaptureState.BlockedByConsent,
            DateTimeOffset.UtcNow,
            ConsentRequiredDetail,
            Sequence: backendStatus.Sequence,
            Reason: CaptureReasonCode.ConsentRequired);
    }

    private void ScheduleConsentStopIfRequired(CaptureStatus backendStatus)
    {
        if (IsDisposed()
            || HasPersistentCaptureAuthorization()
            || !ShouldInitiateConsentStop(backendStatus.State)
            || Interlocked.CompareExchange(ref _consentStopScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = StopForMissingConsentAsync();
    }

    private async Task StopForMissingConsentAsync()
    {
        var entered = false;
        var stopFailed = false;
        var stopAttempted = false;
        CaptureStatus? failureStatus = null;
        try
        {
            await _lifecycleGate
                .WaitAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            entered = true;

            if (!HasPersistentCaptureAuthorization()
                && ShouldInitiateConsentStop(_backend.CurrentStatus.State))
            {
                stopAttempted = true;
                await _backend
                    .StopAsync(_lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            stopFailed = true;
            var backendStatus = _backend.CurrentStatus;
            failureStatus = MayRetainCaptureResources(backendStatus.State)
                ? backendStatus with { Detail = ConsentStopFailedDetail }
                : backendStatus;
        }
        finally
        {
            if (entered)
            {
                _lifecycleGate.Release();
            }

            Interlocked.Exchange(ref _consentStopScheduled, 0);
            var backendStatus = failureStatus ?? _backend.CurrentStatus;
            UpdateStatus(backendStatus);
            if (!stopAttempted
                && !stopFailed
                && !HasPersistentCaptureAuthorization()
                && ShouldInitiateConsentStop(backendStatus.State))
            {
                ScheduleConsentStopIfRequired(backendStatus);
            }
        }
    }

    private void ObserveRuntimeInvalidation(
        long generation,
        CaptureStatus backendStatus)
    {
        if (generation <= 0)
        {
            return;
        }

        AdvanceGeneration(ref _pendingRuntimeInvalidationGeneration, generation);
        lock (_sync)
        {
            if (!_disposed
                && _userIntent == CaptureUserIntent.Recording
                && HasPersistentCaptureAuthorization()
                && MayRetainCaptureResources(backendStatus.State))
            {
                AdvanceGeneration(ref _runtimePauseOwnedGeneration, generation);
                return;
            }
        }

        AdvanceHandledRuntimeInvalidation(generation);
    }

    private void AdvanceHandledRuntimeInvalidation(long generation)
    {
        if (generation > 0)
        {
            AdvanceGeneration(ref _handledRuntimeInvalidationGeneration, generation);
        }
    }

    private void SignalRuntimeResumeReconciliation()
    {
        if (IsDisposed())
        {
            return;
        }

        Interlocked.Exchange(ref _runtimeResumeWakePending, 1);
        if (Interlocked.CompareExchange(ref _runtimeResumeScheduled, 1, 0) == 0)
        {
            _ = ReconcileRuntimeResumeAsync();
        }
    }

    private async Task ReconcileRuntimeResumeAsync()
    {
        var consecutiveFailures = 0;
        try
        {
            while (Interlocked.Exchange(ref _runtimeResumeWakePending, 0) != 0)
            {
                var entered = false;
                var shouldRetry = false;
                try
                {
                    await _lifecycleGate
                        .WaitAsync(_lifetimeCancellation.Token)
                        .ConfigureAwait(false);
                    entered = true;
                    await TryResumeAfterRuntimeRecoveryAsync()
                        .ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                    when (_lifetimeCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    consecutiveFailures = 0;
                    UpdateStatus(_backend.CurrentStatus);
                }
                catch (RecordingConsentRequiredException)
                {
                    consecutiveFailures = 0;
                }
                catch (CaptureRuntimeAdmissionRejectedException)
                {
                    consecutiveFailures = 0;
                }
                catch (Exception)
                {
                    UpdateStatus(_backend.CurrentStatus);
                    consecutiveFailures++;
                    shouldRetry = consecutiveFailures < RuntimeResumeRetryLimit;
                }
                finally
                {
                    if (entered)
                    {
                        _lifecycleGate.Release();
                    }
                }

                if (shouldRetry)
                {
                    try
                    {
                        await Task.Delay(
                                RuntimeResumeRetryDelay,
                                _lifetimeCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (_lifetimeCancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    if (ShouldResumeAfterRuntimeRecovery(_backend.CurrentStatus))
                    {
                        Interlocked.Exchange(ref _runtimeResumeWakePending, 1);
                    }
                    else
                    {
                        consecutiveFailures = 0;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _runtimeResumeScheduled, 0);
            if (Volatile.Read(ref _runtimeResumeWakePending) != 0
                && !IsDisposed()
                && Interlocked.CompareExchange(
                    ref _runtimeResumeScheduled,
                    1,
                    0) == 0)
            {
                _ = ReconcileRuntimeResumeAsync();
            }
        }
    }

    private async Task TryResumeAfterRuntimeRecoveryAsync()
    {
        var backendStatus = _backend.CurrentStatus;
        if (!ShouldResumeAfterRuntimeRecovery(backendStatus))
        {
            return;
        }

        var expectedIntentVersion = ReadUserIntentVersion();
        var ownedGeneration = Volatile.Read(ref _runtimePauseOwnedGeneration);
        var resumeCancellation = TryBeginRuntimeResume(expectedIntentVersion);
        if (resumeCancellation is null)
        {
            throw new CaptureRuntimeAdmissionRejectedException();
        }

        using (resumeCancellation)
        {
            try
            {
                var admissionStamp = await _runtimeAuthorization
                    .TryIssueAdmissionAsync(
                        CaptureAdmissionOperation.Resume,
                        resumeCancellation.Token)
                    .ConfigureAwait(false);
                if (admissionStamp is null)
                {
                    throw new RecordingConsentRequiredException();
                }

                if (!IsRecordingIntentCurrent(expectedIntentVersion)
                    || !HasCaptureAuthorization()
                    || admissionStamp.InvalidationGeneration
                        != _runtimeAuthorization.InvalidationGeneration
                    || admissionStamp.InvalidationGeneration < ownedGeneration
                    || !ShouldResumeAfterRuntimeRecovery(_backend.CurrentStatus))
                {
                    throw new CaptureRuntimeAdmissionRejectedException();
                }

                await _backend
                    .ResumeAsync(admissionStamp, resumeCancellation.Token)
                    .ConfigureAwait(false);
                resumeCancellation.Token.ThrowIfCancellationRequested();
                if (!IsRecordingIntentCurrent(expectedIntentVersion))
                {
                    throw new CaptureRuntimeAdmissionRejectedException();
                }

                AdvanceHandledRuntimeInvalidation(
                    admissionStamp.InvalidationGeneration);
                UpdateStatus(_backend.CurrentStatus);
            }
            finally
            {
                EndRuntimeResume(resumeCancellation);
            }
        }
    }

    private bool ShouldResumeAfterRuntimeRecovery(CaptureStatus backendStatus)
    {
        if (backendStatus.State != CaptureState.Paused
            || !HasCaptureAuthorization()
            || Volatile.Read(ref _runtimePauseOwnedGeneration)
                <= Volatile.Read(ref _handledRuntimeInvalidationGeneration))
        {
            return false;
        }

        lock (_sync)
        {
            return !_disposed && _userIntent == CaptureUserIntent.Recording;
        }
    }

    private void SetStickyUserIntent(CaptureUserIntent intent)
    {
        if (intent == CaptureUserIntent.Recording)
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        CancellationTokenSource? resumeCancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _userIntent = intent;
            _userIntentVersion = unchecked(_userIntentVersion + 1);
            resumeCancellation = _runtimeResumeCancellation;
        }

        CancelRuntimeResume(resumeCancellation);
        RelinquishRuntimePauseOwnership();
        SignalRuntimeResumeReconciliation();
    }

    private CancellationTokenSource? TryBeginRuntimeResume(
        long expectedIntentVersion)
    {
        var resumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        lock (_sync)
        {
            if (!_disposed
                && _userIntent == CaptureUserIntent.Recording
                && _userIntentVersion == expectedIntentVersion
                && _runtimeResumeCancellation is null)
            {
                _runtimeResumeCancellation = resumeCancellation;
                return resumeCancellation;
            }
        }

        resumeCancellation.Dispose();
        return null;
    }

    private void EndRuntimeResume(CancellationTokenSource resumeCancellation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(
                    _runtimeResumeCancellation,
                    resumeCancellation))
            {
                _runtimeResumeCancellation = null;
            }
        }
    }

    private static void CancelRuntimeResume(
        CancellationTokenSource? resumeCancellation)
    {
        try
        {
            resumeCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }
    }

    private bool TrySetRecordingIntent(
        long expectedIntentVersion,
        long admittedInvalidationGeneration,
        out long appliedIntentVersion)
    {
        lock (_sync)
        {
            if (_disposed
                || _userIntentVersion != expectedIntentVersion
                || !HasCaptureAuthorization()
                || admittedInvalidationGeneration
                    != _runtimeAuthorization.InvalidationGeneration)
            {
                appliedIntentVersion = 0;
                return false;
            }

            _userIntent = CaptureUserIntent.Recording;
            _userIntentVersion = unchecked(_userIntentVersion + 1);
            appliedIntentVersion = _userIntentVersion;
        }

        AdvanceHandledRuntimeInvalidation(admittedInvalidationGeneration);
        return true;
    }

    private long ReadUserIntentVersion()
    {
        lock (_sync)
        {
            return _userIntentVersion;
        }
    }

    private bool IsRecordingIntentCurrent(long expectedIntentVersion)
    {
        lock (_sync)
        {
            return !_disposed
                && _userIntent == CaptureUserIntent.Recording
                && _userIntentVersion == expectedIntentVersion;
        }
    }

    private void RelinquishRuntimePauseOwnership()
    {
        AdvanceHandledRuntimeInvalidation(
            Math.Max(
                Volatile.Read(ref _pendingRuntimeInvalidationGeneration),
                Volatile.Read(ref _runtimePauseOwnedGeneration)));
    }

    private static void AdvanceGeneration(ref long location, long generation)
    {
        var current = Volatile.Read(ref location);
        while (generation > current)
        {
            var observed = Interlocked.CompareExchange(
                ref location,
                generation,
                current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static bool ShouldInitiateConsentStop(CaptureState state)
    {
        return state is CaptureState.Starting
            or CaptureState.Recording
            or CaptureState.Pausing
            or CaptureState.Paused
            or CaptureState.Resuming
            or CaptureState.Faulted;
    }

    private static bool MayRetainCaptureResources(CaptureState state)
    {
        return ShouldInitiateConsentStop(state)
            || state == CaptureState.Stopping;
    }

    private static bool DoesNotRetainResumableRun(CaptureState state)
    {
        return state is CaptureState.Unavailable
            or CaptureState.Stopped
            or CaptureState.Stopping
            or CaptureState.Faulted;
    }

    private CaptureUserIntent InferInitialUserIntent(CaptureStatus backendStatus)
    {
        if (!HasPersistentCaptureAuthorization())
        {
            return CaptureUserIntent.Stopped;
        }

        return backendStatus.State switch
        {
            CaptureState.Starting or
            CaptureState.Recording or
            CaptureState.Pausing or
            CaptureState.Resuming => CaptureUserIntent.Recording,
            CaptureState.Paused => CaptureUserIntent.Paused,
            _ => CaptureUserIntent.Stopped,
        };
    }

    private bool HasCaptureAuthorization()
    {
        return HasPersistentCaptureAuthorization()
            && _runtimeAuthorization.IsCaptureAuthorized;
    }

    private bool HasPersistentCaptureAuthorization()
    {
        return _settings.Current.CaptureEnabled
            && _settings.HasValidRecordingConsent;
    }

    private bool IsDisposed()
    {
        lock (_sync)
        {
            return _disposed;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private enum CaptureUserIntent
    {
        Stopped = 0,
        Recording = 1,
        Paused = 2,
    }
}
