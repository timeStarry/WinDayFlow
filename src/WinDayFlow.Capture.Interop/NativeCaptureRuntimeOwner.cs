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
        if ((_backend.Capabilities & NativeCaptureAbiContract.RuntimeOwnerCapabilities)
            != NativeCaptureAbiContract.RuntimeOwnerCapabilities)
        {
            _backend.DisposeSafelyAfterConstructionFailure();
            throw new NotSupportedException(
                "The native capture runtime owner requires target authorization, a persistence generation barrier, deterministic stop, and command admission support.");
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

    public async ValueTask<ICaptureRuntimeAdmissionStamp?> TryIssueAdmissionAsync(
        CaptureAdmissionOperation operation,
        CancellationToken cancellationToken = default)
    {
        ValidateAdmissionOperation(operation);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminating();

        NativeCaptureCommandAdmissionV1? nativeAdmission = null;
        var snapshot = await InvokeCoordinatorAsync(
                () => _coordinator.TryIssueAdmissionAsync(
                    async (expected, issueCancellationToken) =>
                    {
                        if (Volatile.Read(ref _terminating) != 0)
                        {
                            return false;
                        }

                        nativeAdmission = await _backend
                            .TryIssueCommandAdmissionAsync(
                                operation,
                                expected.RuntimePolicyRevision,
                                expected.PersistenceGeneration,
                                expected.TargetEpoch,
                                issueCancellationToken)
                            .ConfigureAwait(false);
                        return nativeAdmission is not null
                            && Volatile.Read(ref _terminating) == 0;
                    },
                    cancellationToken))
            .ConfigureAwait(false);

        if (snapshot is null
            || nativeAdmission is null
            || Volatile.Read(ref _terminating) != 0)
        {
            return null;
        }

        return new RuntimeAdmissionStamp(
            this,
            operation,
            snapshot.Value,
            nativeAdmission.Value);
    }

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

    public Task StartAsync(
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken = default) =>
        ExecuteAdmissionAsync(
            CaptureAdmissionOperation.Start,
            admissionStamp,
            cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return _backend.PauseAsync(cancellationToken);
    }

    public Task ResumeAsync(
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken = default) =>
        ExecuteAdmissionAsync(
            CaptureAdmissionOperation.Resume,
            admissionStamp,
            cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        await _backend.StopAsync(cancellationToken).ConfigureAwait(false);
        await InvokeCoordinatorAsync(_coordinator.ReconcileAfterStopAsync)
            .ConfigureAwait(false);
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

    private async Task ExecuteAdmissionAsync(
        CaptureAdmissionOperation operation,
        ICaptureRuntimeAdmissionStamp admissionStamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admissionStamp);
        ValidateAdmissionOperation(operation);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminating();

        if (admissionStamp is not RuntimeAdmissionStamp ownedStamp
            || !ReferenceEquals(ownedStamp.Issuer, this))
        {
            throw new CaptureRuntimeAdmissionRejectedException();
        }

        if (!ownedStamp.TryConsume(out var nativeAdmission))
        {
            throw new CaptureRuntimeAdmissionRejectedException();
        }

        if (ownedStamp.Operation != operation
            || !MatchesSnapshot(nativeAdmission, ownedStamp.Snapshot))
        {
            throw new CaptureRuntimeAdmissionRejectedException();
        }

        await InvokeCoordinatorAsync(
                () => _coordinator.ExecuteAdmissionAsync(
                    ownedStamp.Snapshot,
                    async () =>
                    {
                        if (operation == CaptureAdmissionOperation.Start)
                        {
                            await _backend
                                .StartAuthorizedAsync(
                                    nativeAdmission,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await _backend
                                .ResumeAuthorizedAsync(
                                    nativeAdmission,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    },
                    cancellationToken))
            .ConfigureAwait(false);
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

    private async Task<T> InvokeCoordinatorAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
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

    private static void ValidateAdmissionOperation(CaptureAdmissionOperation operation)
    {
        if (operation is not CaptureAdmissionOperation.Start
            and not CaptureAdmissionOperation.Resume)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static bool MatchesSnapshot(
        NativeCaptureCommandAdmissionV1 admission,
        NativeCaptureAdmissionSnapshot snapshot)
    {
        return admission.RuntimePolicyRevision == snapshot.RuntimePolicyRevision
            && admission.PersistenceGeneration == snapshot.PersistenceGeneration
            && admission.TargetEpoch == snapshot.TargetEpoch;
    }

    private sealed class RuntimeAdmissionStamp : ICaptureRuntimeAdmissionStamp
    {
        private NativeCaptureCommandAdmissionV1 _nativeAdmission;
        private int _consumed;

        internal RuntimeAdmissionStamp(
            NativeCaptureRuntimeOwner issuer,
            CaptureAdmissionOperation operation,
            NativeCaptureAdmissionSnapshot snapshot,
            NativeCaptureCommandAdmissionV1 nativeAdmission)
        {
            Issuer = issuer;
            Operation = operation;
            Snapshot = snapshot;
            _nativeAdmission = nativeAdmission;
        }

        public long InvalidationGeneration => Snapshot.InvalidationGeneration;

        internal NativeCaptureRuntimeOwner Issuer { get; }

        internal CaptureAdmissionOperation Operation { get; }

        internal NativeCaptureAdmissionSnapshot Snapshot { get; }

        internal bool TryConsume(out NativeCaptureCommandAdmissionV1 nativeAdmission)
        {
            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            {
                nativeAdmission = default;
                return false;
            }

            nativeAdmission = _nativeAdmission;
            _nativeAdmission = default;
            return true;
        }
    }
}
