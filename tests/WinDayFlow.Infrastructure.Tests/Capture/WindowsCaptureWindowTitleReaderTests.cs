using System.Collections.Concurrent;
using System.Diagnostics;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class WindowsCaptureWindowTitleReaderTests
{
    private const ulong WindowHandle = 0x1234;

    [Fact]
    public void SuccessfulReadUsesTheDedicatedWorkerAndClearsItsPrivateBuffer()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(13),
            "Visible title",
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline);

        var callerThreadId = Environment.CurrentManagedThreadId;
        var results = Enumerable.Range(0, 64)
            .Select(_ => Read(reader))
            .ToArray();

        Assert.All(results, result =>
        {
            Assert.Equal(WindowsCaptureObservationReadState.Present, result.State);
            Assert.Equal("Visible title", result.Value);
        });
        Assert.Equal(64, nativeApi.CallCount);
        Assert.NotEqual(callerThreadId, nativeApi.WorkerThreadId);
        Assert.True(nativeApi.BufferWasClearOnEntry);
        Assert.True(nativeApi.IsBufferClear);
        Assert.Equal(64, deadline.CreatedDeadlineCount);
        Assert.Single(nativeApi.WorkerThreadIds);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Idle, reader.State);
    }

    [Fact]
    public async Task StopwatchDeadlineBoundsABlockedNativeRead()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title");
        using var reader = new FailStopWindowsCaptureWindowTitleReader(nativeApi);

        var stopwatch = Stopwatch.StartNew();
        var readTask = Task.Run(() => Read(reader));
        Assert.True(nativeApi.Entered.Wait(TimeSpan.FromSeconds(2)));
        var result = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, result.State);
        Assert.Empty(result.Value);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Poisoned, reader.State);

        nativeApi.Release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.True(nativeApi.IsBufferClear);
    }

    [Fact]
    public void RecoverableNativeFailuresRemainUnknownAndReuseTheWorker()
    {
        var nativeApi = new ThrowingWindowTextBufferApi(
            new InvalidOperationException("recoverable-native-test"),
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline);

        var first = Read(reader);
        var second = Read(reader);

        Assert.Equal(WindowsCaptureObservationReadState.Unknown, first.State);
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, second.State);
        Assert.Equal(2, nativeApi.CallCount);
        Assert.Single(nativeApi.WorkerThreadIds);
        Assert.True(nativeApi.IsBufferClear);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Idle, reader.State);
    }

    [Fact]
    public void RecoverableValueFailuresRemainUnknownAndReuseTheWorker()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title",
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            valueBuilder: (_, _) => throw new InvalidOperationException(
                "recoverable-builder-test"));

        var first = Read(reader);
        var second = Read(reader);

        Assert.Equal(WindowsCaptureObservationReadState.Unknown, first.State);
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, second.State);
        Assert.Equal(2, nativeApi.CallCount);
        Assert.Single(nativeApi.WorkerThreadIds);
        Assert.True(nativeApi.IsBufferClear);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Idle, reader.State);
    }

    [Fact]
    public async Task BlockedReadTimesOutAndLateSensitiveTextCannotPublish()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(11),
            "late-secret");
        var deadline = new ManualWindowTitleDeadline();
        var builtValueCount = 0;
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            valueBuilder: (buffer, length) =>
            {
                _ = Interlocked.Increment(ref builtValueCount);
                return new string(buffer, 0, length);
            });

        var readTask = Task.Run(() => Read(reader));
        Assert.True(nativeApi.Entered.Wait(TimeSpan.FromSeconds(2)));
        var expiredCompletion = Assert.IsAssignableFrom<Task>(
            reader.CurrentAttemptCompletion);

        deadline.ExpireLatest();
        var result = await readTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WindowsCaptureObservationReadState.Unknown, result.State);
        Assert.Empty(result.Value);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Poisoned, reader.State);
        Assert.False(expiredCompletion.IsCompleted);

        var second = reader.ReadWindowTitle(WindowHandle, out var secondValue);
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, second);
        Assert.Empty(secondValue);
        Assert.Equal(1, nativeApi.CallCount);
        Assert.Equal(2, deadline.CreatedDeadlineCount);
        Assert.Equal(1, deadline.WaitCount);

        nativeApi.Release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.False(expiredCompletion.IsCompleted);
        Assert.True(nativeApi.IsBufferClear);
        Assert.Equal(0, Volatile.Read(ref builtValueCount));
    }

    [Fact]
    public async Task SixtyFourConcurrentReadersCannotCreateAQueueBehindTheInFlightRead()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title");
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline);

        var admitted = Task.Run(() => Read(reader));
        Assert.True(nativeApi.Entered.Wait(TimeSpan.FromSeconds(2)));

        var rejected = await Task.WhenAll(Enumerable.Range(0, 63)
            .Select(_ => Task.Run(() => Read(reader))));

        Assert.All(rejected, result =>
        {
            Assert.Equal(WindowsCaptureObservationReadState.Unknown, result.State);
            Assert.Empty(result.Value);
        });
        Assert.Equal(1, nativeApi.CallCount);
        Assert.Equal(64, deadline.CreatedDeadlineCount);
        Assert.Equal(1, deadline.WaitCount);

        deadline.ExpireLatest();
        var first = await admitted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, first.State);
        nativeApi.Release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.Single(nativeApi.WorkerThreadIds);
    }

    [Fact]
    public async Task QueuedDeadlineCancelsWithoutPoisoningTheWorker()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title",
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var workerEntered = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            workerEntry: () =>
            {
                workerEntered.Set();
                releaseWorker.Wait();
            });

        var queuedRead = Task.Run(() => Read(reader));
        Assert.True(workerEntered.Wait(TimeSpan.FromSeconds(2)));
        var expiredCompletion = Assert.IsAssignableFrom<Task>(
            reader.CurrentAttemptCompletion);

        deadline.ExpireLatest();
        var expired = await queuedRead.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WindowsCaptureObservationReadState.Unknown, expired.State);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Idle, reader.State);
        Assert.Equal(0, nativeApi.CallCount);
        Assert.False(expiredCompletion.IsCompleted);

        releaseWorker.Set();
        var recovered = Read(reader);

        Assert.Equal(WindowsCaptureObservationReadState.Present, recovered.State);
        Assert.Equal("title", recovered.Value);
        Assert.Equal(1, nativeApi.CallCount);
        Assert.Equal(2, deadline.CreatedDeadlineCount);
        Assert.False(expiredCompletion.IsCompleted);
    }

    [Fact]
    public void CompletionCommittedBeforeTheDeadlineCannotBeExpiredRetroactively()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title",
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline);

        var result = Read(reader);
        deadline.ExpireLatest();

        Assert.Equal(WindowsCaptureObservationReadState.Present, result.State);
        Assert.Equal("title", result.Value);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Idle, reader.State);
        Assert.Equal(1, nativeApi.CallCount);
    }

    [Fact]
    public async Task BlockedValueConstructionCannotDelayCallerOrDisposePastDeadline()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title");
        var deadline = new ManualWindowTitleDeadline();
        using var builderEntered = new ManualResetEventSlim();
        using var releaseBuilder = new ManualResetEventSlim();
        var builtValueCount = 0;
        var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            disposeTimeoutMilliseconds: 25,
            valueBuilder: (buffer, length) =>
            {
                _ = Interlocked.Increment(ref builtValueCount);
                builderEntered.Set();
                releaseBuilder.Wait();
                return new string(buffer, 0, length);
            });
        var readTask = Task.Run(() => Read(reader));
        Assert.True(nativeApi.Entered.Wait(TimeSpan.FromSeconds(2)));
        var expiredCompletion = Assert.IsAssignableFrom<Task>(
            reader.CurrentAttemptCompletion);

        nativeApi.Release.Set();
        Assert.True(builderEntered.Wait(TimeSpan.FromSeconds(2)));
        deadline.ExpireLatest();
        var result = await readTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WindowsCaptureObservationReadState.Unknown, result.State);
        Assert.Empty(result.Value);
        Assert.Equal(1, Volatile.Read(ref builtValueCount));
        Assert.False(expiredCompletion.IsCompleted);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Poisoned, reader.State);
        Assert.True(reader.IsWorkerAlive);

        var stopwatch = Stopwatch.StartNew();
        reader.Dispose();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Stopping, reader.State);
        Assert.True(reader.IsWorkerAlive);

        releaseBuilder.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.True(nativeApi.IsBufferClear);
        reader.Dispose();
    }

