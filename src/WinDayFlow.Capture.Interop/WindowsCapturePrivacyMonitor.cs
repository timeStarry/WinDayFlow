using System.ComponentModel;
using System.Threading.Channels;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;

namespace WinDayFlow.Capture.Interop;

public sealed record WindowsCapturePrivacyObservation
{
    public WindowsCapturePrivacyObservation(
        NativeCapturePrivacySignals signals,
        WindowsCaptureDisplayTarget displayTarget)
    {
        Signals = signals ?? throw new ArgumentNullException(nameof(signals));
        DisplayTarget = displayTarget
            ?? throw new ArgumentNullException(nameof(displayTarget));

        var statesMatch = signals.Target.State switch
        {
            NativeCaptureTargetIdentityState.Unknown =>
                displayTarget.State == WindowsCaptureDisplayTargetState.Unknown,
            NativeCaptureTargetIdentityState.Absent =>
                displayTarget.State == WindowsCaptureDisplayTargetState.Absent,
            NativeCaptureTargetIdentityState.Present =>
                displayTarget.State == WindowsCaptureDisplayTargetState.Present
                && signals.Target.DisplayMonitorHandle
                    == displayTarget.MonitorHandle
                && string.Equals(
                    signals.Target.DisplayDeviceKey,
                    displayTarget.DeviceKey,
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!statesMatch)
        {
            throw new ArgumentException(
                "The capture target and display target must describe the same observation.",
                nameof(displayTarget));
        }
    }

    public static WindowsCapturePrivacyObservation FailClosed { get; } = new(
        NativeCapturePrivacySignals.FailClosed,
        WindowsCaptureDisplayTarget.Unknown);

    public NativeCapturePrivacySignals Signals { get; }

    public WindowsCaptureDisplayTarget DisplayTarget { get; }

    public override string ToString()
    {
        return $"{nameof(WindowsCapturePrivacyObservation)} {{ "
            + $"SessionUnlocked = {Signals.SessionUnlocked}, "
            + $"SecureDesktopClear = {Signals.SecureDesktopClear}, "
            + $"RemoteSession = {Signals.RemoteSession}, "
            + $"PresentationMode = {Signals.PresentationMode}, "
            + $"ApplicationAllowed = {Signals.ApplicationAllowed}, "
            + $"WindowAllowed = {Signals.WindowAllowed}, "
            + $"StorageAvailable = {Signals.StorageAvailable}, "
            + $"TargetState = {Signals.Target.State}, "
            + $"DisplayTargetState = {DisplayTarget.State}, "
            + $"ExecutableNameState = "
            + $"{Signals.CaptureIdentity.ExecutableNameObservation.State}, "
            + $"PackageFamilyNameState = "
            + $"{Signals.CaptureIdentity.PackageFamilyNameObservation.State}, "
            + $"PublisherCertificateSha256State = "
            + $"{Signals.CaptureIdentity.PublisherCertificateSha256Observation.State}, "
            + $"WindowTitleState = "
            + $"{Signals.CaptureIdentity.WindowTitleObservation.State}, "
            + "Values = [REDACTED] }";
    }
}

internal interface IWindowsCapturePrivacySampler
{
    void InvalidateTargetObservation();

    ValueTask<WindowsCapturePrivacyObservation> SampleAsync(
        CancellationToken cancellationToken);
}

internal interface IWindowsCaptureStorageSampler
{
    ValueTask<NativeCapturePolicyDecision> SampleStorageAsync(
        CancellationToken cancellationToken);
}

internal interface IWindowsCaptureSessionSampler
{
    ValueTask<NativeCapturePolicyDecision> SampleSessionAsync(
        CancellationToken cancellationToken);
}

internal delegate bool TryResolveWindowsCaptureDisplayTarget(
    ulong windowHandle,
    out WindowsCaptureDisplayAnchor displayTarget);

public sealed class WindowsCapturePrivacySampler
    : IWindowsCapturePrivacySampler,
      IWindowsCaptureStorageSampler,
      IWindowsCaptureSessionSampler
{
    private readonly Func<NativeCapturePrivacySignals> _sampleBaseSignals;
    private readonly Func<NativeCapturePolicyDecision> _sampleStorage;
    private readonly Func<NativeCapturePolicyDecision> _sampleSession;
    private readonly Func<WindowsCaptureTargetVerificationResult> _verifyTarget;
    private readonly Action _invalidateTargetObservation;

    public WindowsCapturePrivacySampler(
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
    {
        var privacyProbe = new WindowsCapturePrivacyProbe(
            storageDirectory,
            minimumStorageHeadroomBytes);
        var targetVerifier = new WindowsCaptureTargetVerifier();
        _sampleBaseSignals = privacyProbe.Sample;
        _sampleStorage = privacyProbe.SampleStorage;
        _sampleSession = () => _sampleBaseSignals().SessionUnlocked;
        _verifyTarget = targetVerifier.Verify;
        _invalidateTargetObservation = targetVerifier.InvalidateObservation;
    }

    internal WindowsCapturePrivacySampler(
        Func<NativeCapturePrivacySignals> sampleBaseSignals,
        Func<WindowsCaptureTargetVerificationResult> verifyTarget,
        Action invalidateTargetObservation)
    {
        _sampleBaseSignals = sampleBaseSignals
            ?? throw new ArgumentNullException(nameof(sampleBaseSignals));
        _sampleStorage = () => _sampleBaseSignals().StorageAvailable;
        _sampleSession = () => _sampleBaseSignals().SessionUnlocked;
        _verifyTarget = verifyTarget
            ?? throw new ArgumentNullException(nameof(verifyTarget));
        _invalidateTargetObservation = invalidateTargetObservation
            ?? throw new ArgumentNullException(nameof(invalidateTargetObservation));
    }

    public void InvalidateTargetObservation()
    {
        _invalidateTargetObservation();
    }

    public WindowsCapturePrivacyObservation Sample()
    {
        var before = _sampleBaseSignals();
        var target = _verifyTarget();
        var after = _sampleBaseSignals();
        if (before != after)
        {
            return WindowsCapturePrivacyObservation.FailClosed;
        }

        var signals = new NativeCapturePrivacySignals(
            after.SessionUnlocked,
            after.SecureDesktopClear,
            after.RemoteSession,
            after.PresentationMode,
            after.ApplicationAllowed,
            after.WindowAllowed,
            after.StorageAvailable,
            target.CaptureIdentity,
            target.Target);
        return new WindowsCapturePrivacyObservation(
            signals,
            target.DisplayTarget);
    }

    ValueTask<WindowsCapturePrivacyObservation>
        IWindowsCapturePrivacySampler.SampleAsync(
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Sample());
    }

    ValueTask<NativeCapturePolicyDecision>
        IWindowsCaptureStorageSampler.SampleStorageAsync(
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_sampleStorage());
    }

    ValueTask<NativeCapturePolicyDecision>
        IWindowsCaptureSessionSampler.SampleSessionAsync(
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_sampleSession());
    }
}

[Flags]
internal enum WindowsCapturePrivacyInvalidationReason : long
{
    None = 0,
    Startup = 1L << 0,
    Foreground = 1L << 1,
    DesktopSwitch = 1L << 2,
    ObjectCreated = 1L << 3,
    ObjectDestroyed = 1L << 4,
    ObjectNameChanged = 1L << 5,
    EventSourceFault = 1L << 6,
    MonitorFault = 1L << 7,
    Shutdown = 1L << 8,
    ObjectLocationChanged = 1L << 9,
    DisplayTopologyChanged = 1L << 10,
    SessionUnavailable = 1L << 11,
    SessionAvailable = 1L << 12,
    SessionChanged = 1L << 13,
    PowerSuspending = 1L << 14,
    PowerResumed = 1L << 15,
    TransientTargetRecovery = 1L << 16,
    ApplicationPrivacyModeChanged = 1L << 17,
    StorageHeadroomChanged = 1L << 18,
}

[Flags]
internal enum WindowsCapturePrivacyHold
{
    None = 0,
    SessionUnavailable = 1 << 0,
    PowerSuspended = 1 << 1,
}

