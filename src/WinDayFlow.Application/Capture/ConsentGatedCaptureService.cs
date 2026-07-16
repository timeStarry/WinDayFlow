using WinDayFlow.Application.Settings;

namespace WinDayFlow.Application.Capture;

public sealed class ConsentGatedCaptureService : ICaptureService, IDisposable
{
    private const string ConsentRequiredDetail =
        "请先在设置中确认录制授权。";
    private const string ConsentStopFailedDetail =
        "录制已关闭或授权已失效，但自动停止失败。请立即使用停止操作。";

    private readonly object _sync = new();
    private readonly ICaptureBackend _backend;
    private readonly AppSettingsService _settings;
    private readonly ICaptureRuntimeAuthorization _runtimeAuthorization;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CaptureStatus _status;
    private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;
    private int _consentStopScheduled;
    private long _pendingRuntimeInvalidationGeneration;
    private long _handledRuntimeInvalidationGeneration;
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

        _backend.StatusChanged += OnBackendStatusChanged;
        _settings.SettingsChanged += OnSettingsChanged;
        _runtimeAuthorization.AuthorizationChanged += OnRuntimeAuthorizationChanged;
        ObserveRuntimeInvalidation(_runtimeAuthorization.InvalidationGeneration);
        ScheduleConsentStopIfRequired(_backend.CurrentStatus);
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
        InvokeBackendAsync(
            _backend.StartAsync,
            requiresConsent: true,
            cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        InvokeBackendAsync(
            _backend.PauseAsync,
            requiresConsent: false,
            cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        InvokeBackendAsync(
            _backend.ResumeAsync,
            requiresConsent: true,
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        InvokeBackendAsync(
            token => ShouldInitiateConsentStop(_backend.CurrentStatus.State)
                ? _backend.StopAsync(token)
                : Task.CompletedTask,
            requiresConsent: false,
            cancellationToken);

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
        bool requiresConsent,
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
            if (requiresConsent && !HasCaptureAuthorization())
            {
                throw new RecordingConsentRequiredException();
            }

            await operation(linkedCancellation.Token).ConfigureAwait(false);
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
        ScheduleConsentStopIfRequired(eventArgs.Current);
    }

    private void OnSettingsChanged(
        object? sender,
        AppSettingsChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        var backendStatus = _backend.CurrentStatus;
        UpdateStatus(backendStatus);
        ScheduleConsentStopIfRequired(backendStatus);
    }

    private void OnRuntimeAuthorizationChanged(
        object? sender,
        CaptureRuntimeAuthorizationChangedEventArgs eventArgs)
    {
        _ = sender;
        ObserveRuntimeInvalidation(eventArgs.InvalidationGeneration);
        var backendStatus = _backend.CurrentStatus;
        UpdateStatus(backendStatus);
        ScheduleConsentStopIfRequired(backendStatus);
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

        handler?.Invoke(
            this,
            new CaptureStatusChangedEventArgs(previous, current));
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
        var hasPendingRuntimeInvalidation = HasPendingRuntimeInvalidation();
        if (IsDisposed()
            || (!hasPendingRuntimeInvalidation
                && (HasCaptureAuthorization()
                    || !ShouldInitiateConsentStop(backendStatus.State)))
            || Interlocked.CompareExchange(ref _consentStopScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = StopForMissingConsentAsync();
    }

    private async Task StopForMissingConsentAsync()
    {
        var entered = false;
        var handledRuntimeGeneration = 0L;
        var stopFailed = false;
        var stopAttempted = false;
        CaptureStatus? failureStatus = null;
        try
        {
            await _lifecycleGate
                .WaitAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            entered = true;

            handledRuntimeGeneration = Volatile.Read(
                ref _pendingRuntimeInvalidationGeneration);
            if ((handledRuntimeGeneration
                    > Volatile.Read(ref _handledRuntimeInvalidationGeneration)
                    || !HasCaptureAuthorization())
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
            AdvanceHandledRuntimeInvalidation(handledRuntimeGeneration);
            if (entered)
            {
                _lifecycleGate.Release();
            }

            Interlocked.Exchange(ref _consentStopScheduled, 0);
            var backendStatus = failureStatus ?? _backend.CurrentStatus;
            UpdateStatus(backendStatus);
            if (HasPendingRuntimeInvalidation()
                || (!stopAttempted
                    && !stopFailed
                    && !HasCaptureAuthorization()
                    && ShouldInitiateConsentStop(backendStatus.State)))
            {
                ScheduleConsentStopIfRequired(backendStatus);
            }
        }
    }

    private void ObserveRuntimeInvalidation(long generation)
    {
        if (generation <= 0)
        {
            return;
        }

        AdvanceGeneration(ref _pendingRuntimeInvalidationGeneration, generation);
    }

    private bool HasPendingRuntimeInvalidation()
    {
        return Volatile.Read(ref _pendingRuntimeInvalidationGeneration)
            > Volatile.Read(ref _handledRuntimeInvalidationGeneration);
    }

    private void AdvanceHandledRuntimeInvalidation(long generation)
    {
        if (generation > 0)
        {
            AdvanceGeneration(ref _handledRuntimeInvalidationGeneration, generation);
        }
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

    private bool HasCaptureAuthorization()
    {
        return _settings.Current.CaptureEnabled
            && _settings.HasValidRecordingConsent
            && _runtimeAuthorization.IsCaptureAuthorized;
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
}
