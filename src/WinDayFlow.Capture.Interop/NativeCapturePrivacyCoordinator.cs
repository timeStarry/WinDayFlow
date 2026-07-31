using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public interface INativeCaptureAuthorizationTarget
{
    long InvalidateRuntimeAuthorization();

    Task<NativeCaptureAuthorizationUpdateResult> UpdateRuntimeAuthorizationAsync(
        NativeCaptureRuntimeAuthorization authorization,
        long expectedCallbackInvalidationGeneration,
        CancellationToken cancellationToken = default);

    Task<ulong> RevokeRuntimeAuthorizationAsync(
        CancellationToken cancellationToken = default);
}

public enum NativeCaptureAuthorizationUpdateOutcome
{
    Applied = 1,
    SupersededBeforeCommit = 2,
    AppliedThenSuperseded = 3,
}

public readonly record struct NativeCaptureAuthorizationUpdateResult
{
    public NativeCaptureAuthorizationUpdateResult(
        ulong persistenceGeneration,
        NativeCaptureAuthorizationUpdateOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (outcome != NativeCaptureAuthorizationUpdateOutcome
                .SupersededBeforeCommit
            && persistenceGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(persistenceGeneration),
                persistenceGeneration,
                "A committed native authorization update requires a positive persistence generation.");
        }

        PersistenceGeneration = persistenceGeneration;
        Outcome = outcome;
    }

    public ulong PersistenceGeneration { get; }

    public NativeCaptureAuthorizationUpdateOutcome Outcome { get; }

    public bool WasCommitted =>
        Outcome != NativeCaptureAuthorizationUpdateOutcome.SupersededBeforeCommit;

    public bool WasSuperseded =>
        Outcome != NativeCaptureAuthorizationUpdateOutcome.Applied;
}

internal readonly record struct NativeCaptureAdmissionSnapshot(
    long InvalidationGeneration,
    ulong RuntimePolicyRevision,
    ulong PersistenceGeneration,
    ulong TargetEpoch);

