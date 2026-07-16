using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public sealed class NativeCaptureRuntimeOwner
    : ICaptureBackend,
      IAppSettingsCommitBarrier,
      ICaptureRuntimeAuthorization,
      INativeCapturePrivacySignalSink,
      IAsyncDisposable
{
    private readonly object _terminationSync = new();
    private readonly INativeCaptureRuntimeBackend _backend;
    private readonly NativeCapturePrivacyCoordinator _coordinator;
    private Task? _terminationTask;
    private int _terminating;

    public NativeCaptureRuntimeOwner(
        NativeCaptureConfiguration configuration,
        NativeCapturePrivacyContext initialPrivacyContext,
        AppSettings? initialSettings = null,
        NativeCapturePrivacySignals? initialSignals = null)
        : this(
            new NativeCaptureBackend(configuration, initialPrivacyContext),
            initialPrivacyContext,
            initialSettings,
            initialSignals)
    {
    }

    internal NativeCaptureRuntimeOwner(
        INativeCaptureRuntimeBackend backend,
        NativeCapturePrivacyContext initialPrivacyContext,
        AppSettings? initialSettings = null,
        NativeCapturePrivacySignals? initialSignals = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(initialPrivacyContext);
        if ((_backend.Capabilities & NativeCaptureAbiContract.RuntimeSafetyCapabilities)
            != NativeCaptureAbiContract.RuntimeSafetyCapabilities)
        {
            _backend.DisposeSafelyAfterConstructionFailure();
            throw new NotSupportedException(
                "The native capture runtime owner requires target authorization, a persistence generation barrier, and deterministic stop support.");
        }

        try
        {
            _coordinator = new NativeCapturePrivacyCoordinator(
                backend,
                initialPrivacyContext,
                initialSettings,
                initialSignals);
        }
        catch
        {
            _backend.DisposeSafelyAfterConstructionFailure();
            throw;
        }
        _backend.StatusChanged += OnBackendStatusChanged;
    }

    public CaptureStatus CurrentStatus => _backend.CurrentStatus;

    public bool IsCaptureAuthorized =>
        Volatile.Read(ref _terminating) == 0
        && _coordinator.IsCaptureAuthorized;

    public long InvalidationGeneration => _coordinator.InvalidationGeneration;

    internal Task Termination
    {
        get
        {
            lock (_terminationSync)
            {
                return _terminationTask ?? Task.CompletedTask;
            }
        }
    }

    public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
    {
        add
        {
            ThrowIfTerminating();
            _backend.StatusChanged += value;
        }
        remove => _backend.StatusChanged -= value;
    }

    public event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>?
        AuthorizationChanged
    {
        add
        {
            ThrowIfTerminating();
            _coordinator.AuthorizationChanged += value;
        }
        remove => _coordinator.AuthorizationChanged -= value;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return _backend.StartAsync(cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return _backend.PauseAsync(cancellationToken);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return _backend.ResumeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return _backend.StopAsync(cancellationToken);
    }

    public Task PrepareAsync(
        AppSettings previous,
        AppSettings proposed,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(
            () => _coordinator.PrepareAsync(previous, proposed, cancellationToken));
    }

    public Task CommittedAsync(
        AppSettings previous,
        AppSettings current,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(
            () => _coordinator.CommittedAsync(previous, current, cancellationToken));
    }

    public Task AbortedAsync(
        AppSettings previous,
        AppSettings proposed,
        bool settingsApplied,
        Exception failure,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(() => _coordinator.AbortedAsync(
            previous,
            proposed,
            settingsApplied,
            failure,
            cancellationToken));
    }

    public Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(
            () => _coordinator.UpdateSignalsAsync(signals, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(BeginTermination());
    }

    private Task BeginTermination()
    {
        lock (_terminationSync)
        {
            if (_terminationTask is not null)
            {
                return _terminationTask;
            }

            Interlocked.Exchange(ref _terminating, 1);
            _terminationTask = Task.Run(TerminateCoreAsync);
            return _terminationTask;
        }
    }

    private async Task TerminateCoreAsync()
    {
        var failures = new List<Exception>();
        await CaptureFailureAsync(_coordinator.QuiesceAsync, failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(_backend.RequestStopForShutdownAsync, failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(
                () => _backend.WaitStoppedForShutdownAsync(
                    NativeCaptureBackend.StopTimeoutMilliseconds),
                failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(_backend.StopEventPumpAsync, failures)
            .ConfigureAwait(false);

        try
        {
            var destroyResult = _backend.DestroyForShutdown();
            if (destroyResult != NativeCaptureResult.Ok)
            {
                failures.Add(new NativeCaptureException(
                    destroyResult,
                    "destroy"));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            try
            {
                _backend.StatusChanged -= OnBackendStatusChanged;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                _coordinator.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                _backend.CompleteOwnedShutdown();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "The native capture runtime did not terminate cleanly.",
                failures);
        }
    }

    private static async Task CaptureFailureAsync(
        Func<Task> operation,
        List<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task InvokeCoordinatorAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch
        {
            if (_coordinator.IsFaulted)
            {
                _ = BeginTermination();
            }

            throw;
        }
    }

    private void OnBackendStatusChanged(
        object? sender,
        CaptureStatusChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.Current.State == CaptureState.Faulted)
        {
            _ = BeginTermination();
        }
    }

    private void ThrowIfTerminating()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _terminating) != 0,
            this);
    }
}