#pragma warning disable CA2201 // Deliberately inject runtime-reserved exceptions to verify the catch filter.
    [Fact]
    public void FatalNativeReadIsRethrownAndRemainsSticky()
    {
        var fatal = new OutOfMemoryException("fatal-native-test");
        var nativeApi = new ThrowingWindowTextBufferApi(
            fatal,
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline);

        var first = Assert.Throws<OutOfMemoryException>(() => Read(reader));

        Assert.Same(fatal, first);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Poisoned, reader.State);
        Assert.True(nativeApi.IsBufferClear);

        var second = Assert.Throws<OutOfMemoryException>(() => Read(reader));

        Assert.Same(fatal, second);
        Assert.Equal(2, deadline.CreatedDeadlineCount);
    }

    [Fact]
    public void FatalValueConstructionIsRethrownAndCannotPublish()
    {
        var fatal = new AccessViolationException("fatal-builder-test");
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title",
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            valueBuilder: (_, _) => throw fatal);

        var exception = Assert.Throws<AccessViolationException>(() => Read(reader));

        Assert.Same(fatal, exception);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Poisoned, reader.State);
        Assert.True(nativeApi.IsBufferClear);
        Assert.Equal(1, nativeApi.CallCount);
    }

    [Fact]
    public void ValueBuilderDisposalCannotOverwriteStoppingOrPublish()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title",
            initiallyReleased: true);
        var deadline = new ManualWindowTitleDeadline();
        FailStopWindowsCaptureWindowTitleReader? reader = null;
        reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            valueBuilder: (buffer, length) =>
            {
                reader!.Dispose();
                return new string(buffer, 0, length);
            });

        var result = Read(reader);

        Assert.Equal(WindowsCaptureObservationReadState.Unknown, result.State);
        Assert.Empty(result.Value);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Stopping, reader.State);
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.True(nativeApi.IsBufferClear);
        reader.Dispose();
    }

    [Fact]
    public async Task FatalNativeFailureAfterTimeoutIsRethrownByTheNextRead()
    {
        var fatal = new OutOfMemoryException("late-fatal-native-test");
        var nativeApi = new ThrowingWindowTextBufferApi(fatal);
        var deadline = new ManualWindowTitleDeadline();
        using var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline);

        var readTask = Task.Run(() => Read(reader));
        Assert.True(nativeApi.Entered.Wait(TimeSpan.FromSeconds(2)));
        deadline.ExpireLatest();

        var expired = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, expired.State);

        nativeApi.Release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.True(nativeApi.IsBufferClear);

        var exception = Assert.Throws<OutOfMemoryException>(() => Read(reader));
        Assert.Same(fatal, exception);
    }
