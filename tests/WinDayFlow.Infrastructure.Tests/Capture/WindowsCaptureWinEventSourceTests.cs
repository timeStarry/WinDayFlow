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
        Assert.Equal<uint>(0x800C, WindowsCaptureWinEventSource.EventObjectNameChange);
        Assert.Equal(0, WindowsCaptureWinEventSource.ObjectIdWindow);
        Assert.Equal(0, WindowsCaptureWinEventSource.ChildIdSelf);
        Assert.Equal<uint>(0, WindowsCaptureWinEventSource.WinEventOutOfContext);

        var callbackConvention = typeof(WindowsCaptureWinEventProc)
            .GetCustomAttribute<UnmanagedFunctionPointerAttribute>();
        Assert.NotNull(callbackConvention);
        Assert.Equal(CallingConvention.Winapi, callbackConvention.CallingConvention);

        if (IntPtr.Size == 8)
        {
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
    public void StartRegistersFourNarrowOutOfContextHooksOnTheOwnerThread()
    {
        var api = new FakeWindowsCaptureWinEventNativeApi();
        using var source = new WindowsCaptureWinEventSource(api);

        source.Start(_ => { }, _ => { });

        Assert.True(api.MessageQueueEnsured);
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
                WindowsCaptureWinEventSource.EventObjectNameChange,
                WindowsCaptureWinEventSource.EventObjectNameChange));

        source.Dispose();

        Assert.Equal(new nint[] { 4, 3, 2, 1 }, api.UnhookedHandles);
        Assert.All(
            api.UnhookThreadIds,
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
        api.Raise(WindowsCaptureWinEventSource.EventObjectNameChange, 100, 0, 0);

        Assert.Equal(
            [
                WindowsCaptureWinEventChange.Foreground,
                WindowsCaptureWinEventChange.DesktopSwitch,
                WindowsCaptureWinEventChange.ObjectCreated,
                WindowsCaptureWinEventChange.ObjectDestroyed,
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
            "The Windows capture WinEvent source could not register its hooks.",
            failure.Message);
        Assert.Contains(
            WindowsCaptureWinEventSourceFault.HookRegistrationFailed,
            faults);
        Assert.Equal(new nint[] { 2, 1 }, api.UnhookedHandles);
        Assert.All(
            api.UnhookThreadIds,
            threadId => Assert.Equal(api.HookOwnerThreadId, threadId));
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
    }

    private static int OffsetOf(string fieldName)
    {
        return checked((int)Marshal.OffsetOf<WindowsCaptureThreadMessage>(fieldName));
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

    private sealed class FakeWindowsCaptureWinEventNativeApi
        : IWindowsCaptureWinEventNativeApi
    {
        private const uint DispatchCallbackMessage = 0x0401;

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

        public bool RaiseLateCallbackDuringUnhook { get; set; }

        public bool DelayPostedMessages { get; set; }

        public HashSet<nint> FailedUnhookHandles { get; } = [];

        public List<HookRegistration> Registrations { get; } = [];

        public List<nint> UnhookedHandles { get; } = [];

        public List<uint> UnhookThreadIds { get; } = [];

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

        private static uint CurrentThreadId =>
            checked((uint)Environment.CurrentManagedThreadId);

        private readonly record struct QueuedMessage(
            int Result,
            WindowsCaptureThreadMessage Message);
    }
}