public sealed class NativeCapturePrivacyCoordinator
    : IAppSettingsCommitBarrier, INativeCapturePrivacySignalSink, IDisposable
{
    private readonly INativeCaptureAuthorizationTarget _target;
    private readonly object _disposeSync = new();
    private readonly object _quiesceSync = new();
    private readonly object _settingsCommitSync = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Channel<AuthorizationNotification> _authorizationNotifications =
        Channel.CreateUnbounded<AuthorizationNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private AppSettings _committedSettings;
    private NativeCapturePrivacySignals _signals;
    private NativeCaptureRuntimeAuthorization _lastApplied;
    private ulong _lastPersistenceGeneration;
    private Exception? _fatalFailure;
    private AppSettings? _preparedPrevious;
    private AppSettings? _preparedProposed;
    private int _forcedBlock = 1;
    private int _captureAuthorized;
    private long _invalidationGeneration;
    private long _privacyObservationGeneration;
    private long _nativeCallbackInvalidationGeneration;
    private PrivacyObservationPhase _privacyObservationPhase =
        PrivacyObservationPhase.Legacy;
    private EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? _authorizationChanged;
    private Task? _quiesceTask;
    private int _quiescing;
    private bool _disposed;

    public NativeCapturePrivacyCoordinator(
        INativeCaptureAuthorizationTarget target,
        NativeCapturePrivacyContext initialContext,
        AppSettings? initialSettings = null,
        NativeCapturePrivacySignals? initialSignals = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(initialContext);
        _lastApplied = new NativeCaptureRuntimeAuthorization(
            initialContext,
            NativeCaptureTargetIdentity.Unknown);
        if (_lastApplied.PrivacyContext.ConsentGranted == NativeCapturePolicyDecision.Allow)
        {
            throw new ArgumentException(
                "The native privacy coordinator requires a fail-closed initial context.",
                nameof(initialContext));
        }

        _committedSettings = initialSettings ?? AppSettings.Default;
        _signals = initialSignals ?? NativeCapturePrivacySignals.FailClosed;
        _ = Task.Run(DispatchAuthorizationNotificationsAsync);
    }

    public bool IsCaptureAuthorized =>
        Volatile.Read(ref _captureAuthorized) != 0;

    public long InvalidationGeneration =>
        Volatile.Read(ref _invalidationGeneration);

    public long PrivacyObservationGeneration =>
        Volatile.Read(ref _privacyObservationGeneration);

    public NativeCapturePrivacyContext LastAppliedContext =>
        Volatile.Read(ref _lastApplied).PrivacyContext;

    public NativeCaptureRuntimeAuthorization LastAppliedAuthorization =>
        Volatile.Read(ref _lastApplied);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "This instance member is exposed through the runtime privacy-mode contract.")]
    public CaptureApplicationPrivacyMode ApplicationPrivacyMode =>
        CaptureApplicationPrivacyMode.AllowAllApplications;

    public ulong LastPersistenceGeneration =>
        Volatile.Read(ref _lastPersistenceGeneration);

    public bool IsFaulted => Volatile.Read(ref _fatalFailure) is not null;

    public event EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? AuthorizationChanged
    {
        add
        {
            lock (_disposeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _authorizationChanged += value;
            }
        }
        remove
        {
            lock (_disposeSync)
            {
                _authorizationChanged -= value;
            }
        }
    }

    internal event EventHandler? ApplicationPrivacyModeChanged;

    internal async Task<NativeCaptureAdmissionSnapshot?> TryIssueAdmissionAsync(
        Func<NativeCaptureAdmissionSnapshot, CancellationToken, Task<bool>> issueNative,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issueNative);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            var snapshot = TryCreateAdmissionSnapshot();
            if (snapshot is null)
            {
                return null;
            }

            bool issued;
            try
            {
                issued = await issueNative(snapshot.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                MarkFatal(exception);
                throw;
            }

            return issued && IsAdmissionSnapshotCurrent(snapshot.Value)
                ? snapshot
                : null;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    internal async Task ExecuteAdmissionAsync(
        NativeCaptureAdmissionSnapshot expected,
        Func<Task> executeNative,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executeNative);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUsable();
            if (!IsAdmissionSnapshotCurrent(expected))
            {
                throw new CaptureRuntimeAdmissionRejectedException();
            }

            try
            {
                await executeNative().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                CaptureRuntimeAdmissionRejectedException or NotSupportedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                MarkFatal(exception);
                throw;
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    internal async Task ReconcileAfterStopAsync()
    {
        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            SetCaptureAuthorized(authorized: false);

            var forceNativeUpdate = true;
            PrivacyObservationSnapshot observation;
            NativeAuthorizationApplication application;
            do
            {
                observation = CapturePrivacyObservation();
                application = await ApplyUnderGateAsync(
                        Volatile.Read(ref _committedSettings),
                        observation,
                        forceBlock: Volatile.Read(ref _forcedBlock) != 0,
                        cancellationToken: CancellationToken.None,
                        forceNativeUpdate: forceNativeUpdate)
                    .ConfigureAwait(false);
                forceNativeUpdate = application.WasSuperseded;
            }
            while (application.WasSuperseded
                || !IsPrivacyObservationCurrent(observation));

            var published = PublishAuthorization(application.Context, observation);
            if (!published)
            {
                await CompensateStaleAllowUnderGateAsync(application.Context)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public Task QuiesceAsync()
    {
        TaskCompletionSource? completion = null;
        Task task;
        lock (_quiesceSync)
        {
            if (_quiesceTask is not null)
            {
                return _quiesceTask;
            }

            ThrowIfDisposed();
            Interlocked.Exchange(ref _quiescing, 1);
            Interlocked.Exchange(ref _forcedBlock, 1);
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            _quiesceTask = task;
        }

        SetCaptureAuthorized(authorized: false);
        _ = CompleteQuiesceAsync(completion);
        return task;
    }

    public async Task PrepareAsync(
        AppSettings previous,
        AppSettings proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(proposed);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var restrictive = IsRestrictiveChange(previous, proposed);
        ThrowIfUsable();
        BeginPreparedSettingsCommit(previous, proposed);
        if (!restrictive)
        {
            return;
        }

        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            Interlocked.Exchange(ref _forcedBlock, 1);
            SetCaptureAuthorized(authorized: false);
            PrivacyObservationSnapshot observation;
            NativeAuthorizationApplication application;
            do
            {
                observation = CapturePrivacyObservation();
                application = await ApplyUnderGateAsync(
                        proposed,
                        observation,
                        forceBlock: true,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            while (application.WasSuperseded
                || !IsPrivacyObservationCurrent(observation));

            PublishAuthorization(application.Context, observation);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task CommittedAsync(
        AppSettings previous,
        AppSettings current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var authorizingTransition = !HasUserAuthorization(previous)
            && HasUserAuthorization(current);
        EnsurePreparedSettingsCommit(previous, current);
        try
        {
            await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUsable();
                Volatile.Write(ref _committedSettings, current);
                var forceBlock = Volatile.Read(ref _forcedBlock) != 0
                    && !authorizingTransition;
                PrivacyObservationSnapshot observation;
                NativeAuthorizationApplication application;
                do
                {
                    observation = CapturePrivacyObservation();
                    application = await ApplyUnderGateAsync(
                            current,
                            observation,
                            forceBlock,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                while (application.WasSuperseded
                    || !IsPrivacyObservationCurrent(observation));

                if (authorizingTransition)
                {
                    ClearForcedBlockIfActive();
                }

                var published = PublishAuthorization(
                    application.Context,
                    observation);
                if (!published)
                {
                    await CompensateStaleAllowUnderGateAsync(application.Context)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _applyGate.Release();
            }
        }
        finally
        {
            CompletePreparedSettingsCommit();
        }

        }

    public async Task AbortedAsync(
        AppSettings previous,
        AppSettings proposed,
        bool settingsApplied,
        Exception failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(failure);
        _ = cancellationToken;
        try
        {
            if (!settingsApplied)
            {
                return;
            }

            await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Volatile.Write(ref _committedSettings, proposed);
                Interlocked.Exchange(ref _forcedBlock, 1);
                SetCaptureAuthorized(authorized: false);
            }
            finally
            {
                _applyGate.Release();
            }
        }
        finally
        {
            CompletePreparedSettingsCommit();
        }
    }

    public async Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signals);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var privacyObservationGeneration = PrivacyObservationGeneration;
        _ = await TryUpdateSignalsCoreAsync(
                privacyObservationGeneration,
                signals,
                generationBound: false)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateSignalsAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ValidatePrivacyObservationGeneration(privacyObservationGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (!IsPrivacyObservationGenerationCurrent(privacyObservationGeneration))
        {
            return false;
        }

        return await TryUpdateSignalsCoreAsync(
                privacyObservationGeneration,
                signals,
                generationBound: true)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryRebindTargetAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ValidatePrivacyObservationGeneration(privacyObservationGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (!IsPrivacyObservationGenerationCurrent(privacyObservationGeneration))
        {
            return false;
        }

        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            lock (_disposeSync)
            {
                var current = Volatile.Read(ref _signals);
                if (_disposed
                    || privacyObservationGeneration != PrivacyObservationGeneration
                    || _privacyObservationPhase != PrivacyObservationPhase.Published
                    || Volatile.Read(ref _captureAuthorized) == 0
                    || Volatile.Read(ref _forcedBlock) != 0
                    || current.Target.State
                        != NativeCaptureTargetIdentityState.Present
                    || signals.Target.State
                        != NativeCaptureTargetIdentityState.Present
                    || !HasSameHardGateSignals(current, signals))
                {
                    return false;
                }

                Volatile.Write(ref _signals, signals);
            }

            var observation = CapturePrivacyObservation();
            if (observation.Generation != privacyObservationGeneration
                || observation.Signals != signals)
            {
                return false;
            }

            var application = await ApplyUnderGateAsync(
                    Volatile.Read(ref _committedSettings),
                    observation,
                    forceBlock: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (application.WasSuperseded
                || !IsPrivacyObservationCurrent(observation))
            {
                if (application.WasCommitted
                    && IsFullyAllowed(application.Context))
                {
                    await CompensateStaleAllowUnderGateAsync(
                            application.Context)
                        .ConfigureAwait(false);
                }

                return false;
            }

            return PublishAuthorization(application.Context, observation);
        }
        finally
        {
            _applyGate.Release();
        }
    }
    public long InvalidatePrivacyObservation()
    {
        InvalidOperationException? exhausted = null;
        long nextGeneration;
        lock (_disposeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _signals, NativeCapturePrivacySignals.FailClosed);
            var currentGeneration = PrivacyObservationGeneration;
            if (currentGeneration == long.MaxValue)
            {
                exhausted = new InvalidOperationException(
                    "The privacy observation generation has been exhausted; the native handle must be recreated.");
                Volatile.Write(ref _fatalFailure, exhausted);
                Interlocked.Exchange(ref _forcedBlock, 1);
                nextGeneration = currentGeneration;
            }
            else
            {
                nextGeneration = currentGeneration + 1;
                Volatile.Write(
                    ref _privacyObservationGeneration,
                    nextGeneration);
            }

            _privacyObservationPhase = PrivacyObservationPhase.Invalidated;

            var notification = SetCaptureAuthorizedUnderLock(authorized: false);
            exhausted ??= notification.GenerationFailure;
            RaiseAuthorizationChanged(
                notification.Handler,
                notification.EventArgs);

            try
            {
                var nativeGeneration =
                    _target.InvalidateRuntimeAuthorization();
                if (nativeGeneration <= 0
                    || nativeGeneration
                        <= _nativeCallbackInvalidationGeneration)
                {
                    throw new InvalidOperationException(
                        "The native callback invalidation generation did not advance.");
                }

                Volatile.Write(
                    ref _nativeCallbackInvalidationGeneration,
                    nativeGeneration);
            }
            catch (Exception exception)
            {
                exhausted = exhausted is null
                    ? exception as InvalidOperationException
                        ?? new InvalidOperationException(
                            "The native callback authorization invalidation failed.",
                            exception)
                    : new InvalidOperationException(
                        exhausted.Message,
                        new AggregateException(exhausted, exception));
                Volatile.Write(ref _fatalFailure, exhausted);
                Interlocked.Exchange(ref _forcedBlock, 1);
            }
        }

        if (exhausted is not null)
        {
            throw exhausted;
        }

        return nextGeneration;
    }

    public async Task ApplyPrivacyInvalidationAsync(
        long privacyObservationGeneration,
        CancellationToken cancellationToken = default)
    {
        ValidatePrivacyObservationGeneration(privacyObservationGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (!IsPrivacyObservationGenerationCurrent(privacyObservationGeneration))
        {
            return;
        }

        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            if (!TryBeginPrivacyInvalidationBarrierUnderGate(
                    privacyObservationGeneration))
            {
                return;
            }

            var observation = CapturePrivacyObservation();
            if (observation.Generation != privacyObservationGeneration)
            {
                return;
            }

            var application = await ApplyUnderGateAsync(
                    Volatile.Read(ref _committedSettings),
                    observation with
                    {
                        Signals = NativeCapturePrivacySignals.FailClosed,
                    },
                    forceBlock: true,
                    CancellationToken.None,
                    forceNativeUpdate: true)
                .ConfigureAwait(false);
            if (application.WasSuperseded)
            {
                return;
            }

            if (!TryCompletePrivacyInvalidationBarrierUnderGate(
                    privacyObservationGeneration))
            {
                return;
            }

            PublishAuthorization(
                application.Context,
                observation with
                {
                    Signals = NativeCapturePrivacySignals.FailClosed,
                });
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task<bool> TryUpdateSignalsCoreAsync(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        bool generationBound)
    {
        if (!TryPublishSignalsForGeneration(
                privacyObservationGeneration,
                signals,
                generationBound))
        {
            return false;
        }

        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            if (!IsPrivacyObservationGenerationCurrent(
                    privacyObservationGeneration)
                || signals != Volatile.Read(ref _signals))
            {
                return false;
            }

            if (!IsPrivacyObservationGenerationCurrent(
                    privacyObservationGeneration))
            {
                return false;
            }

            var observation = CapturePrivacyObservation();
            if (observation.Generation != privacyObservationGeneration
                || observation.Signals != signals)
            {
                return false;
            }

            var application = await ApplyUnderGateAsync(
                    Volatile.Read(ref _committedSettings),
                    observation,
                    forceBlock: Volatile.Read(ref _forcedBlock) != 0,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (application.WasSuperseded)
            {
                if (application.WasCommitted
                    && IsFullyAllowed(application.Context))
                {
                    await CompensateStaleAllowUnderGateAsync(
                            application.Context)
                        .ConfigureAwait(false);
                }

                return false;
            }

            if (!IsPrivacyObservationGenerationCurrent(
                    privacyObservationGeneration))
            {
                await CompensateStaleAllowUnderGateAsync(application.Context)
                    .ConfigureAwait(false);
                return false;
            }

            var published = PublishAuthorization(
                application.Context,
                observation);
            if (!published)
            {
                await CompensateStaleAllowUnderGateAsync(application.Context)
                    .ConfigureAwait(false);
                return false;
            }

            return true;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private bool TryPublishSignalsForGeneration(
        long privacyObservationGeneration,
        NativeCapturePrivacySignals signals,
        bool generationBound)
    {
        var preview = Compose(
            Volatile.Read(ref _committedSettings),
            signals,
            forceBlock: Volatile.Read(ref _forcedBlock) != 0,
            LastAppliedContext.RuntimePolicyRevision,
            Volatile.Read(ref _lastApplied).Target);
        lock (_disposeSync)
        {
            if (_disposed
                || privacyObservationGeneration != PrivacyObservationGeneration)
            {
                return false;
            }

            if (generationBound)
            {
                if (privacyObservationGeneration > 0
                    && _privacyObservationPhase
                        != PrivacyObservationPhase.BarrierApplied)
                {
                    return false;
                }

                if (privacyObservationGeneration > 0)
                {
                    _privacyObservationPhase = PrivacyObservationPhase.Published;
                }
            }
            else if (privacyObservationGeneration != 0
                     || _privacyObservationPhase != PrivacyObservationPhase.Legacy)
            {
                return false;
            }

            Volatile.Write(ref _signals, signals);
            if (!IsFullyAllowed(preview.PrivacyContext)
                || !HasSameDecisions(Volatile.Read(ref _lastApplied), preview))
            {
                var notification = SetCaptureAuthorizedUnderLock(
                    authorized: false);
                RaiseAuthorizationChanged(
                    notification.Handler,
                    notification.EventArgs);
            }
        }

        return true;
    }

    private bool TryBeginPrivacyInvalidationBarrierUnderGate(
        long privacyObservationGeneration)
    {
        var signals = NativeCapturePrivacySignals.FailClosed;
        var preview = Compose(
            Volatile.Read(ref _committedSettings),
            signals,
            forceBlock: true,
            LastAppliedContext.RuntimePolicyRevision,
            Volatile.Read(ref _lastApplied).Target);
        lock (_disposeSync)
        {
            if (_disposed
                || privacyObservationGeneration != PrivacyObservationGeneration)
            {
                return false;
            }

            if (privacyObservationGeneration > 0
                && _privacyObservationPhase != PrivacyObservationPhase.Invalidated)
            {
                return false;
            }

            Volatile.Write(ref _signals, signals);
            if (!IsFullyAllowed(preview.PrivacyContext)
                || !HasSameDecisions(Volatile.Read(ref _lastApplied), preview))
            {
                var notification = SetCaptureAuthorizedUnderLock(
                    authorized: false);
                RaiseAuthorizationChanged(
                    notification.Handler,
                    notification.EventArgs);
            }
        }

        return true;
    }

    private bool TryCompletePrivacyInvalidationBarrierUnderGate(
        long privacyObservationGeneration)
    {
        lock (_disposeSync)
        {
            if (_disposed
                || privacyObservationGeneration != PrivacyObservationGeneration)
            {
                return false;
            }

            if (privacyObservationGeneration > 0)
            {
                if (_privacyObservationPhase != PrivacyObservationPhase.Invalidated)
                {
                    return false;
                }

                _privacyObservationPhase = PrivacyObservationPhase.BarrierApplied;
            }

            return true;
        }
    }

    private async Task CompensateStaleAllowUnderGateAsync(
        NativeCapturePrivacyContext applied)
    {
        if (!IsFullyAllowed(applied))
        {
            return;
        }

        SetCaptureAuthorized(authorized: false);
        while (true)
        {
            var observation = CapturePrivacyObservation();
            var compensation = await ApplyUnderGateAsync(
                    Volatile.Read(ref _committedSettings),
                    observation with
                    {
                        Signals = NativeCapturePrivacySignals.FailClosed,
                    },
                    forceBlock: true,
                    CancellationToken.None,
                    forceNativeUpdate: true)
                .ConfigureAwait(false);
            if (!compensation.WasSuperseded
                && IsPrivacyObservationCurrent(observation))
            {
                return;
            }

            if (!IsPrivacyObservationGenerationCurrent(observation.Generation))
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (_disposeSync)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Exchange(ref _forcedBlock, 1);
            Volatile.Write(ref _disposed, true);
            var notification = SetCaptureAuthorizedUnderLock(authorized: false);
            RaiseAuthorizationChanged(
                notification.Handler,
                notification.EventArgs);
            _authorizationChanged = null;
        }

        _authorizationNotifications.Writer.TryComplete();
        GC.SuppressFinalize(this);
    }

    private async Task<NativeAuthorizationApplication> ApplyUnderGateAsync(
        AppSettings settings,
        PrivacyObservationSnapshot observation,
        bool forceBlock,
        CancellationToken cancellationToken,
        bool forceNativeUpdate = false)
    {
        var preview = Compose(
            settings,
            observation.Signals,
            forceBlock,
            _lastApplied.RuntimePolicyRevision,
            _lastApplied.Target);
        if (!forceNativeUpdate && HasSameDecisions(_lastApplied, preview))
        {
            return new NativeAuthorizationApplication(
                _lastApplied.PrivacyContext,
                WasCommitted: true,
                WasSuperseded: false);
        }

        if (_lastApplied.RuntimePolicyRevision == ulong.MaxValue)
        {
            var exhausted = new InvalidOperationException(
                "The native capture runtime policy revision has been exhausted; the native handle must be recreated.");
            MarkFatal(exhausted);
            throw exhausted;
        }

        var next = Compose(
            settings,
            observation.Signals,
            forceBlock,
            _lastApplied.RuntimePolicyRevision + 1,
            _lastApplied.Target);
        NativeCaptureAuthorizationUpdateResult update;
        try
        {
            update = await _target
                .UpdateRuntimeAuthorizationAsync(
                    next,
                    observation.NativeCallbackInvalidationGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            MarkFatal(exception);
            throw;
        }

        if (!update.WasCommitted)
        {
            ThrowIfSupersededObservationRemainedCurrent(
                update,
                observation);
            return new NativeAuthorizationApplication(
                _lastApplied.PrivacyContext,
                WasCommitted: false,
                WasSuperseded: true);
        }

        ValidateAdvancedPersistenceGeneration(
            update.PersistenceGeneration,
            "updating runtime authorization");
        Volatile.Write(
            ref _lastPersistenceGeneration,
            update.PersistenceGeneration);
        Volatile.Write(ref _lastApplied, next);
        ThrowIfSupersededObservationRemainedCurrent(
            update,
            observation);
        return new NativeAuthorizationApplication(
            next.PrivacyContext,
            WasCommitted: true,
            update.WasSuperseded);
    }

    private void ThrowIfSupersededObservationRemainedCurrent(
        NativeCaptureAuthorizationUpdateResult update,
        PrivacyObservationSnapshot observation)
    {
        if (!update.WasSuperseded
            || !IsPrivacyObservationCurrent(observation))
        {
            return;
        }

        var failure = new InvalidOperationException(
            "The native authorization was superseded without a matching privacy observation invalidation.");
        MarkFatal(failure);
        throw failure;
    }

    private NativeCaptureAdmissionSnapshot? TryCreateAdmissionSnapshot()
    {
        var authorization = Volatile.Read(ref _lastApplied);
        var persistenceGeneration = LastPersistenceGeneration;
        var invalidationGeneration = InvalidationGeneration;
        if (!IsAdmissionStateCurrent(
                authorization,
                persistenceGeneration,
                invalidationGeneration))
        {
            return null;
        }

        return new NativeCaptureAdmissionSnapshot(
            invalidationGeneration,
            authorization.RuntimePolicyRevision,
            persistenceGeneration,
            authorization.Target.TargetEpoch);
    }

    private bool IsAdmissionSnapshotCurrent(NativeCaptureAdmissionSnapshot expected)
    {
        var authorization = Volatile.Read(ref _lastApplied);
        return IsAdmissionStateCurrent(
                authorization,
                LastPersistenceGeneration,
                InvalidationGeneration)
            && expected.InvalidationGeneration == InvalidationGeneration
            && expected.RuntimePolicyRevision == authorization.RuntimePolicyRevision
            && expected.PersistenceGeneration == LastPersistenceGeneration
            && expected.TargetEpoch == authorization.Target.TargetEpoch;
    }

    private bool IsAdmissionStateCurrent(
        NativeCaptureRuntimeAuthorization authorization,
        ulong persistenceGeneration,
        long invalidationGeneration)
    {
        return Volatile.Read(ref _captureAuthorized) != 0
            && Volatile.Read(ref _forcedBlock) == 0
            && Volatile.Read(ref _fatalFailure) is null
            && Volatile.Read(ref _quiescing) == 0
            && !Volatile.Read(ref _disposed)
            && invalidationGeneration >= 0
            && persistenceGeneration != 0
            && IsFullyAllowed(authorization.PrivacyContext)
            && authorization.Target.State == NativeCaptureTargetIdentityState.Present
            && authorization.Target.TargetEpoch != 0;
    }

    private PrivacyObservationSnapshot CapturePrivacyObservation()
    {
        lock (_disposeSync)
        {
            return new PrivacyObservationSnapshot(
                PrivacyObservationGeneration,
                Volatile.Read(ref _signals),
                Volatile.Read(ref _nativeCallbackInvalidationGeneration));
        }
    }

    private bool IsPrivacyObservationCurrent(
        PrivacyObservationSnapshot observation)
    {
        lock (_disposeSync)
        {
            return observation.Generation == PrivacyObservationGeneration
                && observation.Signals == Volatile.Read(ref _signals)
                && observation.NativeCallbackInvalidationGeneration
                    == Volatile.Read(
                        ref _nativeCallbackInvalidationGeneration);
        }
    }

    private bool IsPrivacyObservationGenerationCurrent(
        long privacyObservationGeneration)
    {
        return privacyObservationGeneration == PrivacyObservationGeneration;
    }

    private static NativeCaptureRuntimeAuthorization Compose(
        AppSettings settings,
        NativeCapturePrivacySignals signals,
        bool forceBlock,
        ulong runtimePolicyRevision,
        NativeCaptureTargetIdentity previousTarget)
    {
        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            signals,
            runtimePolicyRevision);
        var target = SelectAuthorizationTarget(
            CaptureApplicationPrivacyMode.AllowAllApplications,
            signals.Target,
            previousTarget);
        if (NativeCaptureRuntimeAuthorization.IsFullyAllowed(context)
            && target.State != NativeCaptureTargetIdentityState.Present)
        {
            context = new NativeCapturePrivacyContext(
                context.ConsentGranted,
                context.SessionUnlocked,
                context.SecureDesktopClear,
                context.RemoteSessionAllowed,
                context.PresentationAllowed,
                NativeCapturePolicyDecision.Unknown,
                context.WindowAllowed,
                context.StorageAvailable,
                context.RuntimePolicyRevision);
        }

        if (!forceBlock || !IsFullyAllowed(context))
        {
            return new NativeCaptureRuntimeAuthorization(
                context,
                NativeCaptureRuntimeAuthorization.IsFullyAllowed(context)
                    ? target
                    : NativeCaptureTargetIdentity.Unknown);
        }

        return new NativeCaptureRuntimeAuthorization(
            new NativeCapturePrivacyContext(
                context.ConsentGranted,
                context.SessionUnlocked,
                context.SecureDesktopClear,
                context.RemoteSessionAllowed,
                context.PresentationAllowed,
                NativeCapturePolicyDecision.Unknown,
                context.WindowAllowed,
                context.StorageAvailable,
                context.RuntimePolicyRevision),
            NativeCaptureTargetIdentity.Unknown);
    }

    private static NativeCaptureTargetIdentity SelectAuthorizationTarget(
        CaptureApplicationPrivacyMode mode,
        NativeCaptureTargetIdentity observedTarget,
        NativeCaptureTargetIdentity previousTarget)
    {
        if (mode != CaptureApplicationPrivacyMode.AllowAllApplications
            || observedTarget.State != NativeCaptureTargetIdentityState.Present)
        {
            return observedTarget;
        }

        if (previousTarget.State == NativeCaptureTargetIdentityState.Present
            && previousTarget.Scope == NativeCaptureAuthorizationScope.DisplayWide
            && previousTarget.DisplayMonitorHandle
                == observedTarget.DisplayMonitorHandle
            && string.Equals(
                previousTarget.DisplayDeviceKey,
                observedTarget.DisplayDeviceKey,
                StringComparison.OrdinalIgnoreCase))
        {
            return previousTarget;
        }

        return NativeCaptureTargetIdentity.DisplayWide(
            observedTarget.TargetEpoch,
            observedTarget.DisplayMonitorHandle,
            observedTarget.DisplayDeviceKey!);
    }

    private void RaiseApplicationPrivacyModeChanged()
    {
        var handler = ApplicationPrivacyModeChanged;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"A capture application privacy mode subscriber failed: {exception}");
            }
        }
    }

    private static bool IsRestrictiveChange(
        AppSettings previous,
        AppSettings proposed)
    {
        return HasUserAuthorization(previous) && !HasUserAuthorization(proposed);
    }

    private static bool HasUserAuthorization(AppSettings settings)
    {
        return settings.CaptureIntent != CaptureIntent.Stopped
            && settings.RecordingConsent is { } consent
            && consent.PolicyVersion == AppSettingsService.CurrentRecordingConsentVersion;
    }

    private static bool HasSameHardGateSignals(
        NativeCapturePrivacySignals left,
        NativeCapturePrivacySignals right)
    {
        return left.SessionUnlocked == right.SessionUnlocked
            && left.SecureDesktopClear == right.SecureDesktopClear
            && left.RemoteSession == right.RemoteSession
            && left.PresentationMode == right.PresentationMode
            && left.ApplicationAllowed == right.ApplicationAllowed
            && left.WindowAllowed == right.WindowAllowed
            && left.StorageAvailable == right.StorageAvailable;
    }
    private static bool HasSameDecisions(
        NativeCaptureRuntimeAuthorization left,
        NativeCaptureRuntimeAuthorization right)
    {
        return left.PrivacyContext.ConsentGranted == right.PrivacyContext.ConsentGranted
            && left.PrivacyContext.SessionUnlocked == right.PrivacyContext.SessionUnlocked
            && left.PrivacyContext.SecureDesktopClear == right.PrivacyContext.SecureDesktopClear
            && left.PrivacyContext.RemoteSessionAllowed == right.PrivacyContext.RemoteSessionAllowed
            && left.PrivacyContext.PresentationAllowed == right.PrivacyContext.PresentationAllowed
            && left.PrivacyContext.ApplicationAllowed == right.PrivacyContext.ApplicationAllowed
            && left.PrivacyContext.WindowAllowed == right.PrivacyContext.WindowAllowed
            && left.PrivacyContext.StorageAvailable == right.PrivacyContext.StorageAvailable
            && left.Target.Equals(right.Target);
    }

    private static bool IsFullyAllowed(NativeCapturePrivacyContext context)
    {
        return context.ConsentGranted == NativeCapturePolicyDecision.Allow
            && context.SessionUnlocked == NativeCapturePolicyDecision.Allow
            && context.SecureDesktopClear == NativeCapturePolicyDecision.Allow
            && context.RemoteSessionAllowed == NativeCapturePolicyDecision.Allow
            && context.PresentationAllowed == NativeCapturePolicyDecision.Allow
            && context.ApplicationAllowed == NativeCapturePolicyDecision.Allow
            && context.WindowAllowed == NativeCapturePolicyDecision.Allow
            && context.StorageAvailable == NativeCapturePolicyDecision.Allow;
    }

    private bool PublishAuthorization(
        NativeCapturePrivacyContext context,
        PrivacyObservationSnapshot observation)
    {
        bool observationCurrent;
        lock (_disposeSync)
        {
            observationCurrent = !_disposed
                && observation.Generation == PrivacyObservationGeneration
                && observation.Signals == Volatile.Read(ref _signals)
                && observation.NativeCallbackInvalidationGeneration
                    == Volatile.Read(
                        ref _nativeCallbackInvalidationGeneration);
            var authorized = observationCurrent
                && Volatile.Read(ref _forcedBlock) == 0
                && Volatile.Read(ref _fatalFailure) is null
                && Volatile.Read(ref _quiescing) == 0
                && IsFullyAllowed(context);
            var notification = SetCaptureAuthorizedUnderLock(authorized);
            RaiseAuthorizationChanged(
                notification.Handler,
                notification.EventArgs);
        }

        return observationCurrent;
    }

    private void MarkFatal(Exception failure)
    {
        Volatile.Write(ref _fatalFailure, failure);
        Interlocked.Exchange(ref _forcedBlock, 1);
        SetCaptureAuthorized(authorized: false);
    }

    private void SetCaptureAuthorized(bool authorized)
    {
        lock (_disposeSync)
        {
            var notification = SetCaptureAuthorizedUnderLock(authorized);
            RaiseAuthorizationChanged(
                notification.Handler,
                notification.EventArgs);
        }
    }

    private void ClearForcedBlockIfActive()
    {
        lock (_disposeSync)
        {
            if (!_disposed && Volatile.Read(ref _quiescing) == 0)
            {
                Interlocked.Exchange(ref _forcedBlock, 0);
            }
        }
    }

    private (
        EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? Handler,
        CaptureRuntimeAuthorizationChangedEventArgs? EventArgs,
        InvalidOperationException? GenerationFailure)
        SetCaptureAuthorizedUnderLock(bool authorized)
    {
        authorized = authorized
            && !_disposed
            && Volatile.Read(ref _quiescing) == 0;
        var previous = Volatile.Read(ref _captureAuthorized) != 0;
        if (previous == authorized)
        {
            return (null, null, null);
        }

        Volatile.Write(ref _captureAuthorized, authorized ? 1 : 0);
        InvalidOperationException? generationFailure = null;
        if (!authorized)
        {
            var currentGeneration = InvalidationGeneration;
            if (currentGeneration == long.MaxValue)
            {
                generationFailure = new InvalidOperationException(
                    "The capture admission invalidation generation has been exhausted; the native handle must be recreated.");
                _ = Interlocked.CompareExchange(
                    ref _fatalFailure,
                    generationFailure,
                    null);
                Interlocked.Exchange(ref _forcedBlock, 1);
            }
            else
            {
                Volatile.Write(
                    ref _invalidationGeneration,
                    currentGeneration + 1);
            }
        }

        return (
            _authorizationChanged,
            new CaptureRuntimeAuthorizationChangedEventArgs(
                authorized,
                InvalidationGeneration),
            generationFailure);
    }

    private void RaiseAuthorizationChanged(
        EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? handler,
        CaptureRuntimeAuthorizationChangedEventArgs? eventArgs)
    {
        if (handler is null || eventArgs is null)
        {
            return;
        }

        _authorizationNotifications.Writer.TryWrite(
            new AuthorizationNotification(handler, eventArgs));
    }

    private async Task DispatchAuthorizationNotificationsAsync()
    {
        try
        {
            await foreach (var notification in _authorizationNotifications.Reader
                               .ReadAllAsync()
                               .ConfigureAwait(false))
            {
                foreach (EventHandler<CaptureRuntimeAuthorizationChangedEventArgs> subscriber
                    in notification.Handler.GetInvocationList())
                {
                    try
                    {
                        subscriber(this, notification.EventArgs);
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine(
                            $"A capture runtime authorization subscriber failed: {exception}");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Capture runtime authorization notification dispatch stopped: {exception}");
        }
    }

    private void EnsurePreparedSettingsCommit(
        AppSettings previous,
        AppSettings current)
    {
        lock (_settingsCommitSync)
        {
            if (_preparedPrevious != previous || _preparedProposed != current)
            {
                throw new InvalidOperationException(
                    "The native privacy settings commit did not match its prepared snapshots.");
            }
        }
    }

    private void BeginPreparedSettingsCommit(
        AppSettings previous,
        AppSettings proposed)
    {
        lock (_settingsCommitSync)
        {
            if (_preparedPrevious is not null || _preparedProposed is not null)
            {
                throw new InvalidOperationException(
                    "A native privacy settings commit is already prepared.");
            }

            _preparedPrevious = previous;
            _preparedProposed = proposed;
        }
    }

    private void CompletePreparedSettingsCommit()
    {
        lock (_settingsCommitSync)
        {
            _preparedPrevious = null;
            _preparedProposed = null;
        }
    }

    private void ThrowIfUsable()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _quiescing) != 0)
        {
            throw new InvalidOperationException(
                "The native capture privacy coordinator is quiescing.");
        }

        if (Volatile.Read(ref _fatalFailure) is { } failure)
        {
            throw new InvalidOperationException(
                "The native capture privacy coordinator is faulted; recreate the native handle before continuing.",
                failure);
        }
    }

    private async Task QuiesceCoreAsync()
    {
        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _lastApplied);
            var context = current.PrivacyContext;
            var revoked = new NativeCaptureRuntimeAuthorization(
                new NativeCapturePrivacyContext(
                    NativeCapturePolicyDecision.Block,
                    context.SessionUnlocked,
                    context.SecureDesktopClear,
                    context.RemoteSessionAllowed,
                    context.PresentationAllowed,
                    context.ApplicationAllowed,
                    context.WindowAllowed,
                    context.StorageAvailable,
                    current.RuntimePolicyRevision == ulong.MaxValue
                        ? current.RuntimePolicyRevision
                        : current.RuntimePolicyRevision + 1),
                NativeCaptureTargetIdentity.Unknown);
            var failures = new List<Exception>();
            if (Volatile.Read(ref _fatalFailure) is { } fatalFailure)
            {
                failures.Add(new InvalidOperationException(
                    "The native capture privacy coordinator faulted before it could quiesce.",
                    fatalFailure));
            }
            else if (current.RuntimePolicyRevision == ulong.MaxValue)
            {
                failures.Add(new InvalidOperationException(
                    "The native capture runtime policy revision has been exhausted; the native handle must be recreated."));
            }
            else
            {
                try
                {
                    var observation = CapturePrivacyObservation();
                    var update = await _target
                        .UpdateRuntimeAuthorizationAsync(
                            revoked,
                            observation.NativeCallbackInvalidationGeneration,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (update.WasCommitted)
                    {
                        ValidateAdvancedPersistenceGeneration(
                            update.PersistenceGeneration,
                            "quiescing");
                        Volatile.Write(
                            ref _lastPersistenceGeneration,
                            update.PersistenceGeneration);
                        Volatile.Write(ref _lastApplied, revoked);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                var revokeGeneration = await _target
                    .RevokeRuntimeAuthorizationAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                ValidateCurrentPersistenceGeneration(
                    revokeGeneration,
                    "revoking");
                Volatile.Write(ref _lastPersistenceGeneration, revokeGeneration);
                Volatile.Write(ref _lastApplied, revoked);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count > 0)
            {
                var failure = failures.Count == 1
                    ? failures[0]
                    : new AggregateException(
                        "The native capture runtime could not quiesce cleanly.",
                        failures);
                MarkFatal(failure);
                throw failure;
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task CompleteQuiesceAsync(TaskCompletionSource completion)
    {
        try
        {
            await QuiesceCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private sealed record AuthorizationNotification(
        EventHandler<CaptureRuntimeAuthorizationChangedEventArgs> Handler,
        CaptureRuntimeAuthorizationChangedEventArgs EventArgs);

    private readonly record struct NativeAuthorizationApplication(
        NativeCapturePrivacyContext Context,
        bool WasCommitted,
        bool WasSuperseded);

    private readonly record struct PrivacyObservationSnapshot(
        long Generation,
        NativeCapturePrivacySignals Signals,
        long NativeCallbackInvalidationGeneration);

    private enum PrivacyObservationPhase
    {
        Legacy = 0,
        Invalidated = 1,
        BarrierApplied = 2,
        Published = 3,
    }

    private static void ValidatePrivacyObservationGeneration(
        long privacyObservationGeneration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            privacyObservationGeneration);
    }

    private void ValidateAdvancedPersistenceGeneration(
        ulong persistenceGeneration,
        string operation)
    {
        if (persistenceGeneration == 0
            || persistenceGeneration <= LastPersistenceGeneration)
        {
            throw new InvalidOperationException(
                $"The native persistence generation did not advance while {operation}.");
        }
    }

    private void ValidateCurrentPersistenceGeneration(
        ulong persistenceGeneration,
        string operation)
    {
        if (persistenceGeneration == 0
            || persistenceGeneration < LastPersistenceGeneration)
        {
            throw new InvalidOperationException(
                $"The native persistence generation regressed while {operation}.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

}
