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
                () => TryPostStop(Volatile.Read(ref _ownerThreadId)));
            bridge.Root();
            _bridge = bridge;

            var startup = new TaskCompletionSource<bool>(
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

            if (!startup.Task.GetAwaiter().GetResult())
            {
                if (!thread.Join(_threadTransitionTimeoutMilliseconds))
                {
                    bridge.ReportFault(
                        WindowsCaptureWinEventSourceFault.StartupTimedOut);
                    bridge.CompleteCleanup(retainRoot: true);
                }

                InvokeFaultCallback(
                    faultCallback,
                    WindowsCaptureWinEventSourceFault.HookRegistrationFailed);
                if (Volatile.Read(ref _startupCleanupFault) != 0)
                {
                    InvokeFaultCallback(
                        faultCallback,
                        WindowsCaptureWinEventSourceFault.HookUnregistrationFailed);
                }

                throw new InvalidOperationException(
                    "The Windows capture WinEvent source could not register its hooks.");
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
        TaskCompletionSource<bool> startup)
    {
        var hooks = new List<nint>(capacity: 4);
        var startupCompleted = false;
        var cleanUnhook = true;
        try
        {
            var ownerThreadId = _nativeApi.GetCurrentThreadId();
            Volatile.Write(ref _ownerThreadId, ownerThreadId);
            _nativeApi.EnsureMessageQueue();

            if (!TryRegisterHooks(bridge.Callback, hooks))
            {
                bridge.BeginStop();
                return;
            }

            if (!bridge.IsAcceptingChanges)
            {
                return;
            }

            startupCompleted = true;
            startup.TrySetResult(true);
            RunMessageLoop(bridge);
        }
        catch
        {
            if (startupCompleted)
            {
                bridge.ReportFault(
                    WindowsCaptureWinEventSourceFault.MessageLoopFailed);
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
                if (startupCompleted)
                {
                    bridge.ReportFault(
                        WindowsCaptureWinEventSourceFault.HookUnregistrationFailed);
                }
                else
                {
                    Volatile.Write(ref _startupCleanupFault, 1);
                }
            }
            else
            {
                DrainMessageQueue();
            }

            bridge.CompleteCleanup(retainRoot: !cleanUnhook);
            Volatile.Write(ref _ownerThreadId, 0);
            if (!startupCompleted)
            {
                startup.TrySetResult(false);
            }

            GC.KeepAlive(bridge);
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
    private Action<WindowsCaptureWinEventChange>? _changeCallback;
    private Action<WindowsCaptureWinEventSourceFault>? _faultCallback;
    private GCHandle _selfRoot;
    private int _acceptChanges = 1;
    private int _cleanupCompleted;
    private int _retainedRoot;

    internal WindowsCaptureWinEventCallbackBridge(
        Action<WindowsCaptureWinEventChange> changeCallback,
        Action<WindowsCaptureWinEventSourceFault> faultCallback,
        Func<bool> requestOwnerStop)
    {
        _changeCallback = changeCallback
            ?? throw new ArgumentNullException(nameof(changeCallback));
        _faultCallback = faultCallback
            ?? throw new ArgumentNullException(nameof(faultCallback));
        _requestOwnerStop = requestOwnerStop
            ?? throw new ArgumentNullException(nameof(requestOwnerStop));
        Callback = OnWinEvent;
    }

    internal WindowsCaptureWinEventProc Callback { get; }

    internal bool IsAcceptingChanges =>
        Volatile.Read(ref _acceptChanges) != 0;

    internal bool HasRetainedRoot => Volatile.Read(ref _retainedRoot) != 0;

    internal void Root()
    {
        _selfRoot = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    internal void BeginStop()
    {
        Interlocked.Exchange(ref _acceptChanges, 0);
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
            if (Volatile.Read(ref _acceptChanges) == 0
                || !TryMapChange(
                    eventType,
                    windowHandle,
                    objectId,
                    childId,
                    out var change))
            {
                return;
            }

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

    private static bool TryMapChange(
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

internal interface IWindowsCaptureWinEventNativeApi
{
    bool IsSupportedPlatform { get; }

    uint GetCurrentThreadId();

    void EnsureMessageQueue();

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
    private const uint PeekMessageNoRemove = 0x0000;
    private const uint PeekMessageRemove = 0x0001;

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
    {
        WindowHandle = 0;
        Message = message;
        WParam = 0;
        LParam = 0;
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
