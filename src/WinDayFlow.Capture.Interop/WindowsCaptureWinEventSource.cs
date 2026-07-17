using System.Runtime.InteropServices;

namespace WinDayFlow.Capture.Interop;

internal enum WindowsCaptureWinEventChange
{
    Foreground = 1,
    DesktopSwitch = 2,
    ObjectCreated = 3,
    ObjectDestroyed = 4,
    ObjectNameChanged = 5,
    ObjectLocationChanged = 6,
    DisplayTopologyChanged = 7,
    SessionUnavailable = 8,
    SessionAvailable = 9,
    SessionChanged = 10,
    PowerSuspending = 11,
    PowerResumed = 12,
}

internal enum WindowsCaptureWinEventSourceFault
{
    UnsupportedPlatform = 1,
    ThreadStartFailed = 2,
    HookRegistrationFailed = 3,
    MessageLoopFailed = 4,
    CallbackFailed = 5,
    StopWakeFailed = 6,
    HookUnregistrationFailed = 7,
    StartupTimedOut = 8,
    StopTimedOut = 9,
    WindowClassRegistrationFailed = 10,
    WindowCreationFailed = 11,
    SessionRegistrationFailed = 12,
    PowerRegistrationFailed = 13,
    SessionUnregistrationFailed = 14,
    PowerUnregistrationFailed = 15,
    WindowDestructionFailed = 16,
    WindowClassUnregistrationFailed = 17,
}

internal interface IWindowsCaptureEventSource : IDisposable
{
    void Start(
        Action<WindowsCaptureWinEventChange> changeCallback,
        Action<WindowsCaptureWinEventSourceFault> faultCallback);
}

internal sealed class WindowsCaptureWinEventSource : IWindowsCaptureEventSource
{
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemDesktopSwitch = 0x0020;
    internal const uint EventObjectCreate = 0x8000;
    internal const uint EventObjectDestroy = 0x8001;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const uint EventObjectNameChange = 0x800C;
    internal const int ObjectIdWindow = 0;
    internal const int ChildIdSelf = 0;
    internal const uint WinEventOutOfContext = 0x0000;
    internal const uint StopThreadMessage = 0x84D1;
    internal const uint WindowMessageDisplayChange = 0x007E;
    internal const uint WindowMessagePowerBroadcast = 0x0218;
    internal const uint WindowMessageWtsSessionChange = 0x02B1;
    internal const nuint WtsConsoleConnect = 0x1;
    internal const nuint WtsConsoleDisconnect = 0x2;
    internal const nuint WtsRemoteConnect = 0x3;
    internal const nuint WtsRemoteDisconnect = 0x4;
    internal const nuint WtsSessionLogon = 0x5;
    internal const nuint WtsSessionLogoff = 0x6;
    internal const nuint WtsSessionLock = 0x7;
    internal const nuint WtsSessionUnlock = 0x8;
    internal const nuint WtsSessionRemoteControl = 0x9;
    internal const nuint WtsSessionCreate = 0xA;
    internal const nuint WtsSessionTerminate = 0xB;
    internal const nuint WtsSessionDesktopReady = 0xF;
    internal const nuint PowerBroadcastSuspend = 0x0004;
    internal const nuint PowerBroadcastResumeCritical = 0x0006;
    internal const nuint PowerBroadcastResumeSuspend = 0x0007;
    internal const nuint PowerBroadcastResumeAutomatic = 0x0012;
    internal const nint HandledMessageResult = 0;
    internal const nint HandledPowerBroadcastResult = 1;

    private const string ThreadName = "WinDayFlow.WinEvent";
    private const int DefaultThreadTransitionTimeoutMilliseconds = 5_000;
    private const int MaximumDrainMessages = 4_096;

    private readonly object _lifecycleSync = new();
    private readonly IWindowsCaptureWinEventNativeApi _nativeApi;
    private readonly int _threadTransitionTimeoutMilliseconds;
    private Thread? _thread;
    private WindowsCaptureWinEventCallbackBridge? _bridge;
    private uint _ownerThreadId;
    private int _startupCleanupFault;
    private bool _startAttempted;
    private bool _disposed;

    internal WindowsCaptureWinEventSource()
        : this(
            PInvokeWindowsCaptureWinEventNativeApi.Instance,
            DefaultThreadTransitionTimeoutMilliseconds)
    {
    }

