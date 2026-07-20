using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class WindowsCaptureWinEventSourceTests
{
    [Fact]
    public void ConstantsDelegateAndMessageLayoutMatchTheWindowsContract()
    {
        Assert.Equal<uint>(0x0003, WindowsCaptureWinEventSource.EventSystemForeground);
        Assert.Equal<uint>(0x0020, WindowsCaptureWinEventSource.EventSystemDesktopSwitch);
        Assert.Equal<uint>(0x8000, WindowsCaptureWinEventSource.EventObjectCreate);
        Assert.Equal<uint>(0x8001, WindowsCaptureWinEventSource.EventObjectDestroy);
        Assert.Equal<uint>(
            0x800B,
            WindowsCaptureWinEventSource.EventObjectLocationChange);
        Assert.Equal<uint>(0x800C, WindowsCaptureWinEventSource.EventObjectNameChange);
        Assert.Equal(0, WindowsCaptureWinEventSource.ObjectIdWindow);
        Assert.Equal(0, WindowsCaptureWinEventSource.ChildIdSelf);
        Assert.Equal<uint>(0, WindowsCaptureWinEventSource.WinEventOutOfContext);
        Assert.Equal<uint>(0x007E, WindowsCaptureWinEventSource.WindowMessageDisplayChange);
        Assert.Equal<uint>(0x0218, WindowsCaptureWinEventSource.WindowMessagePowerBroadcast);
        Assert.Equal<uint>(0x02B1, WindowsCaptureWinEventSource.WindowMessageWtsSessionChange);
        Assert.Equal<uint>(0, PInvokeWindowsCaptureWinEventNativeApi.WtsNotifyForThisSession);
        Assert.Equal<uint>(0, PInvokeWindowsCaptureWinEventNativeApi.PowerDeviceNotifyWindowHandle);
        Assert.Equal<uint>(0x80000000, PInvokeWindowsCaptureWinEventNativeApi.HiddenWindowStyle);
        Assert.Equal<uint>(
            0x08000080,
            PInvokeWindowsCaptureWinEventNativeApi.HiddenWindowExtendedStyle);
        Assert.Equal<nint>(0, PInvokeWindowsCaptureWinEventNativeApi.HiddenWindowParent);

        var callbackConvention = typeof(WindowsCaptureWinEventProc)
            .GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        Assert.NotNull(callbackConvention);
        Assert.Equal(CallingConvention.Winapi, callbackConvention.CallingConvention);
        var windowProcedureConvention = typeof(WindowsCaptureWindowProc)
            .GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        Assert.NotNull(windowProcedureConvention);
        Assert.Equal(
            CallingConvention.Winapi,
            windowProcedureConvention.CallingConvention);
        Assert.Equal(
            "user32.dll",
            GetDllImportLibrary(nameof(
                WindowsCaptureWinEventMethods.RegisterSuspendResumeNotification)));
        Assert.Equal(
            "user32.dll",
            GetDllImportLibrary(nameof(
                WindowsCaptureWinEventMethods.UnregisterSuspendResumeNotification)));

        if (IntPtr.Size == 8)
        {
            Assert.Equal(80, Marshal.SizeOf<WindowsCaptureWindowClass>());
            Assert.Equal(8, Marshal.OffsetOf<WindowsCaptureWindowClass>(
                nameof(WindowsCaptureWindowClass.WindowProcedure)).ToInt32());
            Assert.Equal(24, Marshal.OffsetOf<WindowsCaptureWindowClass>(
                nameof(WindowsCaptureWindowClass.Instance)).ToInt32());
            Assert.Equal(64, Marshal.OffsetOf<WindowsCaptureWindowClass>(
                nameof(WindowsCaptureWindowClass.ClassName)).ToInt32());
            Assert.Equal(48, Marshal.SizeOf<WindowsCaptureThreadMessage>());
            Assert.Equal(8, OffsetOf(nameof(WindowsCaptureThreadMessage.Message)));
            Assert.Equal(16, OffsetOf(nameof(WindowsCaptureThreadMessage.WParam)));
            Assert.Equal(24, OffsetOf(nameof(WindowsCaptureThreadMessage.LParam)));
            Assert.Equal(32, OffsetOf(nameof(WindowsCaptureThreadMessage.Time)));
            Assert.Equal(36, OffsetOf(nameof(WindowsCaptureThreadMessage.Point)));
            Assert.Equal(44, OffsetOf(nameof(WindowsCaptureThreadMessage.Private)));
        }
        else
        {
            Assert.Equal(48, Marshal.SizeOf<WindowsCaptureWindowClass>());
            Assert.Equal(32, Marshal.SizeOf<WindowsCaptureThreadMessage>());
            Assert.Equal(4, OffsetOf(nameof(WindowsCaptureThreadMessage.Message)));
            Assert.Equal(8, OffsetOf(nameof(WindowsCaptureThreadMessage.WParam)));
            Assert.Equal(12, OffsetOf(nameof(WindowsCaptureThreadMessage.LParam)));
            Assert.Equal(16, OffsetOf(nameof(WindowsCaptureThreadMessage.Time)));
            Assert.Equal(20, OffsetOf(nameof(WindowsCaptureThreadMessage.Point)));
            Assert.Equal(28, OffsetOf(nameof(WindowsCaptureThreadMessage.Private)));
        }
    }

    [Fact]
    public void StartRegistersHooksAndSystemNotificationsOnTheOwnerThread()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);

        source.Start(_ => { }, _ => { });

        Assert.True(api.MessageQueueEnsured);
        Assert.Equal(
            [
                "register-window-class",
                "create-hidden-window",
                "register-session",
                "register-power",
                "register-hook:1",
                "register-hook:2",
                "register-hook:3",
                "register-hook:4",
            ],
            api.RegistrationOperations);
        Assert.NotNull(api.WindowProcedure);
        Assert.Equal(4, api.WindowRegistrationThreadIds.Count);
        Assert.All(
            api.WindowRegistrationThreadIds,
            threadId => Assert.Equal(api.HookOwnerThreadId, threadId));
        Assert.Collection(
            api.Registrations,
            registration => AssertRegistration(
                registration,
                WindowsCaptureWinEventSource.EventSystemForeground,
                WindowsCaptureWinEventSource.EventSystemForeground),
            registration => AssertRegistration(
                registration,
                WindowsCaptureWinEventSource.EventSystemDesktopSwitch,
                WindowsCaptureWinEventSource.EventSystemDesktopSwitch),
            registration => AssertRegistration(
                registration,
                WindowsCaptureWinEventSource.EventObjectCreate,
                WindowsCaptureWinEventSource.EventObjectDestroy),
            registration => AssertRegistration(
                registration,
                WindowsCaptureWinEventSource.EventObjectLocationChange,
                WindowsCaptureWinEventSource.EventObjectNameChange));

        source.Dispose();

        Assert.Equal(new nint[] { 4, 3, 2, 1 }, api.UnhookedHandles);
        Assert.All(
            api.UnhookThreadIds,
            threadId => Assert.Equal(api.HookOwnerThreadId, threadId));
        Assert.Equal(
            [
                "unhook:4",
                "unhook:3",
                "unhook:2",
                "unhook:1",
                "unregister-power",
                "unregister-session",
                "destroy-window",
                "unregister-window-class",
            ],
            api.CleanupOperations);
        Assert.All(
            api.CleanupThreadIds,
            threadId => Assert.Equal(api.HookOwnerThreadId, threadId));
    }

    [Fact]
    public void WindowMessagesMapToValueFreeSystemChangeKinds()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, _ => { });

        api.RaiseWindowMessageOnOwnerThread(0x007E, 0, 0);
        api.RaiseWindowMessageOnOwnerThread(0x02B1, 0x0007, 0);
        api.RaiseWindowMessageOnOwnerThread(0x02B1, 0x0008, 0);
        api.RaiseWindowMessageOnOwnerThread(0x02B1, 0x0009, 0);
        api.RaiseWindowMessageOnOwnerThread(0x0218, 0x0004, 0);
        api.RaiseWindowMessageOnOwnerThread(0x0218, 0x0012, 0);

        Assert.Equal(
            [
                WindowsCaptureWinEventChange.DisplayTopologyChanged,
                WindowsCaptureWinEventChange.SessionUnavailable,
                WindowsCaptureWinEventChange.SessionAvailable,
                WindowsCaptureWinEventChange.SessionChanged,
                WindowsCaptureWinEventChange.PowerSuspending,
                WindowsCaptureWinEventChange.PowerResumed,
            ],
            changes);
    }

    [Fact]
    public void UnknownWindowMessageUsesDefWindowProcWithoutPublishingAChange()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, _ => { });

        api.RaiseWindowMessageOnOwnerThread(0x05FF, 123, 456);

        Assert.Empty(changes);
        var call = Assert.Single(api.DefWindowProcCalls);
        Assert.Equal(FakeWindowsCaptureWinEventNativeApi.HiddenWindowHandle, call.WindowHandle);
        Assert.Equal<uint>(0x05FF, call.Message);
        Assert.Equal<nuint>(123, call.WParam);
        Assert.Equal<nint>(456, call.LParam);
    }

    [Theory]
    [InlineData(SystemRegistrationFailure.WindowClass)]
    [InlineData(SystemRegistrationFailure.WindowCreation)]
    [InlineData(SystemRegistrationFailure.Session)]
    [InlineData(SystemRegistrationFailure.Power)]
    public void PartialSystemRegistrationFailureRollsBackInReverseOrder(
        SystemRegistrationFailure failureStage)
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            FailWindowClassRegistration =
                failureStage == SystemRegistrationFailure.WindowClass,
            FailWindowCreation =
                failureStage == SystemRegistrationFailure.WindowCreation,
            FailSessionRegistration =
                failureStage == SystemRegistrationFailure.Session,
            FailPowerRegistration =
                failureStage == SystemRegistrationFailure.Power,
        };
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new List<WindowsCaptureWinEventSourceFault>();

        Assert.Throws<InvalidOperationException>(() =>
            source.Start(_ => { }, faults.Add));

        Assert.Contains(ExpectedFault(failureStage), faults);
        Assert.Equal(ExpectedCleanup(failureStage), api.CleanupOperations);
        Assert.Empty(api.Registrations);
        Assert.All(
            api.CleanupThreadIds,
            threadId => Assert.Equal(api.HookOwnerThreadId, threadId));
    }

    [Fact]
    public void RelevantCallbacksMapToValueFreeChangeKinds()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, _ => { });

        api.Raise(WindowsCaptureWinEventSource.EventSystemForeground, 0, -4, 9);
        api.Raise(WindowsCaptureWinEventSource.EventSystemDesktopSwitch, 0, -4, 9);
        api.Raise(WindowsCaptureWinEventSource.EventObjectCreate, 100, 0, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectDestroy, 100, 0, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectLocationChange, 100, 0, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectNameChange, 100, 0, 0);

        Assert.Equal(
            [
                WindowsCaptureWinEventChange.Foreground,
                WindowsCaptureWinEventChange.DesktopSwitch,
                WindowsCaptureWinEventChange.ObjectCreated,
                WindowsCaptureWinEventChange.ObjectDestroyed,
                WindowsCaptureWinEventChange.ObjectLocationChanged,
                WindowsCaptureWinEventChange.ObjectNameChanged,
            ],
            changes);
    }

    [Fact]
    public void ObjectCallbacksRequireAWindowObjectAndSelfChild()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, _ => { });

        api.Raise(WindowsCaptureWinEventSource.EventObjectCreate, 0, 0, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectCreate, 100, -4, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectCreate, 100, 0, 1);
        api.Raise(WindowsCaptureWinEventSource.EventObjectDestroy, 0, 0, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectLocationChange, 0, 0, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectLocationChange, 100, -4, 0);
        api.Raise(WindowsCaptureWinEventSource.EventObjectLocationChange, 100, 0, 1);
        api.Raise(WindowsCaptureWinEventSource.EventObjectNameChange, 100, -4, 0);

        Assert.Empty(changes);
    }

    [Fact]
    public void CallbackFailureDoesNotEscapeAndClosesFurtherDelivery()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var callbackCount = 0;
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();
        using var faultReported = new ManualResetEventSlim();
        source.Start(
            _ =>
            {
                Interlocked.Increment(ref callbackCount);
                throw new InvalidOperationException("sensitive callback detail");
            },
            fault =>
            {
                faults.Enqueue(fault);
                faultReported.Set();
            });

        api.RaiseOnOwnerThread(
            WindowsCaptureWinEventSource.EventSystemForeground,
            100,
            0,
            0);
        Assert.True(faultReported.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(api.AllHooksUnhooked.Wait(TimeSpan.FromSeconds(2)));
        api.Raise(
            WindowsCaptureWinEventSource.EventSystemForeground,
            200,
            0,
            0);

        Assert.Equal(1, callbackCount);
        Assert.Equal(
            WindowsCaptureWinEventSourceFault.CallbackFailed,
            Assert.Single(faults));
        Assert.False(source.HasRetainedCallbackBridge);
    }

    [Fact]
    public void CallbackCanDisposeSourceOnTheOwnerThread()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var callbackThreadId = 0;
        source.Start(
            _ =>
            {
                callbackThreadId = Environment.CurrentManagedThreadId;
                source.Dispose();
            },
            _ => { });

        api.RaiseOnOwnerThread(
            WindowsCaptureWinEventSource.EventSystemForeground,
            100,
            0,
            0);

        Assert.True(api.AllHooksUnhooked.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(api.HookOwnerThreadId, checked((uint)callbackThreadId));
        Assert.Equal(4, api.UnhookedHandles.Count);
        Assert.False(source.HasRetainedCallbackBridge);
    }

    [Fact]
    public void CallbackBridgeSurvivesAFullGarbageCollectionWhileHooked()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, _ => { });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        api.Raise(
            WindowsCaptureWinEventSource.EventSystemForeground,
            100,
            0,
            0);

        Assert.Equal(
            WindowsCaptureWinEventChange.Foreground,
            Assert.Single(changes));
    }

    [Fact]
    public void PartialRegistrationFailureUnhooksCompletedHooksInReverseOrder()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            FailHookRegistrationNumber = 3,
        };
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            source.Start(_ => { }, faults.Enqueue));

        Assert.Equal(
            "The Windows capture event source could not register every required system notification.",
            failure.Message);
        Assert.Contains(
            WindowsCaptureWinEventSourceFault.HookRegistrationFailed,
            faults);
        Assert.Equal(new nint[] { 2, 1 }, api.UnhookedHandles);
        Assert.All(
            api.UnhookThreadIds,
            threadId => Assert.Equal(api.HookOwnerThreadId, threadId));
        Assert.Equal(
            [
                "unhook:2",
                "unhook:1",
                "unregister-power",
                "unregister-session",
                "destroy-window",
                "unregister-window-class",
            ],
            api.CleanupOperations);
        Assert.False(source.HasRetainedCallbackBridge);
    }

    [Fact]
    public void PartialRegistrationAndUnhookFailureReportBothFaultsAndRetainBridge()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            FailHookRegistrationNumber = 3,
        };
        api.FailedUnhookHandles.Add(2);
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();

        Assert.Throws<InvalidOperationException>(() =>
            source.Start(_ => { }, faults.Enqueue));

        Assert.Contains(
            WindowsCaptureWinEventSourceFault.HookRegistrationFailed,
            faults);
        Assert.Contains(
            WindowsCaptureWinEventSourceFault.HookUnregistrationFailed,
            faults);
        Assert.True(source.HasRetainedCallbackBridge);
        Assert.Equal(new nint[] { 2, 1 }, api.UnhookedHandles);
    }

    [Fact]
    public void RegistrationFaultCallbackCanDisposeSourceWithoutStartupDeadlock()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            FailHookRegistrationNumber = 1,
        };
        using var source = new WindowsCaptureWinEventSource(
            api,
            threadTransitionTimeoutMilliseconds: 50);
        var callbackInvoked = false;
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<InvalidOperationException>(() => source.Start(
            _ => { },
            _ =>
            {
                callbackInvoked = true;
                source.Dispose();
            }));
        stopwatch.Stop();

        Assert.True(callbackInvoked);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(source.HasRetainedCallbackBridge);
    }

    [Fact]
    public void DisposeRejectsCallbacksAlreadyQueuedAtUnhookTime()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            RaiseLateCallbackDuringUnhook = true,
        };
        using var source = new WindowsCaptureWinEventSource(api);
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, _ => { });

        source.Dispose();

        Assert.Empty(changes);
        Assert.Equal(4, api.LateCallbackAttempts);
        Assert.False(source.HasRetainedCallbackBridge);
    }

    [Fact]
    public void UnhookFailureReportsValueFreeFaultAndRetainsDetachedBridge()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();
        var changes = new List<WindowsCaptureWinEventChange>();
        source.Start(changes.Add, faults.Enqueue);
        api.FailedUnhookHandles.Add(3);

        source.Dispose();
        api.Registrations[2].Callback(
            api.Registrations[2].Handle,
            WindowsCaptureWinEventSource.EventObjectCreate,
            100,
            0,
            0,
            0,
            0);

        Assert.Contains(
            WindowsCaptureWinEventSourceFault.HookUnregistrationFailed,
            faults);
        Assert.True(source.HasRetainedCallbackBridge);
        Assert.Empty(changes);
        Assert.Equal(4, api.UnhookedHandles.Count);
    }

    [Fact]
    public void NotificationCleanupFailuresAreReportedAfterTheWindowCallbackIsDetached()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            FailPowerUnregistration = true,
            FailSessionUnregistration = true,
        };
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();
        source.Start(_ => { }, faults.Enqueue);

        source.Dispose();

        Assert.Contains(
            WindowsCaptureWinEventSourceFault.PowerUnregistrationFailed,
            faults);
        Assert.Contains(
            WindowsCaptureWinEventSourceFault.SessionUnregistrationFailed,
            faults);
        Assert.False(source.HasRetainedCallbackBridge);
    }

    [Theory]
    [InlineData(true, false, (int)WindowsCaptureWinEventSourceFault.WindowDestructionFailed)]
    [InlineData(false, true, (int)WindowsCaptureWinEventSourceFault.WindowClassUnregistrationFailed)]
    public void UncertainWindowCallbackCleanupRetainsTheDetachedBridge(
        bool failWindowDestruction,
        bool failWindowClassUnregistration,
        int rawExpectedFault)
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            FailWindowDestruction = failWindowDestruction,
            FailWindowClassUnregistration = failWindowClassUnregistration,
        };
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();
        source.Start(_ => { }, faults.Enqueue);

        source.Dispose();

        Assert.Contains((WindowsCaptureWinEventSourceFault)rawExpectedFault, faults);
        Assert.True(source.HasRetainedCallbackBridge);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        var source = new WindowsCaptureWinEventSource(api);
        source.Start(_ => { }, _ => { });

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, api.PostThreadMessageCount);
        Assert.Equal(4, api.UnhookedHandles.Count);
    }

    [Fact]
    public void SuccessfulStopPostUsesBoundedFallbackWhenPumpDoesNotExit()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            DelayPostedMessages = true,
        };
        var source = new WindowsCaptureWinEventSource(
            api,
            threadTransitionTimeoutMilliseconds: 50);
        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();
        source.Start(_ => { }, faults.Enqueue);

        var stopwatch = Stopwatch.StartNew();
        source.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains(WindowsCaptureWinEventSourceFault.StopTimedOut, faults);
        Assert.True(source.HasRetainedCallbackBridge);

        api.ReleaseDelayedMessages();
        Assert.True(api.AllHooksUnhooked.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(4, api.UnhookedHandles.Count);
        Assert.True(SpinWait.SpinUntil(
            () => !source.HasRetainedCallbackBridge,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void UnsupportedPlatformDoesNotInvokeAnyNativeOperation()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi
        {
            IsSupportedPlatform = false,
        };
        using var source = new WindowsCaptureWinEventSource(api);
        var faults = new List<WindowsCaptureWinEventSourceFault>();

        Assert.Throws<PlatformNotSupportedException>(() =>
            source.Start(_ => { }, faults.Add));

        Assert.Equal(
            WindowsCaptureWinEventSourceFault.UnsupportedPlatform,
            Assert.Single(faults));
        Assert.False(api.MessageQueueEnsured);
        Assert.Empty(api.Registrations);
        Assert.Empty(api.RegistrationOperations);
    }

    [Fact]
    public void RealWindowsSystemEventSourceRegistersAndCleansUpWithoutFaults()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var faults = new ConcurrentQueue<WindowsCaptureWinEventSourceFault>();
        using var source = new WindowsCaptureWinEventSource();

        var failure = Record.Exception(() =>
            source.Start(_ => { }, faults.Enqueue));
        Assert.True(
            failure is null,
            $"System event source startup failed with: {string.Join(", ", faults)}");
        source.Dispose();

        Assert.Empty(faults);
        Assert.False(source.HasRetainedCallbackBridge);
    }

    private static WindowsCaptureWinEventSourceFault ExpectedFault(
        SystemRegistrationFailure failureStage)
    {
        return failureStage switch
        {
            SystemRegistrationFailure.WindowClass =>
                WindowsCaptureWinEventSourceFault.WindowClassRegistrationFailed,
            SystemRegistrationFailure.WindowCreation =>
                WindowsCaptureWinEventSourceFault.WindowCreationFailed,
            SystemRegistrationFailure.Session =>
                WindowsCaptureWinEventSourceFault.SessionRegistrationFailed,
            SystemRegistrationFailure.Power =>
                WindowsCaptureWinEventSourceFault.PowerRegistrationFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(failureStage)),
        };
    }

    private static string[] ExpectedCleanup(
        SystemRegistrationFailure failureStage)
    {
        return failureStage switch
        {
            SystemRegistrationFailure.WindowClass => [],
            SystemRegistrationFailure.WindowCreation =>
                ["unregister-window-class"],
            SystemRegistrationFailure.Session =>
                ["destroy-window", "unregister-window-class"],
            SystemRegistrationFailure.Power =>
                [
                    "unregister-session",
                    "destroy-window",
                    "unregister-window-class",
                ],
            _ => throw new ArgumentOutOfRangeException(nameof(failureStage)),
        };
    }

    private static int OffsetOf(string fieldName)
    {
        return checked((int)Marshal.OffsetOf<WindowsCaptureThreadMessage>(fieldName));
    }

    private static string GetDllImportLibrary(string methodName)
    {
        var method = typeof(WindowsCaptureWinEventMethods).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var import = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(import);
        return import.Value;
    }

    private static void AssertRegistration(
        HookRegistration registration,
        uint eventMinimum,
        uint eventMaximum)
    {
        Assert.Equal(eventMinimum, registration.EventMinimum);
        Assert.Equal(eventMaximum, registration.EventMaximum);
        Assert.Equal<nint>(0, registration.CallbackModule);
        Assert.Equal<uint>(0, registration.ProcessId);
        Assert.Equal<uint>(0, registration.ThreadId);
        Assert.Equal(
            WindowsCaptureWinEventSource.WinEventOutOfContext,
            registration.Flags);
        Assert.NotNull(registration.Callback);
    }

    private sealed record HookRegistration(
        nint Handle,
        uint EventMinimum,
        uint EventMaximum,
        nint CallbackModule,
        WindowsCaptureWinEventProc Callback,
        uint ProcessId,
        uint ThreadId,
        uint Flags,
        uint OwnerThreadId);

    private sealed record WindowMessageCall(
        nint WindowHandle,
        uint Message,
        nuint WParam,
        nint LParam);

    public enum SystemRegistrationFailure
    {
        WindowClass,
        WindowCreation,
        Session,
        Power,
    }

    private sealed class FakeWindowsCaptureWinEventNativeApi
        : IWindowsCaptureWinEventNativeApi
    {
        private const uint DispatchCallbackMessage = 0x0401;

        internal const nint HiddenWindowHandle = 0x1001;

        private const nint SuspendResumeRegistrationHandle = 0x2001;

        private readonly Channel<QueuedMessage> _messages =
            Channel.CreateUnbounded<QueuedMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private readonly ConcurrentQueue<QueuedMessage> _delayedMessages = new();
        private readonly ConcurrentQueue<Action> _dispatchCallbacks = new();
        private int _hookRegistrationCount;

        public bool IsSupportedPlatform { get; set; } = true;

        public int? FailHookRegistrationNumber { get; set; }

        public bool FailWindowClassRegistration { get; set; }

        public bool FailWindowCreation { get; set; }

        public bool FailSessionRegistration { get; set; }

        public bool FailPowerRegistration { get; set; }

        public bool FailPowerUnregistration { get; set; }

        public bool FailSessionUnregistration { get; set; }

        public bool FailWindowDestruction { get; set; }

        public bool FailWindowClassUnregistration { get; set; }

        public bool RaiseLateCallbackDuringUnhook { get; set; }

        public bool DelayPostedMessages { get; set; }

        public HashSet<nint> FailedUnhookHandles { get; } = [];

        public List<HookRegistration> Registrations { get; } = [];

        public List<nint> UnhookedHandles { get; } = [];

        public List<uint> UnhookThreadIds { get; } = [];

        public List<string> RegistrationOperations { get; } = [];

        public List<string> CleanupOperations { get; } = [];

        public List<uint> WindowRegistrationThreadIds { get; } = [];

        public List<uint> CleanupThreadIds { get; } = [];

        public List<WindowMessageCall> DefWindowProcCalls { get; } = [];

        public WindowsCaptureWindowProc? WindowProcedure { get; private set; }

        public uint HookOwnerThreadId { get; private set; }

        public bool MessageQueueEnsured { get; private set; }

        public int PostThreadMessageCount { get; private set; }

        public int LateCallbackAttempts { get; private set; }

        public ManualResetEventSlim AllHooksUnhooked { get; } = new();

        public uint GetCurrentThreadId()
        {
            HookOwnerThreadId = CurrentThreadId;
            return HookOwnerThreadId;
        }

        public void EnsureMessageQueue()
        {
            MessageQueueEnsured = true;
        }

        public bool RegisterWindowClass(WindowsCaptureWindowProc windowProcedure)
        {
            RegistrationOperations.Add("register-window-class");
            WindowRegistrationThreadIds.Add(CurrentThreadId);
            WindowProcedure = windowProcedure;
            return !FailWindowClassRegistration;
        }

        public nint CreateHiddenWindow()
        {
            RegistrationOperations.Add("create-hidden-window");
            WindowRegistrationThreadIds.Add(CurrentThreadId);
            return FailWindowCreation ? 0 : HiddenWindowHandle;
        }

        public bool DestroyWindow(nint windowHandle)
        {
            Assert.Equal(HiddenWindowHandle, windowHandle);
            CleanupOperations.Add("destroy-window");
            CleanupThreadIds.Add(CurrentThreadId);
            return !FailWindowDestruction;
        }

        public bool UnregisterWindowClass()
        {
            CleanupOperations.Add("unregister-window-class");
            CleanupThreadIds.Add(CurrentThreadId);
            if (!FailWindowClassUnregistration)
            {
                WindowProcedure = null;
            }

            return !FailWindowClassUnregistration;
        }

        public bool RegisterSessionNotifications(nint windowHandle)
        {
            Assert.Equal(HiddenWindowHandle, windowHandle);
            RegistrationOperations.Add("register-session");
            WindowRegistrationThreadIds.Add(CurrentThreadId);
            return !FailSessionRegistration;
        }

        public bool UnregisterSessionNotifications(nint windowHandle)
        {
            Assert.Equal(HiddenWindowHandle, windowHandle);
            CleanupOperations.Add("unregister-session");
            CleanupThreadIds.Add(CurrentThreadId);
            return !FailSessionUnregistration;
        }

        public nint RegisterSuspendResumeNotifications(nint windowHandle)
        {
            Assert.Equal(HiddenWindowHandle, windowHandle);
            RegistrationOperations.Add("register-power");
            WindowRegistrationThreadIds.Add(CurrentThreadId);
            return FailPowerRegistration ? 0 : SuspendResumeRegistrationHandle;
        }

        public bool UnregisterSuspendResumeNotifications(nint registrationHandle)
        {
            Assert.Equal(SuspendResumeRegistrationHandle, registrationHandle);
            CleanupOperations.Add("unregister-power");
            CleanupThreadIds.Add(CurrentThreadId);
            return !FailPowerUnregistration;
        }

        public nint DefWindowProc(
            nint windowHandle,
            uint message,
            nuint wParam,
            nint lParam)
        {
            DefWindowProcCalls.Add(new WindowMessageCall(
                windowHandle,
                message,
                wParam,
                lParam));
            return 0;
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
            var registrationNumber = Interlocked.Increment(
                ref _hookRegistrationCount);
            if (registrationNumber == FailHookRegistrationNumber)
            {
                return 0;
            }

            var handle = (nint)registrationNumber;
            RegistrationOperations.Add($"register-hook:{registrationNumber}");
            Registrations.Add(new HookRegistration(
                handle,
                eventMinimum,
                eventMaximum,
                callbackModule,
                callback,
                processId,
                threadId,
                flags,
                CurrentThreadId));
            return handle;
        }

        public bool UnhookWinEvent(nint hook)
        {
            UnhookedHandles.Add(hook);
            UnhookThreadIds.Add(CurrentThreadId);
            CleanupOperations.Add($"unhook:{hook}");
            CleanupThreadIds.Add(CurrentThreadId);
            if (UnhookedHandles.Count == Registrations.Count)
            {
                AllHooksUnhooked.Set();
            }

            if (RaiseLateCallbackDuringUnhook)
            {
                LateCallbackAttempts++;
                var registration = Registrations.Single(item => item.Handle == hook);
                registration.Callback(
                    hook,
                    WindowsCaptureWinEventSource.EventSystemForeground,
                    100,
                    0,
                    0,
                    0,
                    0);
            }

            return !FailedUnhookHandles.Contains(hook);
        }

        public int GetMessage(out WindowsCaptureThreadMessage message)
        {
            var queued = _messages.Reader
                .ReadAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            message = queued.Message;
            return queued.Result;
        }

        public bool TryRemoveMessage(out WindowsCaptureThreadMessage message)
        {
            if (_messages.Reader.TryRead(out var queued))
            {
                message = queued.Message;
                return true;
            }

            message = default;
            return false;
        }

        public void TranslateAndDispatchMessage(
            in WindowsCaptureThreadMessage message)
        {
            if (message.Message == DispatchCallbackMessage
                && _dispatchCallbacks.TryDequeue(out var callback))
            {
                callback();
            }
        }

        public bool PostThreadMessage(
            uint threadId,
            uint message,
            nuint wParam,
            nint lParam)
        {
            _ = threadId;
            _ = wParam;
            _ = lParam;
            PostThreadMessageCount++;
            var queued = new QueuedMessage(
                Result: 1,
                new WindowsCaptureThreadMessage(message));
            if (DelayPostedMessages)
            {
                _delayedMessages.Enqueue(queued);
            }
            else
            {
                Assert.True(_messages.Writer.TryWrite(queued));
            }

            return true;
        }

        public void ReleaseDelayedMessages()
        {
            while (_delayedMessages.TryDequeue(out var message))
            {
                Assert.True(_messages.Writer.TryWrite(message));
            }
        }

        public void Raise(
            uint eventType,
            nint windowHandle,
            int objectId,
            int childId)
        {
            var registration = Registrations.Single(item =>
                eventType >= item.EventMinimum
                && eventType <= item.EventMaximum);
            registration.Callback(
                registration.Handle,
                eventType,
                windowHandle,
                objectId,
                childId,
                eventThreadId: 123,
                eventTimeMilliseconds: 456);
        }

        public void RaiseOnOwnerThread(
            uint eventType,
            nint windowHandle,
            int objectId,
            int childId)
        {
            _dispatchCallbacks.Enqueue(
                () => Raise(eventType, windowHandle, objectId, childId));
            Assert.True(_messages.Writer.TryWrite(new QueuedMessage(
                Result: 1,
                new WindowsCaptureThreadMessage(DispatchCallbackMessage))));
        }

        public void RaiseWindowMessageOnOwnerThread(
            uint message,
            nuint wParam,
            nint lParam)
        {
            using var dispatched = new ManualResetEventSlim();
            _dispatchCallbacks.Enqueue(() =>
            {
                try
                {
                    _ = WindowProcedure?.Invoke(
                        HiddenWindowHandle,
                        message,
                        wParam,
                        lParam);
                }
                finally
                {
                    dispatched.Set();
                }
            });
            Assert.True(_messages.Writer.TryWrite(new QueuedMessage(
                Result: 1,
                new WindowsCaptureThreadMessage(DispatchCallbackMessage))));
            Assert.True(dispatched.Wait(TimeSpan.FromSeconds(2)));
        }

        private static uint CurrentThreadId =>
            checked((uint)Environment.CurrentManagedThreadId);

        private readonly record struct QueuedMessage(
            int Result,
            WindowsCaptureThreadMessage Message);
    }
}