public enum WindowsCapturePrivacyMonitorFault
{
    EventSourceStart = 1,
    EventSource = 2,
    ObservationInvalidation = 3,
    PrivacyBarrier = 4,
    SignalPublication = 5,
    GenerationDesynchronized = 6,
    Worker = 7,
    EventSourceDisposal = 8,
    PrivacyBarrierDisposal = 9,
    SinkTerminationDisposal = 10,
}

public sealed class WindowsCapturePrivacyMonitorException : Exception
{
    internal WindowsCapturePrivacyMonitorException(
        WindowsCapturePrivacyMonitorFault fault)
        : base($"The Windows capture privacy monitor failed with {fault}.")
    {
        Fault = fault;
    }

    public WindowsCapturePrivacyMonitorFault Fault { get; }
}

public sealed class WindowsCapturePrivacyMonitor : IAsyncDisposable
{
    private const int TerminalTransitionPending = -1;
    internal const int MaxTransientTargetObservationAttempts = 4;
    private static readonly TimeSpan[] TransientTargetRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350),
    ];
    internal static TimeSpan TransientTargetRecoveryRetryDelay { get; } =
        TimeSpan.FromSeconds(2);
    internal static IReadOnlyList<TimeSpan> DisplayRevalidationRetryDelays
    {
        get;
    } =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(350),
    ];
    internal static TimeSpan StorageRefreshInterval { get; } =
        TimeSpan.FromSeconds(5);

    private readonly object _lifecycleSync = new();
    private readonly object _invalidationSync = new();
    private readonly INativeCapturePrivacySignalSink _sink;
    private readonly IWindowsCapturePrivacySampler _sampler;
    private readonly IWindowsCaptureStorageSampler? _storageSampler;
    private readonly IWindowsCaptureSessionSampler? _sessionSampler;
    private readonly IWindowsCaptureEventSource _eventSource;
    private readonly INativeCaptureApplicationPrivacyModeSource?
        _applicationPrivacyModeSource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _storageRefreshDelayAsync;
    private readonly TimeSpan _storageRefreshInterval;
    private readonly TryResolveWindowsCaptureDisplayTarget _resolveDisplayTarget;
    private readonly CaptureDiagnosticLog? _diagnosticLog;
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly CancellationTokenSource _storageRefreshCancellation = new();
    private readonly Channel<byte> _workerWake =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly Channel<DisplayRevalidationRequest>
        _displayRevalidationRequests =
            Channel.CreateBounded<DisplayRevalidationRequest>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WindowsCapturePrivacyMonitorFault>
        _terminalFaultCommitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    private WindowsCapturePrivacyObservation _lastObservation =
        WindowsCapturePrivacyObservation.FailClosed;
    private Task? _workerTask;
    private Task? _displayRevalidationTask;
    private Task? _storageRefreshTask;
    private Task? _disposeTask;
    private Task? _sinkTerminationProof;
    private CancellationTokenSource? _activeRetryWait;
    private Task? _activeRetryCancellation;
    private long _latestGeneration;
    private long _lastProcessedGeneration;
    private long _lastPublishedGeneration;
    private long _observedReasonBits;
    private long _holdGeneration;
    private long _objectEventTargetWindowHandle;
    private long _pendingForegroundTargetWindowHandle;
    private long _displayRevalidationVersion;
    private WindowsCapturePrivacyHold _activeHolds;
    private int _terminalFault;
    private int _sourceCleanupFault;
    private int _startAttempted;
    private int _acceptCallbacks;
    private int _applicationPrivacyModeSubscribed;
    private int _lastStorageDecision = -1;
    private int _stopping;

    public WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        string storageDirectory,
        ulong minimumStorageHeadroomBytes)
        : this(
            sink,
            new WindowsCapturePrivacySampler(
                storageDirectory,
                minimumStorageHeadroomBytes),
            new WindowsCaptureWinEventSource())
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource)
        : this(
            sink,
            sampler,
            eventSource,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource,
        CaptureDiagnosticLog? diagnosticLog)
        : this(
            sink,
            sampler,
            eventSource,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken),
            PInvokeWindowsCaptureTargetNativeApi.Instance.TryGetDisplayTarget,
            StorageRefreshInterval,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken),
            diagnosticLog)
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
        : this(
            sink,
            sampler,
            eventSource,
            delayAsync,
            PInvokeWindowsCaptureTargetNativeApi.Instance.TryGetDisplayTarget,
            StorageRefreshInterval,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource,
        TimeSpan storageRefreshInterval,
        Func<TimeSpan, CancellationToken, Task> storageRefreshDelayAsync)
        : this(
            sink,
            sampler,
            eventSource,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken),
            PInvokeWindowsCaptureTargetNativeApi.Instance.TryGetDisplayTarget,
            storageRefreshInterval,
            storageRefreshDelayAsync)
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TryResolveWindowsCaptureDisplayTarget resolveDisplayTarget)
        : this(
            sink,
            sampler,
            eventSource,
            delayAsync,
            resolveDisplayTarget,
            StorageRefreshInterval,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal WindowsCapturePrivacyMonitor(
        INativeCapturePrivacySignalSink sink,
        IWindowsCapturePrivacySampler sampler,
        IWindowsCaptureEventSource eventSource,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TryResolveWindowsCaptureDisplayTarget resolveDisplayTarget,
        TimeSpan storageRefreshInterval,
        Func<TimeSpan, CancellationToken, Task> storageRefreshDelayAsync,
        CaptureDiagnosticLog? diagnosticLog = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _storageSampler = sampler as IWindowsCaptureStorageSampler;
        _sessionSampler = sampler as IWindowsCaptureSessionSampler;
        _eventSource = eventSource
            ?? throw new ArgumentNullException(nameof(eventSource));
        _applicationPrivacyModeSource =
            sink as INativeCaptureApplicationPrivacyModeSource;
        _delayAsync = delayAsync
            ?? throw new ArgumentNullException(nameof(delayAsync));
        if (storageRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(storageRefreshInterval),
                storageRefreshInterval,
                "The storage refresh interval must be positive.");
        }

        _storageRefreshInterval = storageRefreshInterval;
        _storageRefreshDelayAsync = storageRefreshDelayAsync
            ?? throw new ArgumentNullException(nameof(storageRefreshDelayAsync));
        _resolveDisplayTarget = resolveDisplayTarget
            ?? throw new ArgumentNullException(nameof(resolveDisplayTarget));
        _diagnosticLog = diagnosticLog;
    }

    public Task Completion => _completion.Task;

    public WindowsCapturePrivacyObservation LastObservation =>
        Volatile.Read(ref _lastObservation);

    public long LastPublishedGeneration =>
        Volatile.Read(ref _lastPublishedGeneration);

    internal WindowsCapturePrivacyInvalidationReason ObservedInvalidationReasons =>
        (WindowsCapturePrivacyInvalidationReason)Volatile.Read(
            ref _observedReasonBits);

    internal WindowsCapturePrivacyHold ActiveHolds
    {
        get
        {
            lock (_invalidationSync)
            {
                return _activeHolds;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsCapturePrivacyMonitorFault? startupFault = null;
        lock (_lifecycleSync)
        {
            if (Interlocked.Exchange(ref _startAttempted, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The Windows capture privacy monitor can only be started once.");
            }

            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _stopping) != 0,
                this);
            lock (_invalidationSync)
            {
                try
                {
                    SubscribeApplicationPrivacyModeChanges();
                    InvalidateWithoutWake(
                        WindowsCapturePrivacyInvalidationReason.Startup);
                }
                catch
                {
                    if (TryBeginTerminalTransitionUnderLock())
                    {
                        CompleteTerminalTransitionUnderLock(
                            WindowsCapturePrivacyMonitorFault
                                .ObservationInvalidation);
                    }

                    startupFault = GetTerminalFault()
                        ?? WindowsCapturePrivacyMonitorFault
                            .ObservationInvalidation;
                }
            }
        }

        if (startupFault is null)
        {
            try
            {
                await ApplyLatestPrivacyInvalidationAsync()
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (_invalidationSync)
                {
                    if (TryBeginTerminalTransitionUnderLock())
                    {
                        CompleteTerminalTransitionUnderLock(
                            WindowsCapturePrivacyMonitorFault.PrivacyBarrier);
                    }

                    startupFault = GetTerminalFault()
                        ?? WindowsCapturePrivacyMonitorFault.PrivacyBarrier;
                }
            }
        }

        if (startupFault is null)
        {
            lock (_lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _stopping) != 0,
                    this);
                Volatile.Write(ref _acceptCallbacks, 1);
                try
                {
                    _eventSource.Start(OnSourceChanged, OnSourceFaulted);
                }
                catch
                {
                    Volatile.Write(ref _acceptCallbacks, 0);
                    lock (_invalidationSync)
                    {
                        if (TryBeginTerminalTransitionUnderLock())
                        {
                            var fault = WindowsCapturePrivacyMonitorFault
                                .EventSourceStart;
                            try
                            {
                                InvalidateWithoutWake(
                                    WindowsCapturePrivacyInvalidationReason
                                        .MonitorFault);
                            }
                            catch
                            {
                                fault = WindowsCapturePrivacyMonitorFault
                                    .ObservationInvalidation;
                            }

                            CompleteTerminalTransitionUnderLock(fault);
                        }

                        startupFault = GetTerminalFault()
                            ?? WindowsCapturePrivacyMonitorFault
                                .EventSourceStart;
                    }
                }

                if (startupFault is null
                    && GetTerminalFault() is { } sourceStartupFault)
                {
                    Volatile.Write(ref _acceptCallbacks, 0);
                    startupFault = sourceStartupFault;
                }

                if (startupFault is null)
                {
                    _workerTask = Task.Run(
                        RunWorkerAsync,
                        CancellationToken.None);
                    _displayRevalidationTask = Task.Run(
                        RunDisplayRevalidationAsync,
                        CancellationToken.None);
                    if (_storageSampler is not null)
                    {
                        _storageRefreshTask = Task.Run(
                            RunStorageRefreshAsync,
                            CancellationToken.None);
                    }

                    WakeWorker();
                }
            }
        }

        if (startupFault is null)
        {
            return;
        }

        await CompleteFailedStartAsync(startupFault.Value).ConfigureAwait(false);
        throw CreateFaultException(startupFault.Value);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private void OnSourceChanged(WindowsCaptureWinEventNotification notification)
    {
        if (Volatile.Read(ref _acceptCallbacks) == 0)
        {
            return;
        }

        try
        {
            lock (_invalidationSync)
            {
                if (Volatile.Read(ref _acceptCallbacks) == 0
                    || IsTerminalTransitionStarted())
                {
                    return;
                }

                try
                {
                    if (!ShouldInvalidateUnderLock(notification))
                    {
                        return;
                    }

                    var reason = MapReason(notification.Change);
                    ApplyHoldChangeUnderLock(notification.Change);
                    InvalidateWithoutWake(reason);
                }
                catch
                {
                    HandleCallbackFailureUnderLock();
                }
            }

            WakeWorker();
        }
        catch
        {
            // Exceptions must never cross a native WinEvent callback boundary.
        }
    }

    private void OnApplicationPrivacyModeChanged(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (Volatile.Read(ref _acceptCallbacks) == 0)
        {
            return;
        }

        try
        {
            lock (_invalidationSync)
            {
                if (Volatile.Read(ref _acceptCallbacks) == 0
                    || IsTerminalTransitionStarted())
                {
                    return;
                }

                try
                {
                    _objectEventTargetWindowHandle = 0;
                    _pendingForegroundTargetWindowHandle = 0;
                    Interlocked.Increment(
                        ref _displayRevalidationVersion);
                    InvalidateWithoutWake(
                        WindowsCapturePrivacyInvalidationReason
                            .ApplicationPrivacyModeChanged);
                }
                catch
                {
                    HandleCallbackFailureUnderLock();
                }
            }

            WakeWorker();
        }
        catch
        {
            // Exceptions must never escape a settings notification callback.
        }
    }

    private void OnSourceFaulted(WindowsCaptureWinEventSourceFault fault)
    {
        try
        {
            _ = fault;
            lock (_invalidationSync)
            {
                if (Volatile.Read(ref _stopping) != 0)
                {
                    Interlocked.CompareExchange(
                        ref _sourceCleanupFault,
                        value: 1,
                        comparand: 0);
                    return;
                }

                if (!TryBeginTerminalTransitionUnderLock())
                {
                    return;
                }

                var terminalFault = WindowsCapturePrivacyMonitorFault.EventSource;
                try
                {
                    InvalidateWithoutWake(
                        WindowsCapturePrivacyInvalidationReason.EventSourceFault);
                }
                catch
                {
                    terminalFault = WindowsCapturePrivacyMonitorFault
                        .ObservationInvalidation;
                }

                CompleteTerminalTransitionUnderLock(terminalFault);
            }

            WakeWorker();
        }
        catch
        {
            // Exceptions must never cross a native WinEvent callback boundary.
        }
    }

    private void HandleCallbackFailureUnderLock()
    {
        if (!TryBeginTerminalTransitionUnderLock())
        {
            return;
        }

        try
        {
            InvalidateWithoutWake(
                WindowsCapturePrivacyInvalidationReason.MonitorFault);
        }
        catch
        {
        }

        CompleteTerminalTransitionUnderLock(
            WindowsCapturePrivacyMonitorFault.ObservationInvalidation);
    }

    private void InvalidateWithoutWake(
        WindowsCapturePrivacyInvalidationReason reason)
    {
        CancelActiveRetryWaitUnderLock();
        var generation = 0L;
        Exception? failure = null;
        try
        {
            generation = _sink.InvalidatePrivacyObservation();
            var current = Volatile.Read(ref _latestGeneration);
            if (generation <= current)
            {
                throw new InvalidOperationException(
                    "The privacy observation generation did not advance.");
            }

            AdvanceGeneration(ref _latestGeneration, generation);
            _holdGeneration = generation;
            _ = Interlocked.Or(ref _observedReasonBits, (long)reason);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _sampler.InvalidateTargetObservation();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        if (failure is not null)
        {
            throw failure;
        }

        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.PrivacyInvalidated,
            new(CaptureDiagnosticField.Generation, generation),
            new(CaptureDiagnosticField.Reason, (long)reason),
            new(CaptureDiagnosticField.Holds, (long)_activeHolds));
    }

    private async Task RunStorageRefreshAsync()
    {
        var storageSampler = _storageSampler;
        if (storageSampler is null)
        {
            return;
        }

        var sessionSampler = _sessionSampler;
        var cancellationToken = _storageRefreshCancellation.Token;
        try
        {
            while (true)
            {
                await _storageRefreshDelayAsync(
                        _storageRefreshInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopping) != 0
                    || IsTerminalTransitionStarted())
                {
                    return;
                }

                NativeCapturePolicyDecision storageDecision;
                try
                {
                    storageDecision = await storageSampler
                        .SampleStorageAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                    when (WindowsCapturePrivacyProbe
                        .IsRecoverableNativeReadException(exception))
                {
                    storageDecision = NativeCapturePolicyDecision.Unknown;
                }

                if (!Enum.IsDefined(storageDecision))
                {
                    storageDecision = NativeCapturePolicyDecision.Unknown;
                }

                var refreshSessionHold = false;
                lock (_invalidationSync)
                {
                    refreshSessionHold = (_activeHolds
                        & WindowsCapturePrivacyHold.SessionUnavailable) != 0;
                }

                var sessionDecision = NativeCapturePolicyDecision.Unknown;
                if (refreshSessionHold && sessionSampler is not null)
                {
                    try
                    {
                        sessionDecision = await sessionSampler
                            .SampleSessionAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                        when (WindowsCapturePrivacyProbe
                            .IsRecoverableNativeReadException(exception))
                    {
                        sessionDecision = NativeCapturePolicyDecision.Unknown;
                    }

                    if (!Enum.IsDefined(sessionDecision))
                    {
                        sessionDecision = NativeCapturePolicyDecision.Unknown;
                    }
                }

                var wakeWorker = false;
                lock (_invalidationSync)
                {
                    if (Volatile.Read(ref _acceptCallbacks) == 0
                        || Volatile.Read(ref _stopping) != 0
                        || IsTerminalTransitionStarted())
                    {
                        return;
                    }

                    var reason = WindowsCapturePrivacyInvalidationReason.None;
                    if (_lastStorageDecision >= 0
                        && _lastStorageDecision != (int)storageDecision)
                    {
                        _lastStorageDecision = (int)storageDecision;
                        reason |= WindowsCapturePrivacyInvalidationReason
                            .StorageHeadroomChanged;
                    }

                    if ((_activeHolds
                            & WindowsCapturePrivacyHold.SessionUnavailable) != 0
                        && sessionDecision == NativeCapturePolicyDecision.Allow)
                    {
                        _activeHolds &=
                            ~WindowsCapturePrivacyHold.SessionUnavailable;
                        reason |= WindowsCapturePrivacyInvalidationReason
                            .SessionAvailable;
                    }

                    if (reason == WindowsCapturePrivacyInvalidationReason.None)
                    {
                        continue;
                    }

                    try
                    {
                        InvalidateWithoutWake(reason);
                    }
                    catch
                    {
                        HandleCallbackFailureUnderLock();
                    }

                    wakeWorker = true;
                }

                if (wakeWorker)
                {
                    WakeWorker();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.Worker)
                .ConfigureAwait(false);
        }
    }

    private async Task RunDisplayRevalidationAsync()
    {
        var cancellationToken = _workerCancellation.Token;
        try
        {
            while (await _displayRevalidationRequests.Reader
                       .WaitToReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!_displayRevalidationRequests.Reader.TryRead(
                        out var request))
                {
                    continue;
                }

                while (_displayRevalidationRequests.Reader.TryRead(
                           out var newerRequest))
                {
                    request = newerRequest;
                }

                var attempt = 0;
                while (attempt < DisplayRevalidationRetryDelays.Count)
                {
                    await _delayAsync(
                            DisplayRevalidationRetryDelays[attempt],
                            cancellationToken)
                        .ConfigureAwait(false);
                    attempt++;

                    while (_displayRevalidationRequests.Reader.TryRead(
                               out var newerRequest))
                    {
                        request = newerRequest;
                    }

                    var completed = false;
                    var wakeWorker = false;
                    lock (_invalidationSync)
                    {
                        if (Volatile.Read(ref _acceptCallbacks) == 0
                            || Volatile.Read(ref _stopping) != 0
                            || IsTerminalTransitionStarted()
                            || !IsAllowAllApplicationsMode()
                            || request.Version
                                != Volatile.Read(
                                    ref _displayRevalidationVersion))
                        {
                            completed = true;
                        }
                        else
                        {
                            var comparison = CompareAuthorizedDisplayUnderLock(
                                request.WindowHandle);
                            if (comparison == DisplayComparison.Same)
                            {
                                if (request.WindowHandle != 0)
                                {
                                    _objectEventTargetWindowHandle =
                                        EncodeWindowHandle(
                                            request.WindowHandle);
                                }

                                _pendingForegroundTargetWindowHandle = 0;
                                completed = true;
                            }
                            else if (comparison == DisplayComparison.Different
                                || attempt
                                    == DisplayRevalidationRetryDelays.Count)
                            {
                                _pendingForegroundTargetWindowHandle =
                                    request.RequireCandidateMatch
                                        ? EncodeWindowHandle(
                                            request.WindowHandle)
                                        : 0;
                                Interlocked.Increment(
                                    ref _displayRevalidationVersion);
                                try
                                {
                                    InvalidateWithoutWake(
                                        MapReason(request.Change));
                                }
                                catch
                                {
                                    HandleCallbackFailureUnderLock();
                                }

                                completed = true;
                                wakeWorker = true;
                            }
                        }
                    }

                    if (wakeWorker)
                    {
                        WakeWorker();
                    }

                    if (completed)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.Worker)
                .ConfigureAwait(false);
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            var slowRecoveryGeneration = 0L;
            while (await _workerWake.Reader
                       .WaitToReadAsync(_workerCancellation.Token)
                       .ConfigureAwait(false))
            {
                while (_workerWake.Reader.TryRead(out _))
                {
                }

                while (true)
                {
                    var generation = Volatile.Read(ref _latestGeneration);
                    var isSlowRecoveryGeneration =
                        generation == slowRecoveryGeneration;
                    if (!isSlowRecoveryGeneration)
                    {
                        slowRecoveryGeneration = 0;
                    }

                    if (generation <= 0)
                    {
                        break;
                    }

                    if (generation <= Volatile.Read(ref _lastProcessedGeneration)
                        && GetTerminalFault() is null)
                    {
                        break;
                    }

                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        return;
                    }

                    try
                    {
                        await _sink
                            .ApplyPrivacyInvalidationAsync(
                                generation,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                        when (Volatile.Read(ref _stopping) != 0)
                    {
                        if (TryCaptureStartedSinkTermination() is not null)
                        {
                            return;
                        }

                        throw;
                    }
                    catch
                    {
                        await CloseWorkerForFaultAsync(
                                WindowsCapturePrivacyMonitorFault.PrivacyBarrier)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!IsCurrentGeneration(generation))
                    {
                        if (generation == Volatile.Read(ref _latestGeneration))
                        {
                            await CloseWorkerForFaultAsync(
                                    WindowsCapturePrivacyMonitorFault
                                        .GenerationDesynchronized)
                                .ConfigureAwait(false);
                            return;
                        }

                        continue;
                    }

                    if (GetTerminalFault() is { } terminalFault)
                    {
                        _completion.TrySetException(
                            CreateFaultException(terminalFault));
                        return;
                    }

                    if (IsTerminalTransitionPending())
                    {
                        break;
                    }

                    if (Volatile.Read(ref _stopping) != 0)
                    {
                        return;
                    }

                    var resolution = CaptureResolutionDirective(generation);
                    if (resolution == ObservationResolutionDirective.Stale)
                    {
                        continue;
                    }

                    WindowsCapturePrivacyObservation? observation;
                    var recoverableSampleFailed = false;
                    if (resolution
                        == ObservationResolutionDirective.PublishFailClosed)
                    {
                        observation = WindowsCapturePrivacyObservation.FailClosed;
                    }
                    else
                    {
                        observation = null;
                        var retryIndex = isSlowRecoveryGeneration
                            ? TransientTargetRetryDelays.Length
                            : 0;
                        while (true)
                        {
                            try
                            {
                                observation = await _sampler
                                    .SampleAsync(_workerCancellation.Token)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                                when (_workerCancellation.IsCancellationRequested)
                            {
                                return;
                            }
                            catch (Exception exception)
                                when (IsRecoverableSampleException(exception))
                            {
                                if (!await PublishRecoverableSampleFailureAsync(
                                        generation)
                                        .ConfigureAwait(false))
                                {
                                    return;
                                }

                                recoverableSampleFailed = true;
                                break;
                            }

                            if (DoesNotMatchCurrentTargetCandidate(
                                    generation,
                                    observation))
                            {
                                observation = WindowsCapturePrivacyObservation.FailClosed;
                            }

                            LogObservation(generation, observation);

                            if (!IsTransientTargetObservation(observation))
                            {
                                break;
                            }

                            if (retryIndex < TransientTargetRetryDelays.Length)
                            {
                                if (!await WaitForTransientTargetRetryAsync(
                                        generation,
                                        TransientTargetRetryDelays[retryIndex])
                                        .ConfigureAwait(false))
                                {
                                    observation = null;
                                    break;
                                }

                                retryIndex++;
                                continue;
                            }

                            if (!IsRecoverableTransientTargetObservation(
                                    observation))
                            {
                                break;
                            }

                            bool failClosedPublished;
                            try
                            {
                                failClosedPublished = await _sink
                                    .TryUpdateSignalsAsync(
                                        generation,
                                        observation.Signals,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                                LogPublication(generation, failClosedPublished);
                                if (failClosedPublished)
                                {
                                    TryCommitPublishedObservation(
                                        generation,
                                        observation);
                                }
                            }
                            catch (ObjectDisposedException)
                                when (Volatile.Read(ref _stopping) != 0)
                            {
                                if (TryCaptureStartedSinkTermination() is not null)
                                {
                                    return;
                                }

                                throw;
                            }
                            catch
                            {
                                await CloseWorkerForFaultAsync(
                                        WindowsCapturePrivacyMonitorFault
                                            .SignalPublication)
                                    .ConfigureAwait(false);
                                return;
                            }

                            if (!failClosedPublished)
                            {
                                if (generation
                                    == Volatile.Read(
                                        ref _latestGeneration))
                                {
                                    await CloseWorkerForFaultAsync(
                                            WindowsCapturePrivacyMonitorFault
                                                .GenerationDesynchronized)
                                        .ConfigureAwait(false);
                                    return;
                                }

                                observation = null;
                                break;
                            }

                            _diagnosticLog?.Write(
                                CaptureDiagnosticEvent.PrivacyRecoveryScheduled,
                                new(CaptureDiagnosticField.Generation, generation),
                                new(
                                    CaptureDiagnosticField.RetryDelayMilliseconds,
                                    (long)TransientTargetRecoveryRetryDelay
                                        .TotalMilliseconds));
                            if (!await WaitForTransientTargetRetryAsync(
                                    generation,
                                    TransientTargetRecoveryRetryDelay,
                                    invalidateTargetObservation: false)
                                    .ConfigureAwait(false))
                            {
                                observation = null;
                                break;
                            }

                            var recoveryGeneration = 0L;
                            var recoveryInvalidationFailed = false;
                            lock (_invalidationSync)
                            {
                                if (IsCurrentGeneration(generation)
                                    && Volatile.Read(ref _stopping) == 0
                                    && !IsTerminalTransitionStarted()
                                    && _activeHolds
                                        == WindowsCapturePrivacyHold.None)
                                {
                                    try
                                    {
                                        InvalidateWithoutWake(
                                            WindowsCapturePrivacyInvalidationReason
                                                .TransientTargetRecovery);
                                        recoveryGeneration = Volatile.Read(
                                            ref _latestGeneration);
                                    }
                                    catch
                                    {
                                        recoveryInvalidationFailed = true;
                                    }
                                }
                            }

                            if (recoveryInvalidationFailed)
                            {
                                await CloseWorkerForFaultAsync(
                                        WindowsCapturePrivacyMonitorFault
                                            .ObservationInvalidation)
                                    .ConfigureAwait(false);
                                return;
                            }

                            if (recoveryGeneration > generation)
                            {
                                slowRecoveryGeneration = recoveryGeneration;
                            }

                            observation = null;
                            break;
                        }
                    }

                    if (recoverableSampleFailed)
                    {
                        if (generation == Volatile.Read(ref _latestGeneration))
                        {
                            break;
                        }

                        continue;
                    }

                    if (observation is null)
                    {
                        continue;
                    }

                    if (IsTerminalTransitionPending())
                    {
                        break;
                    }

                    if (GetTerminalFault() is not null)
                    {
                        continue;
                    }

                    if (!IsCurrentGeneration(generation)
                        || Volatile.Read(ref _stopping) != 0)
                    {
                        continue;
                    }

                    bool published;
                    try
                    {
                        published = await _sink
                            .TryUpdateSignalsAsync(
                                generation,
                                observation.Signals,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        LogPublication(generation, published);
                        if (published)
                        {
                            TryCommitPublishedObservation(generation, observation);
                        }
                    }
                    catch (ObjectDisposedException)
                        when (Volatile.Read(ref _stopping) != 0)
                    {
                        if (TryCaptureStartedSinkTermination() is not null)
                        {
                            return;
                        }

                        throw;
                    }
                    catch
                    {
                        await CloseWorkerForFaultAsync(
                                WindowsCapturePrivacyMonitorFault.SignalPublication)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!published
                        && generation == Volatile.Read(ref _latestGeneration))
                    {
                        await CloseWorkerForFaultAsync(
                                WindowsCapturePrivacyMonitorFault
                                    .GenerationDesynchronized)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (generation == Volatile.Read(ref _latestGeneration))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (_workerCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
            when (Volatile.Read(ref _stopping) != 0)
        {
            if (TryCaptureStartedSinkTermination() is not null)
            {
                return;
            }

            throw;
        }
        catch
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.Worker)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> PublishRecoverableSampleFailureAsync(long generation)
    {
        if (!IsCurrentGeneration(generation)
            || Volatile.Read(ref _stopping) != 0)
        {
            return true;
        }

        try
        {
            var published = await _sink
                .TryUpdateSignalsAsync(
                    generation,
                    NativeCapturePrivacySignals.FailClosed,
                    CancellationToken.None)
                .ConfigureAwait(false);
            LogPublication(generation, published);
            if (published)
            {
                TryCommitPublishedObservation(
                    generation,
                    WindowsCapturePrivacyObservation.FailClosed);
            }
            else if (!published
                && generation == Volatile.Read(ref _latestGeneration))
            {
                await CloseWorkerForFaultAsync(
                        WindowsCapturePrivacyMonitorFault.GenerationDesynchronized)
                    .ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (ObjectDisposedException)
            when (Volatile.Read(ref _stopping) != 0)
        {
            if (TryCaptureStartedSinkTermination() is not null)
            {
                return true;
            }

            throw;
        }
        catch
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.SignalPublication)
                .ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> WaitForTransientTargetRetryAsync(
        long generation,
        TimeSpan retryDelay,
        bool invalidateTargetObservation = true)
    {
        CancellationTokenSource retryWait;
        lock (_invalidationSync)
        {
            if (!IsCurrentGeneration(generation)
                || Volatile.Read(ref _stopping) != 0
                || IsTerminalTransitionStarted()
                || _activeHolds != WindowsCapturePrivacyHold.None)
            {
                return false;
            }

            if (_activeRetryWait is not null)
            {
                throw new InvalidOperationException(
                    "Only one transient target retry can be pending.");
            }

            if (_activeRetryCancellation is not null)
            {
                throw new InvalidOperationException(
                    "The previous transient target retry is still cancelling.");
            }

            retryWait = new CancellationTokenSource();
            _activeRetryWait = retryWait;
        }

        try
        {
            await _delayAsync(
                    retryDelay,
                    retryWait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (retryWait.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Task? cancellationCompletion = null;
            lock (_invalidationSync)
            {
                if (ReferenceEquals(_activeRetryWait, retryWait))
                {
                    _activeRetryWait = null;
                    cancellationCompletion = _activeRetryCancellation;
                    _activeRetryCancellation = null;
                }
            }

            try
            {
                if (cancellationCompletion is not null)
                {
                    await cancellationCompletion.ConfigureAwait(false);
                }
            }
            finally
            {
                retryWait.Dispose();
            }
        }

        var invalidationFailed = false;
        lock (_invalidationSync)
        {
            if (!IsCurrentGeneration(generation)
                || Volatile.Read(ref _stopping) != 0
                || IsTerminalTransitionStarted()
                || _activeHolds != WindowsCapturePrivacyHold.None)
            {
                return false;
            }

            if (invalidateTargetObservation)
            {
                try
                {
                    _sampler.InvalidateTargetObservation();
                }
                catch
                {
                    invalidationFailed = true;
                }
            }
        }

        if (invalidationFailed)
        {
            await CloseWorkerForFaultAsync(
                    WindowsCapturePrivacyMonitorFault.ObservationInvalidation)
                .ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private void CancelActiveRetryWaitUnderLock()
    {
        if (_activeRetryWait is { } retryWait)
        {
            _activeRetryCancellation ??= retryWait.CancelAsync();
        }
    }

    private async Task CloseWorkerForFaultAsync(
        WindowsCapturePrivacyMonitorFault fault)
    {
        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.PrivacyMonitorFaulted,
            new(CaptureDiagnosticField.Fault, (long)fault),
            new(
                CaptureDiagnosticField.Generation,
                Volatile.Read(ref _latestGeneration)),
            new(
                CaptureDiagnosticField.SinkGeneration,
                _sink.PrivacyObservationGeneration));
        var ownsTerminalTransition = false;
        try
        {
            lock (_invalidationSync)
            {
                ownsTerminalTransition = TryBeginTerminalTransitionUnderLock();
                if (ownsTerminalTransition)
                {
                    try
                    {
                        InvalidateWithoutWake(
                            WindowsCapturePrivacyInvalidationReason.MonitorFault);
                    }
                    catch
                    {
                        fault = WindowsCapturePrivacyMonitorFault
                            .ObservationInvalidation;
                    }
                }
            }

            if (ownsTerminalTransition)
            {
                try
                {
                    await ApplyLatestPrivacyInvalidationAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    fault = WindowsCapturePrivacyMonitorFault.PrivacyBarrier;
                }

                lock (_invalidationSync)
                {
                    CompleteTerminalTransitionUnderLock(fault);
                }
            }
            else
            {
                fault = await _terminalFaultCommitted.Task.ConfigureAwait(false);
                try
                {
                    await ApplyLatestPrivacyInvalidationAsync()
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The accepted terminal fault remains authoritative.
                }
            }

            _completion.TrySetException(CreateFaultException(fault));
        }
        catch
        {
            lock (_invalidationSync)
            {
                if (ownsTerminalTransition && IsTerminalTransitionPending())
                {
                    CompleteTerminalTransitionUnderLock(fault);
                }
            }

            _completion.TrySetException(CreateFaultException(fault));
        }
    }

    private async Task CompleteFailedStartAsync(
        WindowsCapturePrivacyMonitorFault startupFault)
    {
        var barrierGeneration = 0L;
        UnsubscribeApplicationPrivacyModeChanges();
        try
        {
            barrierGeneration = await ApplyLatestPrivacyInvalidationAsync()
                .ConfigureAwait(false);
        }
        catch
        {
            // The already accepted startup fault remains authoritative.
        }

        try
        {
            _eventSource.Dispose();
        }
        catch
        {
            Interlocked.CompareExchange(
                ref _sourceCleanupFault,
                value: 1,
                comparand: 0);
        }

        if (barrierGeneration != Volatile.Read(ref _latestGeneration)
            || barrierGeneration != _sink.PrivacyObservationGeneration)
        {
            try
            {
                await ApplyLatestPrivacyInvalidationAsync().ConfigureAwait(false);
            }
            catch
            {
                // The already accepted startup fault remains authoritative.
            }
        }

        var fault = GetTerminalFault() ?? startupFault;
        _completion.TrySetException(CreateFaultException(fault));
    }

    private async Task DisposeCoreAsync()
    {
        WindowsCapturePrivacyMonitorFault? disposalFault = null;
        var barrierGeneration = 0L;
        Task? sinkTermination = null;
        lock (_invalidationSync)
        {
            Volatile.Write(ref _acceptCallbacks, 0);
            UnsubscribeApplicationPrivacyModeChanges();
            Interlocked.Exchange(ref _stopping, 1);
            try
            {
                InvalidateWithoutWake(
                    WindowsCapturePrivacyInvalidationReason.Shutdown);
            }
            catch (ObjectDisposedException)
            {
                sinkTermination = TryCaptureStartedSinkTermination();
                if (sinkTermination is null)
                {
                    disposalFault = WindowsCapturePrivacyMonitorFault
                        .ObservationInvalidation;
                }
            }
            catch
            {
                disposalFault = WindowsCapturePrivacyMonitorFault
                    .ObservationInvalidation;
            }
        }

        try
        {
            await _storageRefreshCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            disposalFault ??= WindowsCapturePrivacyMonitorFault.Worker;
        }

        var storageRefreshTask = Volatile.Read(ref _storageRefreshTask);
        if (storageRefreshTask is not null)
        {
            try
            {
                await storageRefreshTask.ConfigureAwait(false);
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault.Worker;
            }
        }

        _storageRefreshCancellation.Dispose();

        if (sinkTermination is null)
        {
            try
            {
                barrierGeneration = await ApplyLatestPrivacyInvalidationAsync()
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                sinkTermination = TryCaptureStartedSinkTermination();
                if (sinkTermination is null)
                {
                    disposalFault ??= WindowsCapturePrivacyMonitorFault
                        .PrivacyBarrierDisposal;
                }
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault
                    .PrivacyBarrierDisposal;
            }
        }

        try
        {
            _eventSource.Dispose();
        }
        catch
        {
            disposalFault ??= WindowsCapturePrivacyMonitorFault.EventSourceDisposal;
        }

        if (Volatile.Read(ref _sourceCleanupFault) != 0)
        {
            disposalFault ??= WindowsCapturePrivacyMonitorFault.EventSourceDisposal;
        }

        if (sinkTermination is null)
        {
            try
            {
                if (barrierGeneration != Volatile.Read(ref _latestGeneration)
                    || barrierGeneration != _sink.PrivacyObservationGeneration)
                {
                    await ApplyLatestPrivacyInvalidationAsync()
                        .ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException)
            {
                sinkTermination = TryCaptureStartedSinkTermination();
                if (sinkTermination is null)
                {
                    disposalFault ??= WindowsCapturePrivacyMonitorFault
                        .PrivacyBarrierDisposal;
                }
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault
                    .PrivacyBarrierDisposal;
            }
        }

        _workerCancellation.Cancel();
        _workerWake.Writer.TryComplete();
        _displayRevalidationRequests.Writer.TryComplete();
        var workerTask = Volatile.Read(ref _workerTask);
        if (workerTask is not null)
        {
            try
            {
                await workerTask.ConfigureAwait(false);
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault.Worker;
            }
        }

        var displayRevalidationTask = Volatile.Read(
            ref _displayRevalidationTask);
        if (displayRevalidationTask is not null)
        {
            try
            {
                await displayRevalidationTask.ConfigureAwait(false);
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault.Worker;
            }
        }

        _workerCancellation.Dispose();
        sinkTermination ??= Volatile.Read(ref _sinkTerminationProof);
        if (sinkTermination is not null)
        {
            try
            {
                await sinkTermination.ConfigureAwait(false);
            }
            catch
            {
                disposalFault ??= WindowsCapturePrivacyMonitorFault
                    .SinkTerminationDisposal;
            }
        }

        var terminalFault = GetTerminalFault();
        if (terminalFault is null && IsTerminalTransitionPending())
        {
            terminalFault = await _terminalFaultCommitted.Task.ConfigureAwait(false);
        }

        if (terminalFault is { } acceptedTerminalFault)
        {
            _completion.TrySetException(
                CreateFaultException(acceptedTerminalFault));
        }

        if (disposalFault is null)
        {
            if (terminalFault is null)
            {
                _completion.TrySetResult();
            }

            return;
        }

        var failure = CreateFaultException(disposalFault.Value);
        _completion.TrySetException(failure);
        throw failure;
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_invalidationSync)
        {
            return generation == Volatile.Read(ref _latestGeneration)
                && generation == _sink.PrivacyObservationGeneration;
        }
    }

    private bool TryBeginTerminalTransitionUnderLock()
    {
        if (Interlocked.CompareExchange(
                ref _terminalFault,
                TerminalTransitionPending,
                comparand: 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _acceptCallbacks, 0);
        return true;
    }

    private void CompleteTerminalTransitionUnderLock(
        WindowsCapturePrivacyMonitorFault fault)
    {
        if (!Enum.IsDefined(fault))
        {
            throw new ArgumentOutOfRangeException(nameof(fault));
        }

        var previous = Interlocked.CompareExchange(
            ref _terminalFault,
            (int)fault,
            TerminalTransitionPending);
        if (previous != TerminalTransitionPending)
        {
            throw new InvalidOperationException(
                "No privacy monitor terminal transition is pending.");
        }

        _terminalFaultCommitted.TrySetResult(fault);
    }

    private WindowsCapturePrivacyMonitorFault? GetTerminalFault()
    {
        var fault = Volatile.Read(ref _terminalFault);
        return fault <= 0
            ? null
            : (WindowsCapturePrivacyMonitorFault)fault;
    }

    private bool IsTerminalTransitionPending() =>
        Volatile.Read(ref _terminalFault) == TerminalTransitionPending;

    private bool IsTerminalTransitionStarted() =>
        Volatile.Read(ref _terminalFault) != 0;

    private async Task<long> ApplyLatestPrivacyInvalidationAsync()
    {
        while (true)
        {
            var generation = Volatile.Read(ref _latestGeneration);
            if (generation <= 0)
            {
                return 0;
            }

            await _sink
                .ApplyPrivacyInvalidationAsync(
                    generation,
                    CancellationToken.None)
                .ConfigureAwait(false);

            var latestGeneration = Volatile.Read(ref _latestGeneration);
            var sinkGeneration = _sink.PrivacyObservationGeneration;
            if (generation == latestGeneration
                && generation == sinkGeneration)
            {
                return generation;
            }

            if (generation == latestGeneration)
            {
                throw new InvalidOperationException(
                    "The privacy observation generations are desynchronized.");
            }
        }
    }

    private Task? TryCaptureStartedSinkTermination()
    {
        try
        {
            if (_sink is INativeCapturePrivacySignalSinkTermination termination
                && termination.IsTerminationStarted)
            {
                _ = Interlocked.CompareExchange(
                    ref _sinkTerminationProof,
                    termination.Termination,
                    comparand: null);
                return Volatile.Read(ref _sinkTerminationProof);
            }
        }
        catch
        {
        }

        return null;
    }

    private static WindowsCapturePrivacyMonitorException CreateFaultException(
        WindowsCapturePrivacyMonitorFault fault)
    {
        return new WindowsCapturePrivacyMonitorException(fault);
    }

    private void WakeWorker()
    {
        _workerWake.Writer.TryWrite(0);
    }

    private static void AdvanceGeneration(ref long location, long generation)
    {
        if (generation <= 0)
        {
            throw new InvalidOperationException(
                "The privacy observation generation must be positive.");
        }

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

    private void ApplyHoldChangeUnderLock(WindowsCaptureWinEventChange change)
    {
        switch (change)
        {
            case WindowsCaptureWinEventChange.SessionUnavailable:
                _activeHolds |= WindowsCapturePrivacyHold.SessionUnavailable;
                break;
            case WindowsCaptureWinEventChange.SessionAvailable:
                _activeHolds &= ~WindowsCapturePrivacyHold.SessionUnavailable;
                break;
            case WindowsCaptureWinEventChange.PowerSuspending:
                _activeHolds |= WindowsCapturePrivacyHold.PowerSuspended;
                break;
            case WindowsCaptureWinEventChange.PowerResumed:
                _activeHolds &= ~WindowsCapturePrivacyHold.PowerSuspended;
                break;
        }
    }

    private enum DisplayComparison
    {
        Unknown = 0,
        Same = 1,
        Different = 2,
    }

    private readonly record struct DisplayRevalidationRequest(
        long Version,
        WindowsCaptureWinEventChange Change,
        ulong WindowHandle,
        bool RequireCandidateMatch);

    private bool ShouldInvalidateUnderLock(
        WindowsCaptureWinEventNotification notification)
    {
        if (IsAllowAllApplicationsMode())
        {
            if (IsDisplayWideCapturePinned())
            {
                var displayWideDecision =
                    ShouldInvalidatePinnedDisplayTargetUnderLock(notification);
                if (displayWideDecision.HasValue)
                {
                    return displayWideDecision.Value;
                }
            }

            Interlocked.Increment(ref _displayRevalidationVersion);
        }

        if (notification.Change == WindowsCaptureWinEventChange.Foreground)
        {
            var candidateWindowHandle = EncodeWindowHandle(
                notification.WindowHandle);
            _objectEventTargetWindowHandle = candidateWindowHandle;
            _pendingForegroundTargetWindowHandle = candidateWindowHandle;
            return true;
        }

        if (notification.Change == WindowsCaptureWinEventChange.DesktopSwitch)
        {
            _objectEventTargetWindowHandle = 0;
            _pendingForegroundTargetWindowHandle = 0;
            return true;
        }

        if (!IsObjectChange(notification.Change))
        {
            return true;
        }

        var eventWindowHandle = EncodeWindowHandle(notification.WindowHandle);
        var targetWindowHandle = _objectEventTargetWindowHandle;
        if (eventWindowHandle == 0 || targetWindowHandle == 0)
        {
            return true;
        }

        if (eventWindowHandle != targetWindowHandle)
        {
            return false;
        }

        if (notification.Change == WindowsCaptureWinEventChange.ObjectDestroyed)
        {
            _objectEventTargetWindowHandle = 0;
            if (_pendingForegroundTargetWindowHandle == eventWindowHandle)
            {
                _pendingForegroundTargetWindowHandle = 0;
            }
        }

        return true;
    }

    private bool? ShouldInvalidatePinnedDisplayTargetUnderLock(
        WindowsCaptureWinEventNotification notification)
    {
        var candidateWindowHandle = EncodeWindowHandle(
            notification.WindowHandle);
        switch (notification.Change)
        {
            case WindowsCaptureWinEventChange.Foreground:
                if (candidateWindowHandle != 0)
                {
                    _objectEventTargetWindowHandle = candidateWindowHandle;
                }

                _pendingForegroundTargetWindowHandle = 0;
                return false;
            case WindowsCaptureWinEventChange.ObjectDestroyed:
                if (candidateWindowHandle != 0
                    && candidateWindowHandle == _objectEventTargetWindowHandle)
                {
                    _objectEventTargetWindowHandle = 0;
                    _pendingForegroundTargetWindowHandle = 0;
                }

                return false;
            case WindowsCaptureWinEventChange.ObjectCreated:
            case WindowsCaptureWinEventChange.ObjectNameChanged:
            case WindowsCaptureWinEventChange.ObjectLocationChanged:
                return false;
            default:
                return null;
        }
    }

    private DisplayComparison CompareAuthorizedDisplayUnderLock(
        ulong windowHandle)
    {
        if (windowHandle == 0
            || _lastObservation.DisplayTarget.State
                != WindowsCaptureDisplayTargetState.Present)
        {
            return DisplayComparison.Unknown;
        }

        try
        {
            if (!_resolveDisplayTarget(windowHandle, out var first)
                || !first.IsValid
                || !_resolveDisplayTarget(windowHandle, out var second)
                || !second.IsValid
                || !first.Equals(second))
            {
                return DisplayComparison.Unknown;
            }

            var authorized = _lastObservation.DisplayTarget;
            return authorized.MonitorHandle == second.MonitorHandle
                && string.Equals(
                    authorized.DeviceKey,
                    second.DeviceKey,
                    StringComparison.OrdinalIgnoreCase)
                        ? DisplayComparison.Same
                        : DisplayComparison.Different;
        }
        catch
        {
            return DisplayComparison.Unknown;
        }
    }

    private void ScheduleDisplayRevalidationUnderLock(
        DisplayRevalidationRequest request)
    {
        if (!_displayRevalidationRequests.Writer.TryWrite(request))
        {
            throw new InvalidOperationException(
                "The display revalidation queue is closed.");
        }
    }

    private void TryCommitPublishedObservation(
        long generation,
        WindowsCapturePrivacyObservation observation)
    {
        lock (_invalidationSync)
        {
            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            Volatile.Write(ref _lastObservation, observation);
            Volatile.Write(ref _lastPublishedGeneration, generation);
            Volatile.Write(ref _lastProcessedGeneration, generation);
            if (_activeHolds == WindowsCapturePrivacyHold.None)
            {
                _lastStorageDecision =
                    (int)observation.Signals.StorageAvailable;
            }

            Interlocked.Increment(ref _displayRevalidationVersion);
            if (observation.Signals.Target.State
                == NativeCaptureTargetIdentityState.Present)
            {
                var observedWindowHandle = EncodeWindowHandle(
                    observation.Signals.Target.WindowHandle);
                _objectEventTargetWindowHandle = observedWindowHandle;
                if (_pendingForegroundTargetWindowHandle == observedWindowHandle)
                {
                    _pendingForegroundTargetWindowHandle = 0;
                }
            }
        }
    }

    private void LogObservation(
        long generation,
        WindowsCapturePrivacyObservation observation)
    {
        var signals = observation.Signals;
        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.PrivacySampled,
            new(CaptureDiagnosticField.Generation, generation),
            new(CaptureDiagnosticField.TargetState, (long)signals.Target.State),
            new(
                CaptureDiagnosticField.DisplayState,
                (long)observation.DisplayTarget.State),
            new(
                CaptureDiagnosticField.SessionUnlocked,
                (long)signals.SessionUnlocked),
            new(
                CaptureDiagnosticField.SecureDesktopClear,
                (long)signals.SecureDesktopClear),
            new(
                CaptureDiagnosticField.RemoteSession,
                (long)signals.RemoteSession),
            new(
                CaptureDiagnosticField.PresentationMode,
                (long)signals.PresentationMode),
            new(
                CaptureDiagnosticField.ApplicationAllowed,
                (long)signals.ApplicationAllowed),
            new(
                CaptureDiagnosticField.WindowAllowed,
                (long)signals.WindowAllowed),
            new(
                CaptureDiagnosticField.StorageAvailable,
                (long)signals.StorageAvailable));
    }

    private void LogPublication(long generation, bool accepted)
    {
        _diagnosticLog?.Write(
            CaptureDiagnosticEvent.PrivacyPublished,
            new(CaptureDiagnosticField.Generation, generation),
            new(CaptureDiagnosticField.Accepted, accepted ? 1 : 0));
    }

    private static bool IsObjectChange(WindowsCaptureWinEventChange change)
    {
        return change is WindowsCaptureWinEventChange.ObjectCreated
            or WindowsCaptureWinEventChange.ObjectDestroyed
            or WindowsCaptureWinEventChange.ObjectNameChanged
            or WindowsCaptureWinEventChange.ObjectLocationChanged;
    }

    private bool IsAllowAllApplicationsMode()
    {
        return _applicationPrivacyModeSource?.ApplicationPrivacyMode
            == CaptureApplicationPrivacyMode.AllowAllApplications;
    }

    private bool IsDisplayWideCapturePinned()
    {
        return _applicationPrivacyModeSource?.CurrentCaptureState switch
        {
            null => false,
            CaptureState.Unavailable or
            CaptureState.Stopped or
            CaptureState.BlockedByConsent => false,
            _ => true,
        };
    }

    private void SubscribeApplicationPrivacyModeChanges()
    {
        if (_applicationPrivacyModeSource is null
            || Interlocked.Exchange(
                ref _applicationPrivacyModeSubscribed,
                1) != 0)
        {
            return;
        }

        _applicationPrivacyModeSource.ApplicationPrivacyModeChanged +=
            OnApplicationPrivacyModeChanged;
    }

    private void UnsubscribeApplicationPrivacyModeChanges()
    {
        if (_applicationPrivacyModeSource is null
            || Interlocked.Exchange(
                ref _applicationPrivacyModeSubscribed,
                0) == 0)
        {
            return;
        }

        _applicationPrivacyModeSource.ApplicationPrivacyModeChanged -=
            OnApplicationPrivacyModeChanged;
    }

    private static long EncodeWindowHandle(ulong windowHandle) =>
        unchecked((long)windowHandle);

    private bool DoesNotMatchCurrentTargetCandidate(
        long generation,
        WindowsCapturePrivacyObservation observation)
    {
        lock (_invalidationSync)
        {
            if (!IsCurrentGeneration(generation)
                || _pendingForegroundTargetWindowHandle == 0)
            {
                return false;
            }

            var target = observation.Signals.Target;
            return target.State == NativeCaptureTargetIdentityState.Present
                && EncodeWindowHandle(target.WindowHandle)
                    != _pendingForegroundTargetWindowHandle;
        }
    }

    private static bool IsTransientTargetObservation(
        WindowsCapturePrivacyObservation observation)
    {
        return observation.Signals.Target.State switch
        {
            NativeCaptureTargetIdentityState.Unknown =>
                observation.DisplayTarget.State
                    == WindowsCaptureDisplayTargetState.Unknown,
            NativeCaptureTargetIdentityState.Absent =>
                observation.DisplayTarget.State
                    == WindowsCaptureDisplayTargetState.Absent,
            _ => false,
        };
    }

    internal static bool IsRecoverableTransientTargetObservation(
        WindowsCapturePrivacyObservation observation)
    {
        if (!IsTransientTargetObservation(observation))
        {
            return false;
        }

        var signals = observation.Signals;
        return signals.SessionUnlocked != NativeCapturePolicyDecision.Block
            && signals.SecureDesktopClear != NativeCapturePolicyDecision.Block
            && signals.ApplicationAllowed != NativeCapturePolicyDecision.Block
            && signals.WindowAllowed != NativeCapturePolicyDecision.Block
            && signals.StorageAvailable != NativeCapturePolicyDecision.Block;
    }

    private ObservationResolutionDirective CaptureResolutionDirective(
        long generation)
    {
        lock (_invalidationSync)
        {
            if (generation != Volatile.Read(ref _latestGeneration)
                || generation != _holdGeneration)
            {
                return ObservationResolutionDirective.Stale;
            }

            return _activeHolds == WindowsCapturePrivacyHold.None
                ? ObservationResolutionDirective.Sample
                : ObservationResolutionDirective.PublishFailClosed;
        }
    }

    private static WindowsCapturePrivacyInvalidationReason MapReason(
        WindowsCaptureWinEventChange change)
    {
        return change switch
        {
            WindowsCaptureWinEventChange.Foreground =>
                WindowsCapturePrivacyInvalidationReason.Foreground,
            WindowsCaptureWinEventChange.DesktopSwitch =>
                WindowsCapturePrivacyInvalidationReason.DesktopSwitch,
            WindowsCaptureWinEventChange.ObjectCreated =>
                WindowsCapturePrivacyInvalidationReason.ObjectCreated,
            WindowsCaptureWinEventChange.ObjectDestroyed =>
                WindowsCapturePrivacyInvalidationReason.ObjectDestroyed,
            WindowsCaptureWinEventChange.ObjectNameChanged =>
                WindowsCapturePrivacyInvalidationReason.ObjectNameChanged,
            WindowsCaptureWinEventChange.ObjectLocationChanged =>
                WindowsCapturePrivacyInvalidationReason.ObjectLocationChanged,
            WindowsCaptureWinEventChange.DisplayTopologyChanged =>
                WindowsCapturePrivacyInvalidationReason.DisplayTopologyChanged,
            WindowsCaptureWinEventChange.SessionUnavailable =>
                WindowsCapturePrivacyInvalidationReason.SessionUnavailable,
            WindowsCaptureWinEventChange.SessionAvailable =>
                WindowsCapturePrivacyInvalidationReason.SessionAvailable,
            WindowsCaptureWinEventChange.SessionChanged =>
                WindowsCapturePrivacyInvalidationReason.SessionChanged,
            WindowsCaptureWinEventChange.PowerSuspending =>
                WindowsCapturePrivacyInvalidationReason.PowerSuspending,
            WindowsCaptureWinEventChange.PowerResumed =>
                WindowsCapturePrivacyInvalidationReason.PowerResumed,
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
    }

    private enum ObservationResolutionDirective
    {
        Stale = 0,
        Sample = 1,
        PublishFailClosed = 2,
    }

    private static bool IsRecoverableSampleException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or Win32Exception
            or PlatformNotSupportedException;
    }
}