    internal WindowsCaptureWinEventSource(
        IWindowsCaptureWinEventNativeApi nativeApi,
        int threadTransitionTimeoutMilliseconds =
            DefaultThreadTransitionTimeoutMilliseconds)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            threadTransitionTimeoutMilliseconds,
            1);
        _threadTransitionTimeoutMilliseconds =
            threadTransitionTimeoutMilliseconds;
    }

    internal bool HasRetainedCallbackBridge =>
        Volatile.Read(ref _bridge)?.HasRetainedRoot == true;

    public void Start(
        Action<WindowsCaptureWinEventChange> changeCallback,
        Action<WindowsCaptureWinEventSourceFault> faultCallback)
    {
        ArgumentNullException.ThrowIfNull(changeCallback);
        ArgumentNullException.ThrowIfNull(faultCallback);

        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startAttempted)
            {
                throw new InvalidOperationException(
                    "The Windows capture WinEvent source can only be started once.");
            }

            _startAttempted = true;
            if (!_nativeApi.IsSupportedPlatform)
            {
                InvokeFaultCallback(
                    faultCallback,
                    WindowsCaptureWinEventSourceFault.UnsupportedPlatform);
                throw new PlatformNotSupportedException(
                    "The Windows capture WinEvent source requires Windows 10 version 1809 or later.");
            }

            var bridge = new WindowsCaptureWinEventCallbackBridge(
                changeCallback,
                faultCallback,
                () => TryPostStop(Volatile.Read(ref _ownerThreadId)),
                _nativeApi.DefWindowProc);
            bridge.Root();
            _bridge = bridge;

            var startup = new TaskCompletionSource<
                WindowsCaptureWinEventSourceFault?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => RunOwnerThread(bridge, startup))
            {
                IsBackground = true,
                Name = ThreadName,
            };
            _thread = thread;

            try
            {
                thread.Start();
            }
            catch
            {
                bridge.ReportFault(
                    WindowsCaptureWinEventSourceFault.ThreadStartFailed);
                bridge.CompleteCleanup(retainRoot: false);
                throw new InvalidOperationException(
                    "The Windows capture WinEvent source thread could not start.");
            }

            if (!startup.Task.Wait(_threadTransitionTimeoutMilliseconds))
            {
                bridge.BeginStop();
                bridge.ReportFault(
                    WindowsCaptureWinEventSourceFault.StartupTimedOut);
                TryWakeOwnerThread(bridge, thread);
                if (!thread.Join(_threadTransitionTimeoutMilliseconds))
                {
                    bridge.CompleteCleanup(retainRoot: true);
                }

                throw new InvalidOperationException(
                    "The Windows capture WinEvent source startup timed out.");
            }

            if (startup.Task.GetAwaiter().GetResult() is { } startupFailure)
            {
                if (!thread.Join(_threadTransitionTimeoutMilliseconds))
                {
                    bridge.ReportFault(
                        WindowsCaptureWinEventSourceFault.StartupTimedOut);
                    bridge.CompleteCleanup(retainRoot: true);
                }

                InvokeFaultCallback(
                    faultCallback,
                    startupFailure);
                if (Volatile.Read(ref _startupCleanupFault) is > 0
                    and var cleanupFault)
                {
                    InvokeFaultCallback(
                        faultCallback,
                        (WindowsCaptureWinEventSourceFault)cleanupFault);
                }

                throw new InvalidOperationException(
                    "The Windows capture event source could not register every required system notification.");
            }
        }
    }

    public void Dispose()
    {
        Thread? thread;
        WindowsCaptureWinEventCallbackBridge? bridge;
        uint ownerThreadId;
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            bridge = _bridge;
            ownerThreadId = Volatile.Read(ref _ownerThreadId);
            bridge?.BeginStop();
        }

        if (thread is not null && thread.IsAlive)
        {
            if (!TryPostStop(ownerThreadId))
            {
                bridge?.ReportFault(
                    WindowsCaptureWinEventSourceFault.StopWakeFailed);
                bridge?.CompleteCleanup(retainRoot: true);
                return;
            }

            if (!ReferenceEquals(Thread.CurrentThread, thread)
                && !thread.Join(_threadTransitionTimeoutMilliseconds))
            {
                bridge?.ReportFault(
                    WindowsCaptureWinEventSourceFault.StopTimedOut);
                bridge?.CompleteCleanup(retainRoot: true);
                return;
            }
        }

        GC.SuppressFinalize(this);
    }

    private void TryWakeOwnerThread(
        WindowsCaptureWinEventCallbackBridge bridge,
        Thread thread)
    {
        if (!thread.IsAlive)
        {
            return;
        }

        if (!TryPostStop(Volatile.Read(ref _ownerThreadId)))
        {
            bridge.ReportFault(
                WindowsCaptureWinEventSourceFault.StopWakeFailed);
            bridge.CompleteCleanup(retainRoot: true);
        }
    }

    private bool TryPostStop(uint ownerThreadId)
    {
        if (ownerThreadId == 0)
        {
            return false;
        }

        try
        {
            return _nativeApi.PostThreadMessage(
                ownerThreadId,
                StopThreadMessage,
                0,
                0);
        }
        catch
        {
            return false;
        }
    }

    private void RunOwnerThread(
        WindowsCaptureWinEventCallbackBridge bridge,
        TaskCompletionSource<WindowsCaptureWinEventSourceFault?> startup)
    {
        var hooks = new List<nint>(capacity: 4);
        var windowClassRegistered = false;
        var hiddenWindow = (nint)0;
        var sessionNotificationsRegistered = false;
        var suspendResumeNotifications = (nint)0;
        WindowsCaptureWinEventSourceFault? startupFailure = null;
        var startupCompleted = false;
        var cleanUnhook = true;
        var cleanWindowCallbackLifetime = true;
        try
        {
            var ownerThreadId = _nativeApi.GetCurrentThreadId();
            Volatile.Write(ref _ownerThreadId, ownerThreadId);
            _nativeApi.EnsureMessageQueue();

            if (!_nativeApi.RegisterWindowClass(bridge.WindowProcedure))
            {
                startupFailure = WindowsCaptureWinEventSourceFault
                    .WindowClassRegistrationFailed;
                bridge.BeginStop();
                return;
            }

            windowClassRegistered = true;
            hiddenWindow = _nativeApi.CreateHiddenWindow();
            if (hiddenWindow == 0)
            {
                startupFailure = WindowsCaptureWinEventSourceFault
                    .WindowCreationFailed;
                bridge.BeginStop();
                return;
            }

            if (!_nativeApi.RegisterSessionNotifications(hiddenWindow))
            {
                startupFailure = WindowsCaptureWinEventSourceFault
                    .SessionRegistrationFailed;
                bridge.BeginStop();
                return;
            }

            sessionNotificationsRegistered = true;
            suspendResumeNotifications =
                _nativeApi.RegisterSuspendResumeNotifications(hiddenWindow);
            if (suspendResumeNotifications == 0)
            {
                startupFailure = WindowsCaptureWinEventSourceFault
                    .PowerRegistrationFailed;
                bridge.BeginStop();
                return;
            }

            if (!TryRegisterHooks(bridge.Callback, hooks))
            {
                startupFailure = WindowsCaptureWinEventSourceFault
                    .HookRegistrationFailed;
                bridge.BeginStop();
                return;
            }

            if (!bridge.TryBeginAcceptingChanges())
            {
                startupFailure = WindowsCaptureWinEventSourceFault.CallbackFailed;
                return;
            }

            startupCompleted = true;
            startup.TrySetResult(null);
            RunMessageLoop(bridge);
        }
        catch
        {
            if (startupCompleted)
            {
                bridge.ReportFault(
                    WindowsCaptureWinEventSourceFault.MessageLoopFailed);
            }
            else
            {
                startupFailure ??= WindowsCaptureWinEventSourceFault
                    .MessageLoopFailed;
            }
        }
        finally
        {
            bridge.BeginStop();
            for (var index = hooks.Count - 1; index >= 0; index--)
            {
                bool unhooked;
                try
                {
                    unhooked = _nativeApi.UnhookWinEvent(hooks[index]);
                }
                catch
                {
                    unhooked = false;
                }

                cleanUnhook &= unhooked;
            }

            if (!cleanUnhook)
            {
                ReportCleanupFault(
                    WindowsCaptureWinEventSourceFault.HookUnregistrationFailed,
                    startupCompleted,
                    bridge);
            }

            DrainMessageQueue();

            if (suspendResumeNotifications != 0)
            {
                var unregistered = TryCleanup(() =>
                    _nativeApi.UnregisterSuspendResumeNotifications(
                        suspendResumeNotifications));
                if (!unregistered)
                {
                    ReportCleanupFault(
                        WindowsCaptureWinEventSourceFault
                            .PowerUnregistrationFailed,
                        startupCompleted,
                        bridge);
                }
            }

            if (sessionNotificationsRegistered)
            {
                var unregistered = TryCleanup(() =>
                    _nativeApi.UnregisterSessionNotifications(hiddenWindow));
                if (!unregistered)
                {
                    ReportCleanupFault(
                        WindowsCaptureWinEventSourceFault
                            .SessionUnregistrationFailed,
                        startupCompleted,
                        bridge);
                }
            }

            if (hiddenWindow != 0)
            {
                var destroyed = TryCleanup(() =>
                    _nativeApi.DestroyWindow(hiddenWindow));
                cleanWindowCallbackLifetime &= destroyed;
                if (!destroyed)
                {
                    ReportCleanupFault(
                        WindowsCaptureWinEventSourceFault
                            .WindowDestructionFailed,
                        startupCompleted,
                        bridge);
                }
            }

            if (windowClassRegistered)
            {
                var unregistered = TryCleanup(
                    _nativeApi.UnregisterWindowClass);
                cleanWindowCallbackLifetime &= unregistered;
                if (!unregistered)
                {
                    ReportCleanupFault(
                        WindowsCaptureWinEventSourceFault
                            .WindowClassUnregistrationFailed,
                        startupCompleted,
                        bridge);
                }
            }

            bridge.CompleteCleanup(
                retainRoot: !cleanUnhook || !cleanWindowCallbackLifetime);
            Volatile.Write(ref _ownerThreadId, 0);
            if (!startupCompleted)
            {
                startup.TrySetResult(
                    startupFailure
                    ?? WindowsCaptureWinEventSourceFault.MessageLoopFailed);
            }

            GC.KeepAlive(bridge);
        }
    }

    private void ReportCleanupFault(
        WindowsCaptureWinEventSourceFault fault,
        bool startupCompleted,
        WindowsCaptureWinEventCallbackBridge bridge)
    {
        if (startupCompleted)
        {
            bridge.ReportFault(fault);
            return;
        }

        Interlocked.CompareExchange(
            ref _startupCleanupFault,
            (int)fault,
            comparand: 0);
    }

    private static bool TryCleanup(Func<bool> cleanup)
    {
        try
        {
            return cleanup();
        }
        catch
        {
            return false;
        }
    }

    private bool TryRegisterHooks(
        WindowsCaptureWinEventProc callback,
        List<nint> hooks)
    {
        return TryRegisterHook(
                EventSystemForeground,
                EventSystemForeground,
                callback,
                hooks)
            && TryRegisterHook(
                EventSystemDesktopSwitch,
                EventSystemDesktopSwitch,
                callback,
                hooks)
            && TryRegisterHook(
                EventObjectCreate,
                EventObjectDestroy,
                callback,
                hooks)
            && TryRegisterHook(
                EventObjectLocationChange,
                EventObjectNameChange,
                callback,
                hooks);
    }

    private bool TryRegisterHook(
        uint eventMinimum,
        uint eventMaximum,
        WindowsCaptureWinEventProc callback,
        List<nint> hooks)
    {
        var hook = _nativeApi.SetWinEventHook(
            eventMinimum,
            eventMaximum,
            0,
            callback,
            processId: 0,
            threadId: 0,
            WinEventOutOfContext);
        if (hook == 0)
        {
            return false;
        }

        hooks.Add(hook);
        return true;
    }

    private void RunMessageLoop(WindowsCaptureWinEventCallbackBridge bridge)
    {
        while (true)
        {
            var result = _nativeApi.GetMessage(out var message);
            if (result == -1)
            {
                bridge.ReportFault(
                    WindowsCaptureWinEventSourceFault.MessageLoopFailed);
                return;
            }

            if (result == 0)
            {
                bridge.ReportFault(
                    WindowsCaptureWinEventSourceFault.MessageLoopFailed);
                return;
            }

            if (message.Message == StopThreadMessage)
            {
                return;
            }

            _nativeApi.TranslateAndDispatchMessage(in message);
        }
    }

    private void DrainMessageQueue()
    {
        for (var count = 0;
             count < MaximumDrainMessages
             && _nativeApi.TryRemoveMessage(out var message);
             count++)
        {
            if (message.Message != StopThreadMessage)
            {
                _nativeApi.TranslateAndDispatchMessage(in message);
            }
        }
    }

    private static void InvokeFaultCallback(
        Action<WindowsCaptureWinEventSourceFault> callback,
        WindowsCaptureWinEventSourceFault fault)
    {
        try
        {
            callback(fault);
        }
        catch
        {
        }
    }
}

