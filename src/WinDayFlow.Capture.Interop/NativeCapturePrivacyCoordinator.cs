using System.Diagnostics;
using System.Threading.Channels;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public interface INativeCaptureAuthorizationTarget
{
    Task<ulong> UpdateRuntimeAuthorizationAsync(
        NativeCaptureRuntimeAuthorization authorization,
        CancellationToken cancellationToken = default);

    Task<ulong> RevokeRuntimeAuthorizationAsync(
        CancellationToken cancellationToken = default);
}

internal readonly record struct NativeCaptureAdmissionSnapshot(
    long InvalidationGeneration,
    ulong RuntimePolicyRevision,
    ulong PersistenceGeneration,
    ulong TargetEpoch);

public sealed class NativeCapturePrivacyCoordinator
    : IAppSettingsCommitBarrier, IDisposable
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

    public NativeCapturePrivacyContext LastAppliedContext =>
        Volatile.Read(ref _lastApplied).PrivacyContext;

    public NativeCaptureRuntimeAuthorization LastAppliedAuthorization =>
        Volatile.Read(ref _lastApplied);

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
            NativeCapturePrivacySignals signals;
            NativeCapturePrivacyContext applied;
            do
            {
                signals = Volatile.Read(ref _signals);
                applied = await ApplyUnderGateAsync(
                        Volatile.Read(ref _committedSettings),
                        signals,
                        forceBlock: Volatile.Read(ref _forcedBlock) != 0,
                        cancellationToken: CancellationToken.None,
                        forceNativeUpdate: forceNativeUpdate)
                    .ConfigureAwait(false);
                forceNativeUpdate = false;
            }
            while (signals != Volatile.Read(ref _signals));

            PublishAuthorization(applied, signals);
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
            var signals = Volatile.Read(ref _signals);
            var applied = await ApplyUnderGateAsync(
                    proposed,
                    signals,
                    forceBlock: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            PublishAuthorization(applied, signals);
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
                NativeCapturePrivacySignals signals;
                NativeCapturePrivacyContext applied;
                do
                {
                    signals = Volatile.Read(ref _signals);
                    applied = await ApplyUnderGateAsync(
                            current,
                            signals,
                            forceBlock,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                while (signals != Volatile.Read(ref _signals));

                if (authorizingTransition)
                {
                    ClearForcedBlockIfActive();
                }

                PublishAuthorization(applied, signals);
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

        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            Volatile.Write(ref _signals, signals);
            var preview = Compose(
                Volatile.Read(ref _committedSettings),
                signals,
                forceBlock: Volatile.Read(ref _forcedBlock) != 0,
                LastAppliedContext.RuntimePolicyRevision);
            if (!IsFullyAllowed(preview.PrivacyContext)
                || !HasSameDecisions(Volatile.Read(ref _lastApplied), preview))
            {
                SetCaptureAuthorized(authorized: false);
            }

            var applied = await ApplyUnderGateAsync(
                    Volatile.Read(ref _committedSettings),
                    signals,
                    forceBlock: Volatile.Read(ref _forcedBlock) != 0,
                    CancellationToken.None)
                .ConfigureAwait(false);
            PublishAuthorization(applied, signals);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public void Dispose()
    {
        EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? handler;
        CaptureRuntimeAuthorizationChangedEventArgs? eventArgs;
        lock (_disposeSync)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Exchange(ref _forcedBlock, 1);
            Volatile.Write(ref _disposed, true);
            (handler, eventArgs) = SetCaptureAuthorizedUnderLock(authorized: false);
            _authorizationChanged = null;
        }

        _authorizationNotifications.Writer.TryComplete();
        RaiseAuthorizationChanged(handler, eventArgs);
        GC.SuppressFinalize(this);
    }

    private async Task<NativeCapturePrivacyContext> ApplyUnderGateAsync(
        AppSettings settings,
        NativeCapturePrivacySignals signals,
        bool forceBlock,
        CancellationToken cancellationToken,
        bool forceNativeUpdate = false)
    {
        var preview = Compose(
            settings,
            signals,
            forceBlock,
            _lastApplied.RuntimePolicyRevision);
        if (!forceNativeUpdate && HasSameDecisions(_lastApplied, preview))
        {
            return _lastApplied.PrivacyContext;
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
            signals,
            forceBlock,
            _lastApplied.RuntimePolicyRevision + 1);
        try
        {
            var persistenceGeneration = await _target
                .UpdateRuntimeAuthorizationAsync(next, cancellationToken)
                .ConfigureAwait(false);
            ValidateAdvancedPersistenceGeneration(
                persistenceGeneration,
                "updating runtime authorization");

            Volatile.Write(ref _lastPersistenceGeneration, persistenceGeneration);
        }
        catch (Exception exception)
        {
            MarkFatal(exception);
            throw;
        }

        Volatile.Write(ref _lastApplied, next);
        return next.PrivacyContext;
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

    private static NativeCaptureRuntimeAuthorization Compose(
        AppSettings settings,
        NativeCapturePrivacySignals signals,
        bool forceBlock,
        ulong runtimePolicyRevision)
    {
        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            signals,
            runtimePolicyRevision);
        if (NativeCaptureRuntimeAuthorization.IsFullyAllowed(context)
            && signals.Target.State != NativeCaptureTargetIdentityState.Present)
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

        if (!forceBlock || context.ConsentGranted == NativeCapturePolicyDecision.Block)
        {
            return new NativeCaptureRuntimeAuthorization(
                context,
                NativeCaptureRuntimeAuthorization.IsFullyAllowed(context)
                    ? signals.Target
                    : NativeCaptureTargetIdentity.Unknown);
        }

        return new NativeCaptureRuntimeAuthorization(
            new NativeCapturePrivacyContext(
                NativeCapturePolicyDecision.Block,
                context.SessionUnlocked,
                context.SecureDesktopClear,
                context.RemoteSessionAllowed,
                context.PresentationAllowed,
                context.ApplicationAllowed,
                context.WindowAllowed,
                context.StorageAvailable,
                context.RuntimePolicyRevision),
            NativeCaptureTargetIdentity.Unknown);
    }

    private static bool IsRestrictiveChange(
        AppSettings previous,
        AppSettings proposed)
    {
        return previous.CapturePrivacy.Revision != proposed.CapturePrivacy.Revision
            || (HasUserAuthorization(previous) && !HasUserAuthorization(proposed));
    }

    private static bool HasUserAuthorization(AppSettings settings)
    {
        return settings.CaptureEnabled
            && settings.RecordingConsent is { } consent
            && consent.PolicyVersion == AppSettingsService.CurrentRecordingConsentVersion
            && consent.PrivacyRevision == settings.CapturePrivacy.Revision;
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

    private void PublishAuthorization(
        NativeCapturePrivacyContext context,
        NativeCapturePrivacySignals appliedSignals)
    {
        var authorized = Volatile.Read(ref _forcedBlock) == 0
            && Volatile.Read(ref _fatalFailure) is null
            && appliedSignals == Volatile.Read(ref _signals)
            && IsFullyAllowed(context);
        SetCaptureAuthorized(authorized);
    }

    private void MarkFatal(Exception failure)
    {
        Volatile.Write(ref _fatalFailure, failure);
        Interlocked.Exchange(ref _forcedBlock, 1);
        SetCaptureAuthorized(authorized: false);
    }

    private void SetCaptureAuthorized(bool authorized)
    {
        EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? handler;
        CaptureRuntimeAuthorizationChangedEventArgs? eventArgs;
        lock (_disposeSync)
        {
            (handler, eventArgs) = SetCaptureAuthorizedUnderLock(authorized);
        }

        RaiseAuthorizationChanged(handler, eventArgs);
    }

    private void ClearForcedBlockIfActive()
    {
        lock (_disposeSync)
        {
            if (!_disposed)
            {
                Interlocked.Exchange(ref _forcedBlock, 0);
            }
        }
    }

    private (
        EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? Handler,
        CaptureRuntimeAuthorizationChangedEventArgs? EventArgs)
        SetCaptureAuthorizedUnderLock(bool authorized)
    {
        authorized = authorized && !_disposed;
        var previous = Volatile.Read(ref _captureAuthorized) != 0;
        if (previous == authorized)
        {
            return (null, null);
        }

        Volatile.Write(ref _captureAuthorized, authorized ? 1 : 0);
        if (!authorized)
        {
            var generation = checked(InvalidationGeneration + 1);
            Volatile.Write(ref _invalidationGeneration, generation);
        }

        return (
            _authorizationChanged,
            new CaptureRuntimeAuthorizationChangedEventArgs(
                authorized,
                InvalidationGeneration));
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
                    var updateGeneration = await _target
                        .UpdateRuntimeAuthorizationAsync(revoked, CancellationToken.None)
                        .ConfigureAwait(false);
                    ValidateAdvancedPersistenceGeneration(
                        updateGeneration,
                        "quiescing");
                    Volatile.Write(ref _lastPersistenceGeneration, updateGeneration);
                    Volatile.Write(ref _lastApplied, revoked);
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
