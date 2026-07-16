using WinDayFlow.Application.Settings;

namespace WinDayFlow.Application.Capture;

public sealed class ConsentGatedCaptureService : ICaptureService, IDisposable
{
    private const string ConsentRequiredDetail =
        "请先在设置中确认录制授权。";
    private const string ConsentStopFailedDetail =
        "录制授权已失效，但自动停止失败。请立即使用停止操作。";

    private readonly object _sync = new();
    private readonly ICaptureBackend _backend;
    private readonly AppSettingsService _settings;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CaptureStatus _status;
    private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;
    private int _consentStopScheduled;
    private bool _disposed;

    public ConsentGatedCaptureService(
        ICaptureBackend backend,
        AppSettingsService settings)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _status = ProjectStatus(_backend.CurrentStatus, current: null);

        _backend.StatusChanged += OnBackendStatusChanged;
        _settings.SettingsChanged += OnSettingsChanged;
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
            _backend.StopAsync,
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

        _lifetimeCancellation.Cancel();
        _backend.StatusChanged -= OnBackendStatusChanged;
        _settings.SettingsChanged -= OnSettingsChanged;
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
            if (requiresConsent && !_settings.HasValidRecordingConsent)
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

        if (backendStatus.State is CaptureState.Unavailable or CaptureState.Faulted
            || _settings.HasValidRecordingConsent
            || MayRetainCaptureResources(backendStatus.State))
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
        if (_settings.HasValidRecordingConsent
            || !ShouldInitiateConsentStop(backendStatus.State)
            || IsDisposed()
            || Interlocked.CompareExchange(ref _consentStopScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = StopForMissingConsentAsync();
    }

    private async Task StopForMissingConsentAsync()
    {
        var entered = false;
        CaptureStatus? failureStatus = null;
        try
        {
            await _lifecycleGate
                .WaitAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            entered = true;

            if (!_settings.HasValidRecordingConsent
                && ShouldInitiateConsentStop(_backend.CurrentStatus.State))
            {
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
            UpdateStatus(failureStatus ?? _backend.CurrentStatus);
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