internal sealed class WindowsCaptureWinEventCallbackBridge
{
    private readonly object _cleanupSync = new();
    private Func<bool>? _requestOwnerStop;
    private WindowsCaptureDefWindowProc? _defWindowProc;
    private Action<WindowsCaptureWinEventChange>? _changeCallback;
    private Action<WindowsCaptureWinEventSourceFault>? _faultCallback;
    private GCHandle _selfRoot;
    private int _acceptChanges;
    private int _cleanupCompleted;
    private int _retainedRoot;

    internal WindowsCaptureWinEventCallbackBridge(
        Action<WindowsCaptureWinEventChange> changeCallback,
        Action<WindowsCaptureWinEventSourceFault> faultCallback,
        Func<bool> requestOwnerStop,
        WindowsCaptureDefWindowProc defWindowProc)
    {
        _changeCallback = changeCallback
            ?? throw new ArgumentNullException(nameof(changeCallback));
        _faultCallback = faultCallback
            ?? throw new ArgumentNullException(nameof(faultCallback));
        _requestOwnerStop = requestOwnerStop
            ?? throw new ArgumentNullException(nameof(requestOwnerStop));
        _defWindowProc = defWindowProc
            ?? throw new ArgumentNullException(nameof(defWindowProc));
        Callback = OnWinEvent;
        WindowProcedure = OnWindowMessage;
    }