#pragma warning restore CA2201

    [Fact]
    public async Task DisposeIsBoundedWhileTheNativeReadRemainsBlocked()
    {
        var nativeApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(5),
            "title");
        var deadline = new ManualWindowTitleDeadline();
        var reader = new FailStopWindowsCaptureWindowTitleReader(
            nativeApi,
            deadline,
            disposeTimeoutMilliseconds: 25);
        var readTask = Task.Run(() => Read(reader));
        Assert.True(nativeApi.Entered.Wait(TimeSpan.FromSeconds(2)));
        var expiredCompletion = Assert.IsAssignableFrom<Task>(
            reader.CurrentAttemptCompletion);

        var stopwatch = Stopwatch.StartNew();
        reader.Dispose();
        stopwatch.Stop();
        var result = await readTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(WindowsCaptureObservationReadState.Unknown, result.State);
        Assert.Equal(WindowsCaptureWindowTitleWorkerState.Stopping, reader.State);
        Assert.True(reader.IsWorkerAlive);
        Assert.False(expiredCompletion.IsCompleted);

        nativeApi.Release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !reader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.True(nativeApi.IsBufferClear);
        reader.Dispose();
    }

    [Fact]
    public async Task VerifierReleasesItsProcessAfterOneTitleDeadline()
    {
        var windowTextApi = new ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult.Present(11),
            "late-secret");
        var deadline = new ManualWindowTitleDeadline();
        using var titleReader = new FailStopWindowsCaptureWindowTitleReader(
            windowTextApi,
            deadline);
        var targetApi = new StableTargetNativeApi(titleReader);
        var verifier = new WindowsCaptureTargetVerifier(targetApi);

        var verificationTask = Task.Run(verifier.Verify);
        Assert.True(windowTextApi.Entered.Wait(TimeSpan.FromSeconds(2)));
        deadline.ExpireLatest();
        var result = await verificationTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(NativeCaptureTargetIdentityState.Present, result.Target.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            result.CaptureIdentity.WindowTitleObservation.State);
        Assert.Equal(1, targetApi.Process.DisposeCount);
        Assert.Equal(1, windowTextApi.CallCount);
        Assert.Equal(2, deadline.CreatedDeadlineCount);
        Assert.Equal(1, deadline.WaitCount);
        Assert.True(titleReader.IsWorkerAlive);

        windowTextApi.Release.Set();
        Assert.True(SpinWait.SpinUntil(
            () => !titleReader.IsWorkerAlive,
            TimeSpan.FromSeconds(2)));
        Assert.True(windowTextApi.IsBufferClear);
    }

    private static WindowTitleReadResult Read(
        FailStopWindowsCaptureWindowTitleReader reader)
    {
        var state = reader.ReadWindowTitle(WindowHandle, out var value);
        return new WindowTitleReadResult(state, value);
    }

    private readonly record struct WindowTitleReadResult(
        WindowsCaptureObservationReadState State,
        string Value);

    private sealed class ManualWindowTitleDeadline
        : IWindowsCaptureWindowTitleDeadline
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource>
            _expirationSignals = new();
        private long _lastCreatedDeadline;
        private long _expiredThroughDeadline;
        private int _createdDeadlineCount;
        private int _waitCount;

        internal int CreatedDeadlineCount => Volatile.Read(
            ref _createdDeadlineCount);

        internal int WaitCount => Volatile.Read(ref _waitCount);

        public long CreateDeadline(TimeSpan timeout)
        {
            Assert.True(timeout > TimeSpan.Zero);
            _ = Interlocked.Increment(ref _createdDeadlineCount);
            var deadline = Interlocked.Increment(ref _lastCreatedDeadline);
            Assert.True(_expirationSignals.TryAdd(
                deadline,
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)));
            return deadline;
        }

        public bool IsExpired(long deadline)
        {
            return Volatile.Read(ref _expiredThroughDeadline) >= deadline;
        }

        public void Wait(Task completion, long deadline)
        {
            _ = Interlocked.Increment(ref _waitCount);
            var expiration = _expirationSignals[deadline];
            _ = Task.WaitAny(completion, expiration.Task);
        }

        internal void ExpireLatest()
        {
            ExpireLatestWithoutWake();
            var deadline = Volatile.Read(ref _lastCreatedDeadline);
            foreach (var expiration in _expirationSignals
                .Where(entry => entry.Key <= deadline)
                .Select(entry => entry.Value))
            {
                expiration.TrySetResult();
            }
        }

        internal void ExpireLatestWithoutWake()
        {
            var deadline = Volatile.Read(ref _lastCreatedDeadline);
            Interlocked.Exchange(ref _expiredThroughDeadline, deadline);
        }
    }

    private sealed class ControlledWindowTextBufferApi
        : IWindowsCaptureWindowTextBufferApi
    {
        private readonly WindowsCaptureWindowTextBufferReadResult _result;
        private readonly string _value;
        private char[]? _buffer;
        private int _callCount;
        private int _workerThreadId;

        internal ControlledWindowTextBufferApi(
            WindowsCaptureWindowTextBufferReadResult result,
            string value,
            bool initiallyReleased = false)
        {
            _result = result;
            _value = value;
            if (initiallyReleased)
            {
                Release.Set();
            }
        }

        internal ManualResetEventSlim Entered { get; } = new();

        internal ManualResetEventSlim Release { get; } = new();

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int WorkerThreadId => Volatile.Read(ref _workerThreadId);

        internal HashSet<int> WorkerThreadIds { get; } = [];

        internal bool BufferWasClearOnEntry { get; private set; } = true;

        internal bool IsBufferClear =>
            _buffer is not null && _buffer.All(character => character == '\0');

        public WindowsCaptureWindowTextBufferReadResult ReadWindowText(
            ulong windowHandle,
            char[] buffer)
        {
            Assert.Equal(WindowHandle, windowHandle);
            _ = Interlocked.Increment(ref _callCount);
            var workerThreadId = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _workerThreadId, workerThreadId);
            lock (WorkerThreadIds)
            {
                _ = WorkerThreadIds.Add(workerThreadId);
            }

            _buffer = buffer;
            BufferWasClearOnEntry &= buffer.All(character => character == '\0');
            Entered.Set();
            Release.Wait();

            if (_result.State is WindowsCaptureObservationReadState.Present)
            {
                _value.CopyTo(0, buffer, 0, _value.Length);
                buffer[_value.Length] = '\0';
            }

            return _result;
        }
    }

    private sealed class ThrowingWindowTextBufferApi
        : IWindowsCaptureWindowTextBufferApi
    {
        private readonly Exception _exception;
        private char[]? _buffer;
        private int _callCount;

        internal ThrowingWindowTextBufferApi(
            Exception exception,
            bool initiallyReleased = false)
        {
            _exception = exception;
            if (initiallyReleased)
            {
                Release.Set();
            }
        }

        internal ManualResetEventSlim Entered { get; } = new();

        internal ManualResetEventSlim Release { get; } = new();

        internal int CallCount => Volatile.Read(ref _callCount);

        internal HashSet<int> WorkerThreadIds { get; } = [];

        internal bool IsBufferClear =>
            _buffer is not null && _buffer.All(character => character == '\0');

        public WindowsCaptureWindowTextBufferReadResult ReadWindowText(
            ulong windowHandle,
            char[] buffer)
        {
            Assert.Equal(WindowHandle, windowHandle);
            _ = Interlocked.Increment(ref _callCount);
            lock (WorkerThreadIds)
            {
                _ = WorkerThreadIds.Add(Environment.CurrentManagedThreadId);
            }

            _buffer = buffer;
            Entered.Set();
            Release.Wait();
            throw _exception;
        }
    }

    private sealed class StableTargetNativeApi(
        IWindowsCaptureWindowTitleReader titleReader)
        : IWindowsCaptureTargetNativeApi
    {
        private const uint ProcessId = 42;

        internal TrackingTargetProcess Process { get; } = new();

        public bool IsSupportedPlatform => true;

        public bool TryGetForegroundWindow(out ulong windowHandle)
        {
            windowHandle = WindowHandle;
            return true;
        }

        public bool TryGetWindowOwner(
            ulong windowHandle,
            out WindowsCaptureWindowOwner owner)
        {
            _ = windowHandle;
            owner = new WindowsCaptureWindowOwner(7, ProcessId);
            return true;
        }

        public bool TryGetDisplayTarget(
            ulong windowHandle,
            out WindowsCaptureDisplayAnchor displayTarget)
        {
            _ = windowHandle;
            displayTarget = new WindowsCaptureDisplayAnchor(
                0x5678,
                @"\\.\DISPLAY1");
            return true;
        }

        public bool TryOpenProcess(
            uint processId,
            out IWindowsCaptureTargetProcess? process)
        {
            Assert.Equal(ProcessId, processId);
            process = Process;
            return true;
        }

        public WindowsCaptureObservationReadState ReadWindowTitle(
            ulong windowHandle,
            out string value)
        {
            return titleReader.ReadWindowTitle(windowHandle, out value);
        }
    }

    private sealed class TrackingTargetProcess : IWindowsCaptureTargetProcess
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool TryGetProcessId(out uint processId)
        {
            processId = 42;
            return true;
        }

        public bool TryGetCreationTime100ns(out ulong creationTime100ns)
        {
            creationTime100ns = 123_456;
            return true;
        }

        public bool TryGetActive(out bool active)
        {
            active = true;
            return true;
        }

        public WindowsCaptureObservationReadState ReadExecutableName(
            out string value)
        {
            value = "editor.exe";
            return WindowsCaptureObservationReadState.Present;
        }

        public WindowsCaptureObservationReadState ReadPackageFamilyName(
            out string value)
        {
            value = "Contoso.Editor_123456789abcd";
            return WindowsCaptureObservationReadState.Present;
        }

        public WindowsCaptureObservationReadState ReadPublisherCertificateSha256(
            out string value)
        {
            value = new string('a', 64);
            return WindowsCaptureObservationReadState.Present;
        }

        public void Dispose()
        {
            _ = Interlocked.Increment(ref _disposeCount);
        }
    }
}
