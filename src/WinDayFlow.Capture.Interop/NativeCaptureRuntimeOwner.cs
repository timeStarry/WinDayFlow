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
    private readonly SemaphoreSlim _stopReconciliationGate = new(1, 1);
    private readonly INativeCaptureRuntimeBackend _backend;
    private readonly NativeCapturePrivacyCoordinator _coordinator;
    private readonly CaptureDiagnosticLog? _diagnosticLog;
    private readonly CaptureRuleObservationBuffer? _ruleObservations;
    private readonly TimeProvider _timeProvider;
    private AppSettings _currentSettings;
    private NativeCapturePrivacySignals _latestSignals;
    private EventHandler<CaptureChunkCommittedEventArgs>? _chunkCommitted;
    private Task? _terminationTask;
    private ulong _lastReconciledStoppedSequence;
    private int _faultStopScheduled;
    private int _terminating;

    public NativeCaptureRuntimeOwner(
        NativeCaptureConfiguration configuration,
        NativeCapturePrivacyContext initialPrivacyContext,
        AppSettings? initialSettings = null,
        NativeCapturePrivacySignals? initialSignals = null,
        CaptureDiagnosticLog? diagnosticLog = null,
        CaptureRuleObservationBuffer? ruleObservations = null,
        TimeProvider? timeProvider = null)
        : this(
            new NativeCaptureBackend(configuration, initialPrivacyContext),
            initialPrivacyContext,
            initialSettings,
            initialSignals,
            diagnosticLog,
            ruleObservations,
            timeProvider)
    {
    }

    internal NativeCaptureRuntimeOwner(
        INativeCaptureRuntimeBackend backend,
        NativeCapturePrivacyContext initialPrivacyContext,
        AppSettings? initialSettings = null,
        NativeCapturePrivacySignals? initialSignals = null,
        CaptureDiagnosticLog? diagnosticLog = null,
        CaptureRuleObservationBuffer? ruleObservations = null,
        TimeProvider? timeProvider = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _diagnosticLog = diagnosticLog;
        _ruleObservations = ruleObservations;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentSettings = initialSettings ?? AppSettings.Default;
        _latestSignals = initialSignals ?? NativeCapturePrivacySignals.FailClosed;
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
        await ReconcileAfterStopAsync(
                _backend.CurrentStatus.Sequence,
                requireAutomaticProtectionStop: false)
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

    public async Task CommittedAsync(
        AppSettings previous,
        AppSettings current,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        await InvokeCoordinatorAsync(
                () => _coordinator.CommittedAsync(
                    previous,
                    current,
                    cancellationToken))
            .ConfigureAwait(false);
        Volatile.Write(ref _currentSettings, current);
        ObserveRules(Volatile.Read(ref _latestSignals));
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

    public async Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        await InvokeCoordinatorAsync(
                () => _coordinator.UpdateSignalsAsync(signals, cancellationToken))
            .ConfigureAwait(false);
        Volatile.Write(ref _latestSignals, signals);
        ObserveRules(signals);
    }

    public long InvalidatePrivacyObservation()
    {
        ThrowIfTerminating();
        try
        {
            var generation = _coordinator.InvalidatePrivacyObservation();
            Volatile.Write(
                ref _latestSignals,
                NativeCapturePrivacySignals.FailClosed);
            _ruleObservations?.Invalidate(
                _timeProvider.GetUtcNow(),
                Volatile.Read(ref _currentSettings));
            return generation;
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

    public async Task<bool> TryUpdateSignalsAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        var applied = await InvokeCoordinatorAsync(
                () => _coordinator.TryUpdateSignalsAsync(
                    privacyObservationGeneration,
                    signals,
                    cancellationToken))
            .ConfigureAwait(false);
        LogPrivacyDecision(privacyObservationGeneration, applied);
        if (applied)
        {
            Volatile.Write(ref _latestSignals, signals);
            ObserveRules(signals);
        }

        return applied;
    }

    private void LogPrivacyDecision(long generation, bool applied)
    {
        var authorization = _coordinator.LastAppliedAuthorization;
        var context = authorization.PrivacyContext;
        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.PrivacyDecisionEvaluated,
            new(CaptureDiagnosticField.Generation, generation),
            new(CaptureDiagnosticField.Accepted, applied ? 1 : 0),
            new(CaptureDiagnosticField.CaptureAllowed,
                IsCaptureAuthorized ? 1 : 0),
            new(CaptureDiagnosticField.TargetState,
                (long)authorization.Target.State),
            new(CaptureDiagnosticField.ConsentGranted,
                (long)context.ConsentGranted),
            new(CaptureDiagnosticField.SessionUnlocked,
                (long)context.SessionUnlocked),
            new(CaptureDiagnosticField.SecureDesktopClear,
                (long)context.SecureDesktopClear),
            new(CaptureDiagnosticField.RemoteSession,
                (long)context.RemoteSessionAllowed),
            new(CaptureDiagnosticField.PresentationMode,
                (long)context.PresentationAllowed),
            new(CaptureDiagnosticField.ApplicationAllowed,
                (long)context.ApplicationAllowed),
            new(CaptureDiagnosticField.WindowAllowed,
                (long)context.WindowAllowed),
            new(CaptureDiagnosticField.StorageAvailable,
                (long)context.StorageAvailable));
    }

    public async Task<bool> TryRebindTargetAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ThrowIfTerminating();
        var applied = await InvokeCoordinatorAsync(
                () => _coordinator.TryRebindTargetAsync(
                    privacyObservationGeneration,
                    signals,
                    cancellationToken))
            .ConfigureAwait(false);
        if (applied)
        {
            Volatile.Write(ref _latestSignals, signals);
            ObserveRules(signals);
        }

        return applied;
    }

    private void ObserveRules(NativeCapturePrivacySignals signals)
    {
        _ruleObservations?.Observe(
            _timeProvider.GetUtcNow(),
            Volatile.Read(ref _currentSettings),
            signals);
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
        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.BackendStatusChanged,
            new(CaptureDiagnosticField.State, (long)eventArgs.Current.State),
            new(CaptureDiagnosticField.Reason, (long)eventArgs.Current.Reason),
            new(
                CaptureDiagnosticField.Sequence,
                ToDiagnosticInt64(eventArgs.Current.Sequence)),
            new(
                CaptureDiagnosticField.ErrorCode,
                (long)eventArgs.Current.ErrorCode));
        if (eventArgs.Current.State == CaptureState.Faulted)
        {
            if (Volatile.Read(ref _terminating) == 0
                && Interlocked.CompareExchange(
                    ref _faultStopScheduled,
                    1,
                    0) == 0)
            {
                _ = Task.Run(StopFaultedBackendAsync);
            }
            return;
        }

        if (eventArgs.Current.State == CaptureState.Stopped
            && IsAutomaticProtectionReason(eventArgs.Current.Reason)
            && Volatile.Read(ref _terminating) == 0)
        {
            _ = Task.Run(
                () => ObserveAutomaticStopReconciliationAsync(eventArgs.Current));
        }
    }

    private async Task StopFaultedBackendAsync()
    {
        try
        {
            await _backend.StopAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Stopping a faulted capture backend failed: {exception.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _faultStopScheduled, 0);
        }

        if (_backend.CurrentStatus.State == CaptureState.Faulted
            && Volatile.Read(ref _terminating) == 0)
        {
            _ = BeginTermination();
        }
    }

    private async Task ObserveAutomaticStopReconciliationAsync(
        CaptureStatus stoppedStatus)
    {
        try
        {
            await ReconcileAfterStopAsync(
                    stoppedStatus.Sequence,
                    requireAutomaticProtectionStop: true)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Automatic capture stop reconciliation failed: {exception.GetType().Name}");
        }
    }

    private async Task ReconcileAfterStopAsync(
        ulong stoppedSequence,
        bool requireAutomaticProtectionStop)
    {
        await _stopReconciliationGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            _diagnosticLog?.Write(
                CaptureDiagnosticEvent.StopReconciliationStarted,
                new(
                    CaptureDiagnosticField.Sequence,
                    ToDiagnosticInt64(stoppedSequence)),
                new(
                    CaptureDiagnosticField.Automatic,
                    requireAutomaticProtectionStop ? 1 : 0));
            if (Volatile.Read(ref _terminating) != 0)
            {
                LogStopReconciliationCompleted(
                    stoppedSequence,
                    requireAutomaticProtectionStop,
                    CaptureDiagnosticOutcome.Skipped);
                return;
            }

            if (stoppedSequence != 0
                && stoppedSequence <= _lastReconciledStoppedSequence)
            {
                LogStopReconciliationCompleted(
                    stoppedSequence,
                    requireAutomaticProtectionStop,
                    CaptureDiagnosticOutcome.Skipped);
                return;
            }

            if (requireAutomaticProtectionStop)
            {
                var current = _backend.CurrentStatus;
                if (current.State != CaptureState.Stopped
                    || !IsAutomaticProtectionReason(current.Reason)
                    || current.Sequence < stoppedSequence)
                {
                    LogStopReconciliationCompleted(
                        stoppedSequence,
                        requireAutomaticProtectionStop,
                        CaptureDiagnosticOutcome.Skipped);
                    return;
                }

                stoppedSequence = current.Sequence;
            }

            try
            {
                await InvokeCoordinatorAsync(_coordinator.ReconcileAfterStopAsync)
                    .ConfigureAwait(false);
            }
            catch
            {
                LogStopReconciliationCompleted(
                    stoppedSequence,
                    requireAutomaticProtectionStop,
                    CaptureDiagnosticOutcome.Failed);
                throw;
            }

            if (stoppedSequence > _lastReconciledStoppedSequence)
            {
                _lastReconciledStoppedSequence = stoppedSequence;
            }

            LogStopReconciliationCompleted(
                stoppedSequence,
                requireAutomaticProtectionStop,
                CaptureDiagnosticOutcome.Succeeded);
        }
        finally
        {
            _stopReconciliationGate.Release();
        }
    }

    private void LogStopReconciliationCompleted(
        ulong stoppedSequence,
        bool automatic,
        CaptureDiagnosticOutcome outcome)
    {
        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.StopReconciliationCompleted,
            new(
                CaptureDiagnosticField.Sequence,
                ToDiagnosticInt64(stoppedSequence)),
            new(CaptureDiagnosticField.Automatic, automatic ? 1 : 0),
            new(CaptureDiagnosticField.Outcome, (long)outcome));
    }

    private static long ToDiagnosticInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static bool IsAutomaticProtectionReason(CaptureReasonCode reason)
    {
        return reason is
            CaptureReasonCode.SessionLocked or
            CaptureReasonCode.SecureDesktop or
            CaptureReasonCode.SystemSleep or
            CaptureReasonCode.DisplayUnavailable or
            CaptureReasonCode.AccessLost or
            CaptureReasonCode.StorageConstrained or
            CaptureReasonCode.PolicyBlocked or
            CaptureReasonCode.BackendFault;
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