    internal WindowsCaptureWinEventProc Callback { get; }

    internal WindowsCaptureWindowProc WindowProcedure { get; }

    internal bool IsAcceptingChanges =>
        Volatile.Read(ref _acceptChanges) == 1;

    internal bool HasRetainedRoot => Volatile.Read(ref _retainedRoot) != 0;

    internal void Root()
    {
        _selfRoot = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    internal void BeginStop()
    {
        Interlocked.Exchange(ref _acceptChanges, -1);
    }

    internal bool TryBeginAcceptingChanges()
    {
        return Interlocked.CompareExchange(
            ref _acceptChanges,
            value: 1,
            comparand: 0) == 0;
    }

    internal void ReportFault(WindowsCaptureWinEventSourceFault fault)
    {
        var callback = Volatile.Read(ref _faultCallback);
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(fault);
        }
        catch
        {
        }
    }

    internal void CompleteCleanup(bool retainRoot)
    {
        lock (_cleanupSync)
        {
            BeginStop();
            Volatile.Write(ref _changeCallback, null);
            Volatile.Write(ref _faultCallback, null);
            Volatile.Write(ref _requestOwnerStop, null);
            Volatile.Write(ref _defWindowProc, null);
            if (retainRoot)
            {
                if (_cleanupCompleted == 0)
                {
                    Volatile.Write(ref _retainedRoot, 1);
                }

                return;
            }

            if (_cleanupCompleted != 0)
            {
                return;
            }

            _cleanupCompleted = 1;
            Volatile.Write(ref _retainedRoot, 0);
            if (_selfRoot.IsAllocated)
            {
                _selfRoot.Free();
            }
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTimeMilliseconds)
    {
        _ = hook;
        _ = eventThreadId;
        _ = eventTimeMilliseconds;
        try
        {
            if (Volatile.Read(ref _acceptChanges) != 1
                || !TryMapWinEventChange(
                    eventType,
                    windowHandle,
                    objectId,
                    childId,
                    out var change))
            {
                return;
            }

            PublishChange(change);
        }
        catch
        {
            HandleCallbackFailure();
        }
    }

    private nint OnWindowMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam)
    {
        try
        {
            if (Volatile.Read(ref _acceptChanges) == 1
                && TryMapWindowMessage(message, wParam, out var change))
            {
                PublishChange(change);
                return message == WindowsCaptureWinEventSource
                        .WindowMessagePowerBroadcast
                    ? WindowsCaptureWinEventSource.HandledPowerBroadcastResult
                    : WindowsCaptureWinEventSource.HandledMessageResult;
            }

            var defWindowProc = Volatile.Read(ref _defWindowProc);
            return defWindowProc is null
                ? 0
                : defWindowProc(windowHandle, message, wParam, lParam);
        }
        catch
        {
            HandleCallbackFailure();
            return message == WindowsCaptureWinEventSource
                    .WindowMessagePowerBroadcast
                ? WindowsCaptureWinEventSource.HandledPowerBroadcastResult
                : WindowsCaptureWinEventSource.HandledMessageResult;
        }
    }

