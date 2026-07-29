using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public sealed class NativeCaptureRuntimeOwner
    : ICaptureBackend,
      ICaptureChunkCommitNotifier,
      IAppSettingsCommitBarrier,
      ICaptureRuntimeAuthorization,
      INativeCapturePrivacySignalSink,
      INativeCapturePrivacySignalSinkTermination,
      INativeCaptureApplicationPrivacyModeSource,
      IAsyncDisposable
{
    private const uint AnalysisChunkDurationMilliseconds = 15 * 60 * 1_000;
    private readonly object _terminationSync = new();
    private readonly INativeCaptureRuntimeBackend _backend;
    private readonly NativeCapturePrivacyCoordinator _coordinator;
    private EventHandler<CaptureChunkCommittedEventArgs>? _chunkCommitted;
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
        if ((_backend.Capabilities
                & NativeCaptureAbiContract.DisplayWideContinuousCapabilities)
            != NativeCaptureAbiContract.DisplayWideContinuousCapabilities)
        {
            _backend.DisposeSafelyAfterConstructionFailure();
            throw new NotSupportedException(
                "The native capture runtime owner requires display-wide continuous authorization, display-scoped target authorization, a persistence generation barrier, deterministic stop, and command admission support.");
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
        _backend.ChunkCommitted += OnBackendChunkCommitted;
    }

    public CaptureStatus CurrentStatus => _backend.CurrentStatus;

    public bool IsCaptureAuthorized =>
        Volatile.Read(ref _terminating) == 0
        && _coordinator.IsCaptureAuthorized;

    public long InvalidationGeneration => _coordinator.InvalidationGeneration;

    public long PrivacyObservationGeneration =>
        _coordinator.PrivacyObservationGeneration;

    public CaptureApplicationPrivacyMode ApplicationPrivacyMode =>
        _coordinator.ApplicationPrivacyMode;

    public CaptureState CurrentCaptureState => _backend.CurrentStatus.State;

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

    bool INativeCapturePrivacySignalSinkTermination.IsTerminationStarted
    {
        get
        {
            lock (_terminationSync)
            {
                return _terminationTask is not null;
            }
        }
    }

    Task INativeCapturePrivacySignalSinkTermination.Termination
    {
        get
        {
            lock (_terminationSync)
            {
                return _terminationTask
                    ?? throw new InvalidOperationException(
                        "The native capture runtime termination has not started.");
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

    public event EventHandler<CaptureChunkCommittedEventArgs>? ChunkCommitted
    {
        add
        {
            ThrowIfTerminating();
            lock (_terminationSync)
            {
                ObjectDisposedException.ThrowIf(_terminating != 0, this);
                _chunkCommitted += value;
            }
        }
        remove
        {
            lock (_terminationSync)
            {
                _chunkCommitted -= value;
            }
        }
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

    public event EventHandler? ApplicationPrivacyModeChanged
    {
        add
        {
            ThrowIfTerminating();
            _coordinator.ApplicationPrivacyModeChanged += value;
        }
        remove => _coordinator.ApplicationPrivacyModeChanged -= value;
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

    public async Task PrepareAsync(
        AppSettings previous,
        AppSettings proposed,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        await InvokeCoordinatorAsync(
                () => _coordinator.PrepareAsync(
                    previous,
                    proposed,
                    cancellationToken))
            .ConfigureAwait(false);
        if (previous.CaptureIntervalSeconds != proposed.CaptureIntervalSeconds)
        {
            await _backend.UpdateTimingAsync(
                    checked((uint)proposed.CaptureIntervalSeconds * 1_000U),
                    AnalysisChunkDurationMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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

    public async Task AbortedAsync(
        AppSettings previous,
        AppSettings proposed,
        bool settingsApplied,
        Exception failure,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        Exception? timingRollbackFailure = null;
        if (!settingsApplied
            && previous.CaptureIntervalSeconds != proposed.CaptureIntervalSeconds)
        {
            try
            {
                await _backend.UpdateTimingAsync(
                        checked((uint)previous.CaptureIntervalSeconds * 1_000U),
                        AnalysisChunkDurationMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                timingRollbackFailure = exception;
            }
        }

        try
        {
            await InvokeCoordinatorAsync(() => _coordinator.AbortedAsync(
                    previous,
                    proposed,
                    settingsApplied,
                    failure,
                    cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception coordinatorFailure) when (timingRollbackFailure is not null)
        {
            throw new AggregateException(
                "Capture timing rollback and privacy abort handling both failed.",
                timingRollbackFailure,
                coordinatorFailure);
        }

        if (timingRollbackFailure is not null)
        {
            throw timingRollbackFailure;
        }
    }

    public Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(
            () => _coordinator.UpdateSignalsAsync(signals, cancellationToken));
    }

    public long InvalidatePrivacyObservation()
    {
        ThrowIfTerminating();
        try
        {
            return _coordinator.InvalidatePrivacyObservation();
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

    public Task ApplyPrivacyInvalidationAsync(
        long privacyObservationGeneration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(
            () => _coordinator.ApplyPrivacyInvalidationAsync(
                privacyObservationGeneration,
                cancellationToken));
    }

    public Task<bool> TryUpdateSignalsAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        return InvokeCoordinatorAsync(
            () => _coordinator.TryUpdateSignalsAsync(
                privacyObservationGeneration,
                signals,
                cancellationToken));
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
        await CaptureQuiesceFailureAsync(failures)
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
                _backend.ChunkCommitted -= OnBackendChunkCommitted;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                lock (_terminationSync)
                {
                    _chunkCommitted = null;
                }
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

    private async Task CaptureQuiesceFailureAsync(List<Exception> failures)
    {
        try
        {
            await _coordinator.QuiesceAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CaptureState state;
            try
            {
                state = _backend.CurrentStatus.State;
            }
            catch (Exception statusFailure)
            {
                failures.Add(exception);
                failures.Add(statusFailure);
                return;
            }

            var failure = NormalizeTerminalQuiesceFailure(exception, state);
            if (failure is not null)
            {
                failures.Add(failure);
            }
        }
    }

    private static Exception? NormalizeTerminalQuiesceFailure(
        Exception failure,
        CaptureState state)
    {
        if (state is not CaptureState.Faulted
            and not CaptureState.Stopping
            and not CaptureState.Stopped)
        {
            return failure;
        }

        if (IsExpectedTerminalAuthorizationRace(failure))
        {
            return null;
        }

        if (failure is not AggregateException aggregate)
        {
            return failure;
        }

        var remaining = aggregate.InnerExceptions
            .Where(static exception =>
                !IsExpectedTerminalAuthorizationRace(exception))
            .ToList();
        return remaining.Count switch
        {
            0 => null,
            1 => remaining[0],
            _ when remaining.Count == aggregate.InnerExceptions.Count => failure,
            _ => new AggregateException(aggregate.Message, remaining),
        };
    }

    private static bool IsExpectedTerminalAuthorizationRace(Exception failure)
    {
        return failure is NativeCaptureException nativeFailure
            && nativeFailure.ResultCode == (int)NativeCaptureResult.InvalidState
            && nativeFailure.Operation is "update_runtime_authorization"
                or "revoke_runtime_authorization";
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

    private void OnBackendChunkCommitted(
        object? sender,
        CaptureChunkCommittedEventArgs eventArgs)
    {
        _ = sender;
        EventHandler<CaptureChunkCommittedEventArgs>? handler;
        lock (_terminationSync)
        {
            if (_terminating != 0)
            {
                return;
            }

            handler = _chunkCommitted;
        }

        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<CaptureChunkCommittedEventArgs>)subscriber)(
                    this,
                    eventArgs);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Capture chunk wake-hint subscriber failed: {exception}");
            }
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
