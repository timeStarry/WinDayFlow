using System.Diagnostics;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public interface INativeCapturePrivacyTarget
{
    Task UpdatePrivacyContextAsync(
        NativeCapturePrivacyContext context,
        CancellationToken cancellationToken = default);
}

public sealed class NativeCapturePrivacyCoordinator
    : IAppSettingsCommitBarrier, ICaptureRuntimeAuthorization, IDisposable
{
    private readonly INativeCapturePrivacyTarget _target;
    private readonly object _disposeSync = new();
    private readonly object _settingsCommitSync = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private AppSettings _committedSettings;
    private NativeCapturePrivacySignals _signals;
    private NativeCapturePrivacyContext _lastApplied;
    private Exception? _fatalFailure;
    private AppSettings? _preparedPrevious;
    private AppSettings? _preparedProposed;
    private int _forcedBlock = 1;
    private int _captureAuthorized;
    private long _invalidationGeneration;
    private EventHandler<CaptureRuntimeAuthorizationChangedEventArgs>? _authorizationChanged;
    private bool _disposed;

    public NativeCapturePrivacyCoordinator(
        INativeCapturePrivacyTarget target,
        NativeCapturePrivacyContext initialContext,
        AppSettings? initialSettings = null,
        NativeCapturePrivacySignals? initialSignals = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _lastApplied = initialContext
            ?? throw new ArgumentNullException(nameof(initialContext));
        if (_lastApplied.ConsentGranted == NativeCapturePolicyDecision.Allow)
        {
            throw new ArgumentException(
                "The native privacy coordinator requires a fail-closed initial context.",
                nameof(initialContext));
        }

        _committedSettings = initialSettings ?? AppSettings.Default;
        _signals = initialSignals ?? NativeCapturePrivacySignals.FailClosed;
    }

    public bool IsCaptureAuthorized =>
        Volatile.Read(ref _captureAuthorized) != 0;

    public long InvalidationGeneration =>
        Volatile.Read(ref _invalidationGeneration);

    public NativeCapturePrivacyContext LastAppliedContext =>
        Volatile.Read(ref _lastApplied);

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
        if (restrictive)
        {
            Interlocked.Exchange(ref _forcedBlock, 1);
            SetCaptureAuthorized(authorized: false);
        }

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

    public Task AbortedAsync(
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
        if (settingsApplied)
        {
            Volatile.Write(ref _committedSettings, proposed);
            Interlocked.Exchange(ref _forcedBlock, 1);
            SetCaptureAuthorized(authorized: false);
        }

        CompletePreparedSettingsCommit();
        return Task.CompletedTask;
    }

    public async Task UpdateSignalsAsync(
        NativeCapturePrivacySignals signals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signals);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        Volatile.Write(ref _signals, signals);
        var preview = Compose(
            Volatile.Read(ref _committedSettings),
            signals,
            forceBlock: Volatile.Read(ref _forcedBlock) != 0,
            LastAppliedContext.RuntimePolicyRevision);
        if (!IsFullyAllowed(preview))
        {
            SetCaptureAuthorized(authorized: false);
        }

        await _applyGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfUsable();
            var signalsToApply = signals;
            NativeCapturePrivacyContext applied;
            while (true)
            {
                applied = await ApplyUnderGateAsync(
                        Volatile.Read(ref _committedSettings),
                        signalsToApply,
                        forceBlock: Volatile.Read(ref _forcedBlock) != 0,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var latestSignals = Volatile.Read(ref _signals);
                if (signalsToApply == latestSignals)
                {
                    break;
                }

                signalsToApply = latestSignals;
            }

            PublishAuthorization(applied, signalsToApply);
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

        RaiseAuthorizationChanged(handler, eventArgs);
        GC.SuppressFinalize(this);
    }

    private async Task<NativeCapturePrivacyContext> ApplyUnderGateAsync(
        AppSettings settings,
        NativeCapturePrivacySignals signals,
        bool forceBlock,
        CancellationToken cancellationToken)
    {
        var preview = Compose(
            settings,
            signals,
            forceBlock,
            _lastApplied.RuntimePolicyRevision);
        if (HasSameDecisions(_lastApplied, preview))
        {
            return _lastApplied;
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
            await _target
                .UpdatePrivacyContextAsync(next, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            MarkFatal(exception);
            throw;
        }

        Volatile.Write(ref _lastApplied, next);
        return next;
    }

    private static NativeCapturePrivacyContext Compose(
        AppSettings settings,
        NativeCapturePrivacySignals signals,
        bool forceBlock,
        ulong runtimePolicyRevision)
    {
        var context = NativeCapturePrivacyPolicy.Compose(
            settings,
            signals,
            runtimePolicyRevision);
        if (!forceBlock || context.ConsentGranted == NativeCapturePolicyDecision.Block)
        {
            return context;
        }

        return new NativeCapturePrivacyContext(
            NativeCapturePolicyDecision.Block,
            context.SessionUnlocked,
            context.SecureDesktopClear,
            context.RemoteSessionAllowed,
            context.PresentationAllowed,
            context.ApplicationAllowed,
            context.WindowAllowed,
            context.StorageAvailable,
            context.RuntimePolicyRevision);
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
        NativeCapturePrivacyContext left,
        NativeCapturePrivacyContext right)
    {
        return left.ConsentGranted == right.ConsentGranted
            && left.SessionUnlocked == right.SessionUnlocked
            && left.SecureDesktopClear == right.SecureDesktopClear
            && left.RemoteSessionAllowed == right.RemoteSessionAllowed
            && left.PresentationAllowed == right.PresentationAllowed
            && left.ApplicationAllowed == right.ApplicationAllowed
            && left.WindowAllowed == right.WindowAllowed
            && left.StorageAvailable == right.StorageAvailable;
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

        foreach (EventHandler<CaptureRuntimeAuthorizationChangedEventArgs> subscriber
            in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, eventArgs);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"A capture runtime authorization subscriber failed: {exception}");
            }
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
        if (Volatile.Read(ref _fatalFailure) is { } failure)
        {
            throw new InvalidOperationException(
                "The native capture privacy coordinator is faulted; recreate the native handle before continuing.",
                failure);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }
}