    private void PublishChange(WindowsCaptureWinEventChange change)
    {
        var callback = Volatile.Read(ref _changeCallback);
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(change);
        }
        catch
        {
            HandleCallbackFailure();
        }
    }

    private void HandleCallbackFailure()
    {
        BeginStop();
        bool stopRequested;
        try
        {
            var requestOwnerStop = Volatile.Read(ref _requestOwnerStop);
            stopRequested = requestOwnerStop?.Invoke() == true;
        }
        catch
        {
            stopRequested = false;
        }

        ReportFault(WindowsCaptureWinEventSourceFault.CallbackFailed);
        if (stopRequested)
        {
            return;
        }

        ReportFault(WindowsCaptureWinEventSourceFault.StopWakeFailed);
        CompleteCleanup(retainRoot: true);
    }

    private static bool TryMapWinEventChange(
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        out WindowsCaptureWinEventChange change)
    {
        switch (eventType)
        {
            case WindowsCaptureWinEventSource.EventSystemForeground:
                change = WindowsCaptureWinEventChange.Foreground;
                return true;
            case WindowsCaptureWinEventSource.EventSystemDesktopSwitch:
                change = WindowsCaptureWinEventChange.DesktopSwitch;
                return true;
            case WindowsCaptureWinEventSource.EventObjectCreate:
                change = WindowsCaptureWinEventChange.ObjectCreated;
                break;
            case WindowsCaptureWinEventSource.EventObjectDestroy:
                change = WindowsCaptureWinEventChange.ObjectDestroyed;
                break;
            case WindowsCaptureWinEventSource.EventObjectLocationChange:
                change = WindowsCaptureWinEventChange.ObjectLocationChanged;
                break;
            case WindowsCaptureWinEventSource.EventObjectNameChange:
                change = WindowsCaptureWinEventChange.ObjectNameChanged;
                break;
            default:
                change = default;
                return false;
        }

        return windowHandle != 0
            && objectId == WindowsCaptureWinEventSource.ObjectIdWindow
            && childId == WindowsCaptureWinEventSource.ChildIdSelf;
    }

    private static bool TryMapWindowMessage(
        uint message,
        nuint wParam,
        out WindowsCaptureWinEventChange change)
    {
        switch (message)
        {
            case WindowsCaptureWinEventSource.WindowMessageDisplayChange:
                change = WindowsCaptureWinEventChange.DisplayTopologyChanged;
                return true;
            case WindowsCaptureWinEventSource.WindowMessageWtsSessionChange:
                return TryMapSessionChange(wParam, out change);
            case WindowsCaptureWinEventSource.WindowMessagePowerBroadcast:
                return TryMapPowerChange(wParam, out change);
            default:
                change = default;
                return false;
        }
    }

    private static bool TryMapSessionChange(
        nuint eventType,
        out WindowsCaptureWinEventChange change)
    {
        switch (eventType)
        {
            case WindowsCaptureWinEventSource.WtsConsoleDisconnect:
            case WindowsCaptureWinEventSource.WtsRemoteDisconnect:
            case WindowsCaptureWinEventSource.WtsSessionLogoff:
            case WindowsCaptureWinEventSource.WtsSessionLock:
            case WindowsCaptureWinEventSource.WtsSessionTerminate:
                change = WindowsCaptureWinEventChange.SessionUnavailable;
                return true;
            case WindowsCaptureWinEventSource.WtsConsoleConnect:
            case WindowsCaptureWinEventSource.WtsRemoteConnect:
            case WindowsCaptureWinEventSource.WtsSessionLogon:
            case WindowsCaptureWinEventSource.WtsSessionUnlock:
            case WindowsCaptureWinEventSource.WtsSessionCreate:
            case WindowsCaptureWinEventSource.WtsSessionDesktopReady:
                change = WindowsCaptureWinEventChange.SessionAvailable;
                return true;
            case WindowsCaptureWinEventSource.WtsSessionRemoteControl:
                change = WindowsCaptureWinEventChange.SessionChanged;
                return true;
            default:
                change = WindowsCaptureWinEventChange.SessionUnavailable;
                return true;
        }
    }

    private static bool TryMapPowerChange(
        nuint eventType,
        out WindowsCaptureWinEventChange change)
    {
        switch (eventType)
        {
            case WindowsCaptureWinEventSource.PowerBroadcastSuspend:
                change = WindowsCaptureWinEventChange.PowerSuspending;
                return true;
            case WindowsCaptureWinEventSource.PowerBroadcastResumeCritical:
            case WindowsCaptureWinEventSource.PowerBroadcastResumeSuspend:
            case WindowsCaptureWinEventSource.PowerBroadcastResumeAutomatic:
                change = WindowsCaptureWinEventChange.PowerResumed;
                return true;
            default:
                change = default;
                return false;
        }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void WindowsCaptureWinEventProc(
    nint hook,
    uint eventType,
    nint windowHandle,
    int objectId,
    int childId,
    uint eventThreadId,
    uint eventTimeMilliseconds);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate nint WindowsCaptureWindowProc(
    nint windowHandle,
    uint message,
    nuint wParam,
    nint lParam);

internal delegate nint WindowsCaptureDefWindowProc(
    nint windowHandle,
    uint message,
    nuint wParam,
    nint lParam);

internal interface IWindowsCaptureWinEventNativeApi
{
    bool IsSupportedPlatform { get; }

    uint GetCurrentThreadId();

    void EnsureMessageQueue();

    bool RegisterWindowClass(WindowsCaptureWindowProc windowProcedure);

    nint CreateHiddenWindow();

    bool DestroyWindow(nint windowHandle);

    bool UnregisterWindowClass();

    bool RegisterSessionNotifications(nint windowHandle);

    bool UnregisterSessionNotifications(nint windowHandle);

    nint RegisterSuspendResumeNotifications(nint windowHandle);

    bool UnregisterSuspendResumeNotifications(nint registrationHandle);

    nint DefWindowProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint callbackModule,
        WindowsCaptureWinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    bool UnhookWinEvent(nint hook);

    int GetMessage(out WindowsCaptureThreadMessage message);

    bool TryRemoveMessage(out WindowsCaptureThreadMessage message);

    void TranslateAndDispatchMessage(in WindowsCaptureThreadMessage message);

    bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);
}

internal sealed class PInvokeWindowsCaptureWinEventNativeApi
    : IWindowsCaptureWinEventNativeApi
{
    private const string WindowClassName =
        "WinDayFlow.Capture.SystemEventWindow";
    private const uint PeekMessageNoRemove = 0x0000;
    private const uint PeekMessageRemove = 0x0001;
    internal const uint WtsNotifyForThisSession = 0;
    internal const uint PowerDeviceNotifyWindowHandle = 0;
    internal const uint HiddenWindowStyle = 0x80000000;
    internal const uint HiddenWindowExtendedStyle = 0x00000080 | 0x08000000;
    internal const nint HiddenWindowParent = 0;

    private PInvokeWindowsCaptureWinEventNativeApi()
    {
    }

    internal static PInvokeWindowsCaptureWinEventNativeApi Instance { get; } = new();

    public bool IsSupportedPlatform =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public uint GetCurrentThreadId() =>
        WindowsCaptureWinEventMethods.GetCurrentThreadId();

    public void EnsureMessageQueue()
    {
        _ = WindowsCaptureWinEventMethods.PeekMessage(
            out _,
            0,
            0,
            0,
            PeekMessageNoRemove);
    }

    public bool RegisterWindowClass(WindowsCaptureWindowProc windowProcedure)
    {
        ArgumentNullException.ThrowIfNull(windowProcedure);
        var windowClass = new WindowsCaptureWindowClass
        {
            Size = checked((uint)Marshal.SizeOf<WindowsCaptureWindowClass>()),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(
                windowProcedure),
            Instance = WindowsCaptureWinEventMethods.GetModuleHandle(null),
            ClassName = WindowClassName,
        };
        return windowClass.Instance != 0
            && WindowsCaptureWinEventMethods.RegisterClassEx(ref windowClass) != 0;
    }

    public nint CreateHiddenWindow()
    {
        var instance = WindowsCaptureWinEventMethods.GetModuleHandle(null);
        if (instance == 0)
        {
            return 0;
        }

        return WindowsCaptureWinEventMethods.CreateWindowEx(
            HiddenWindowExtendedStyle,
            WindowClassName,
            string.Empty,
            HiddenWindowStyle,
            x: 0,
            y: 0,
            width: 0,
            height: 0,
            parent: HiddenWindowParent,
            menu: 0,
            instance,
            parameter: 0);
    }

    public bool DestroyWindow(nint windowHandle) =>
        WindowsCaptureWinEventMethods.DestroyWindow(windowHandle);

    public bool UnregisterWindowClass()
    {
        var instance = WindowsCaptureWinEventMethods.GetModuleHandle(null);
        return instance != 0
            && WindowsCaptureWinEventMethods.UnregisterClass(
                WindowClassName,
                instance);
    }

    public bool RegisterSessionNotifications(nint windowHandle) =>
        WindowsCaptureWinEventMethods.WtsRegisterSessionNotification(
            windowHandle,
            WtsNotifyForThisSession);

    public bool UnregisterSessionNotifications(nint windowHandle) =>
        WindowsCaptureWinEventMethods.WtsUnregisterSessionNotification(
            windowHandle);

    public nint RegisterSuspendResumeNotifications(nint windowHandle) =>
        WindowsCaptureWinEventMethods.RegisterSuspendResumeNotification(
            windowHandle,
            PowerDeviceNotifyWindowHandle);

    public bool UnregisterSuspendResumeNotifications(nint registrationHandle) =>
        WindowsCaptureWinEventMethods.UnregisterSuspendResumeNotification(
            registrationHandle);

    public nint DefWindowProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam) =>
        WindowsCaptureWinEventMethods.DefWindowProc(
            windowHandle,
            message,
            wParam,
            lParam);

    public nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint callbackModule,
        WindowsCaptureWinEventProc callback,
        uint processId,
        uint threadId,
        uint flags)
    {
        return WindowsCaptureWinEventMethods.SetWinEventHook(
            eventMinimum,
            eventMaximum,
            callbackModule,
            callback,
            processId,
            threadId,
            flags);
    }

    public bool UnhookWinEvent(nint hook) =>
        WindowsCaptureWinEventMethods.UnhookWinEvent(hook);

    public int GetMessage(out WindowsCaptureThreadMessage message) =>
        WindowsCaptureWinEventMethods.GetMessage(
            out message,
            0,
            0,
            0);

    public bool TryRemoveMessage(out WindowsCaptureThreadMessage message) =>
        WindowsCaptureWinEventMethods.PeekMessage(
            out message,
            0,
            0,
            0,
            PeekMessageRemove);

    public void TranslateAndDispatchMessage(
        in WindowsCaptureThreadMessage message)
    {
        _ = WindowsCaptureWinEventMethods.TranslateMessage(in message);
        _ = WindowsCaptureWinEventMethods.DispatchMessage(in message);
    }

    public bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam)
    {
        return WindowsCaptureWinEventMethods.PostThreadMessage(
            threadId,
            message,
            wParam,
            lParam);
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsCaptureWindowClass
{
    internal uint Size;
    internal uint Style;
    internal nint WindowProcedure;
    internal int ClassExtraBytes;
    internal int WindowExtraBytes;
    internal nint Instance;
    internal nint Icon;
    internal nint Cursor;
    internal nint BackgroundBrush;

    [MarshalAs(UnmanagedType.LPWStr)]
    internal string? MenuName;

    [MarshalAs(UnmanagedType.LPWStr)]
    internal string ClassName;

    internal nint SmallIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct WindowsCaptureThreadMessage
{
    internal readonly nint WindowHandle;
    internal readonly uint Message;
    internal readonly nuint WParam;
    internal readonly nint LParam;
    internal readonly uint Time;
    internal readonly WindowsCapturePoint Point;
    internal readonly uint Private;

    internal WindowsCaptureThreadMessage(uint message)
        : this(0, message, 0, 0)
    {
    }

    internal WindowsCaptureThreadMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam)
    {
        WindowHandle = windowHandle;
        Message = message;
        WParam = wParam;
        LParam = lParam;
        Time = 0;
        Point = default;
        Private = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct WindowsCapturePoint
{
    internal readonly int X;
    internal readonly int Y;
}

internal static class WindowsCaptureWinEventMethods
{
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern ushort RegisterClassEx(
        ref WindowsCaptureWindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClass(
        string className,
        nint instance);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW",
        ExactSpelling = true)]
    internal static extern nint DefWindowProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint RegisterSuspendResumeNotification(
        nint recipient,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterSuspendResumeNotification(
        nint registrationHandle);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WtsRegisterSessionNotification(
        nint windowHandle,
        uint flags);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WtsUnregisterSessionNotification(
        nint windowHandle);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint callbackModule,
        WindowsCaptureWinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true,
        SetLastError = true)]
    internal static extern int GetMessage(
        out WindowsCaptureThreadMessage message,
        nint windowHandle,
        uint messageFilterMinimum,
        uint messageFilterMaximum);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out WindowsCaptureThreadMessage message,
        nint windowHandle,
        uint messageFilterMinimum,
        uint messageFilterMaximum,
        uint removeMessage);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(
        in WindowsCaptureThreadMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)]
    internal static extern nint DispatchMessage(
        in WindowsCaptureThreadMessage message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetCurrentThreadId();
}
