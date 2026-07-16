using WinDayFlow.Application.Capture;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCaptureInteropTests
{
    [Fact]
    public void ManagedAbiLayoutMatchesTheX64CContract()
    {
        var layout = NativeCaptureAbiContract.GetManagedLayout();

        Assert.Equal(8, layout.PointerSize);
        Assert.Equal(80, layout.ConfigSize);
        Assert.Equal(32, layout.ConfigOutputDirectoryOffset);
        Assert.Equal(80, layout.PrivacyContextSize);
        Assert.Equal(40, layout.PrivacyPolicyRevisionOffset);
        Assert.Equal(80, layout.EventSize);
        Assert.Equal(8, layout.EventSequenceOffset);
    }

    [Fact]
    public void IncompatibleAbiStopsBeforeCapabilityNegotiation()
    {
        using var nativeApi = new FakeNativeCaptureApi
        {
            AbiVersion = NativeCaptureAbiContract.AbiVersion + 1,
        };

        var probe = NativeCaptureBackend.Probe(nativeApi);

        Assert.True(probe.LibraryLoaded);
        Assert.False(probe.AbiCompatible);
        Assert.Equal(nativeApi.AbiVersion, probe.AbiVersion);
        Assert.Equal(NativeCaptureCapabilities.None, probe.Capabilities);
        Assert.Equal(0, nativeApi.GetCapabilitiesCallCount);
    }

    [Fact]
    public void NativeLibraryResolutionIsPinnedToTheApplicationDirectory()
    {
        var expected = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            NativeCaptureMethods.LibraryName));

        Assert.Equal(expected, NativeCaptureLibrary.AbsolutePath);
        Assert.True(Path.IsPathFullyQualified(NativeCaptureLibrary.AbsolutePath));
    }

    [Fact]
    public void SafeHandleTreatsOnlyZeroAsInvalidAndNeverThrowsFromRelease()
    {
        nuint releasedValue = 0;
        var releaseCalls = 0;
        NativeCaptureResult Release(ref nuint value)
        {
            releaseCalls++;
            releasedValue = value;
            value = 0;
            return NativeCaptureResult.Ok;
        }

        using (var handle = new SafeCaptureHandle(nuint.MaxValue, Release))
        {
            Assert.False(handle.IsInvalid);
        }

        Assert.Equal(1, releaseCalls);
        Assert.Equal(nuint.MaxValue, releasedValue);

        NativeCaptureResult ThrowingRelease(ref nuint value)
        {
            _ = value;
            throw new InvalidOperationException("release failed");
        }

        var throwingHandle = new SafeCaptureHandle(1, ThrowingRelease);
        var exception = Record.Exception(throwingHandle.Dispose);
        Assert.Null(exception);
        Assert.True(throwingHandle.IsClosed);
    }

    [Fact]
    public void ManagedContractsRejectValuesTheNativeBoundaryCannotAccept()
    {
        Assert.Throws<ArgumentException>(
            () => new NativeCaptureConfiguration("relative"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NativeCaptureConfiguration(
                Path.GetTempPath(),
                captureIntervalMilliseconds: 249));
        Assert.Throws<ArgumentException>(
            () => new NativeCaptureConfiguration(
                Path.GetTempPath(),
                captureIntervalMilliseconds: 20_000,
                chunkDurationMilliseconds: 10_000));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NativeCapturePrivacyContext.FailClosed(policyRevision: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NativeCapturePrivacyContext(
                (NativeCapturePolicyDecision)99,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                NativeCapturePolicyDecision.Unknown,
                PolicyRevision: 1));
    }

    [Fact]
    public void ProbeNegotiatesTheRealNativeFoundation()
    {
        var probe = NativeCaptureBackend.Probe();
        if (!RequireNativeBinary(probe))
        {
            return;
        }

        Assert.True(probe.AbiCompatible);
        Assert.Equal(NativeCaptureAbiContract.AbiVersion, probe.AbiVersion);
        Assert.True(probe.Capabilities.HasFlag(NativeCaptureCapabilities.PrivacyGuard));
        Assert.True(probe.Capabilities.HasFlag(NativeCaptureCapabilities.EventQueue));
        Assert.False(probe.Capabilities.HasFlag(NativeCaptureCapabilities.ScreenCapture));
        Assert.Null(probe.Failure);
    }

    [Fact]
    public async Task FoundationBackendPollsItsInitialEventButRemainsUnavailable()
    {
        var probe = NativeCaptureBackend.Probe();
        if (!RequireNativeBinary(probe))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        using var backend = CreateBackend(directory.Path);

        await WaitUntilAsync(
            () => backend.CurrentStatus.Sequence > 0,
            TimeSpan.FromSeconds(5));

        Assert.False(backend.SupportsScreenCapture);
        Assert.Equal(CaptureState.Unavailable, backend.CurrentStatus.State);
        Assert.Equal(CaptureReasonCode.BackendUnavailable, backend.CurrentStatus.Reason);
        Assert.True(backend.CurrentStatus.Sequence > 0);
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.StartAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.PauseAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.ResumeAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.StopAsync());
    }

    [Fact]
    public async Task PrivacyUpdatesRejectStaleAndConflictingRevisions()
    {
        var probe = NativeCaptureBackend.Probe();
        if (!RequireNativeBinary(probe))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var revisionOne = NativeCapturePrivacyContext.FailClosed(policyRevision: 1);
        using var backend = new NativeCaptureBackend(
            new NativeCaptureConfiguration(directory.Path),
            revisionOne);

        await backend.UpdatePrivacyContextAsync(revisionOne);
        var revisionTwo = CopyPrivacyContext(
            revisionOne,
            policyRevision: 2,
            revisionOne.ConsentGranted);
        await backend.UpdatePrivacyContextAsync(revisionTwo);

        var stale = await Assert.ThrowsAsync<NativeCaptureException>(
            () => backend.UpdatePrivacyContextAsync(revisionOne));
        Assert.Equal(-7, stale.ResultCode);
        Assert.Equal("update_privacy_context", stale.Operation);

        var conflicting = await Assert.ThrowsAsync<NativeCaptureException>(
            () => backend.UpdatePrivacyContextAsync(CopyPrivacyContext(
                revisionTwo,
                revisionTwo.PolicyRevision,
                NativeCapturePolicyDecision.Allow)));
        Assert.Equal(-8, conflicting.ResultCode);
    }

    [Fact]
    public async Task DisposeIsIdempotentAndRejectsFurtherNativeCalls()
    {
        var probe = NativeCaptureBackend.Probe();
        if (!RequireNativeBinary(probe))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var backend = CreateBackend(directory.Path);

        backend.Dispose();
        backend.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => backend.UpdatePrivacyContextAsync(
                NativeCapturePrivacyContext.FailClosed(policyRevision: 2)));
    }

    [Fact]
    public async Task ChunkCommittedEventsAreDeliveredAsTypedNotifications()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var committed = new TaskCompletionSource<NativeCaptureChunkCommitted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backend.ChunkCommitted += (_, args) => committed.TrySetResult(args.Chunk);

        nativeApi.Enqueue(
            sequence: 1,
            NativeCaptureEventKind.ChunkCommitted,
            CaptureState.Recording,
            detail: "chunks/20260716-120000.mp4");

        var chunk = await committed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal<ulong>(1, chunk.Sequence);
        Assert.Equal("chunks/20260716-120000.mp4", chunk.ArtifactIdentifier);
        Assert.Equal(CaptureState.Recording, chunk.State);
        Assert.Equal<uint>(0, chunk.DroppedBefore);
    }

    [Fact]
    public async Task DroppedNativeEventsFailClosedBeforeFurtherStateProjection()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);

        nativeApi.Enqueue(
            sequence: 3,
            NativeCaptureEventKind.Diagnostic,
            CaptureState.Recording,
            detail: "queue pressure",
            droppedBefore: 2);

        await WaitUntilAsync(
            () => backend.CurrentStatus.Sequence == 3,
            TimeSpan.FromSeconds(2));
        Assert.Equal(CaptureState.Faulted, backend.CurrentStatus.State);
        Assert.Equal(CaptureReasonCode.BackendFault, backend.CurrentStatus.Reason);
        Assert.Equal(CaptureErrorCode.NativeFailure, backend.CurrentStatus.ErrorCode);
        Assert.Contains("dropped 2 event", backend.CurrentStatus.Detail, StringComparison.Ordinal);
        Assert.True(nativeApi.RequestStopCallCount > 0);

        nativeApi.Enqueue(
            sequence: 4,
            NativeCaptureEventKind.StateChanged,
            CaptureState.Recording,
            detail: "must not resume");
        await Task.Delay(100);
        Assert.Equal<ulong>(3, backend.CurrentStatus.Sequence);
        Assert.Equal(CaptureState.Faulted, backend.CurrentStatus.State);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotStopOrderedNotificationDispatch()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var observedSequences = new List<ulong>();
        var observedSecond = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backend.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber failed");
        backend.StatusChanged += (_, args) =>
        {
            lock (observedSequences)
            {
                observedSequences.Add(args.Current.Sequence);
                if (args.Current.Sequence == 2)
                {
                    observedSecond.TrySetResult();
                }
            }
        };

        nativeApi.Enqueue(
            sequence: 1,
            NativeCaptureEventKind.StateChanged,
            CaptureState.Starting,
            detail: "starting");
        nativeApi.Enqueue(
            sequence: 2,
            NativeCaptureEventKind.StateChanged,
            CaptureState.Recording,
            detail: "recording");

        await observedSecond.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (observedSequences)
        {
            Assert.Equal(new ulong[] { 1, 2 }, observedSequences);
        }

        Assert.Equal(CaptureState.Recording, backend.CurrentStatus.State);
    }

    [Fact]
    public async Task DisposeFromStatusCallbackDoesNotWaitOnThePollingTaskItOwns()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        var backend = CreateBackend(directory.Path, nativeApi);
        var disposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backend.StatusChanged += (_, _) =>
        {
            backend.Dispose();
            disposed.TrySetResult();
        };

        nativeApi.Enqueue(
            sequence: 1,
            NativeCaptureEventKind.StateChanged,
            CaptureState.Recording,
            detail: "recording");

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.Dispose();
        Assert.Equal(1, nativeApi.DestroyCallCount);
    }

    private static NativeCaptureBackend CreateBackend(string outputDirectory)
    {
        return new NativeCaptureBackend(
            new NativeCaptureConfiguration(outputDirectory),
            NativeCapturePrivacyContext.FailClosed(policyRevision: 1));
    }

    private static NativeCaptureBackend CreateBackend(
        string outputDirectory,
        INativeCaptureApi nativeApi)
    {
        return new NativeCaptureBackend(
            new NativeCaptureConfiguration(outputDirectory),
            NativeCapturePrivacyContext.FailClosed(policyRevision: 1),
            nativeApi);
    }

    private static NativeCapturePrivacyContext CopyPrivacyContext(
        NativeCapturePrivacyContext source,
        ulong policyRevision,
        NativeCapturePolicyDecision consentGranted)
    {
        return new NativeCapturePrivacyContext(
            consentGranted,
            source.SessionUnlocked,
            source.SecureDesktopClear,
            source.RemoteSessionAllowed,
            source.PresentationAllowed,
            source.ApplicationAllowed,
            source.WindowAllowed,
            source.StorageAvailable,
            policyRevision);
    }

    private static bool RequireNativeBinary(NativeCaptureProbe probe)
    {
        var binaryPath = Path.Combine(
            AppContext.BaseDirectory,
            "WinDayFlow.Capture.Native.dll");
        var binaryExpected = File.Exists(binaryPath)
            || string.Equals(
                Environment.GetEnvironmentVariable("CI"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        if (!binaryExpected)
        {
            return false;
        }

        Assert.True(probe.LibraryLoaded, probe.Failure);
        return true;
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The native capture condition was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeNativeCaptureApi : INativeCaptureApi, IDisposable
    {
        private readonly object _eventSync = new();
        private readonly Queue<FakeNativeEvent> _events = new();
        private readonly AutoResetEvent _eventAvailable = new(initialState: false);
        private bool _closed;
        private int _getCapabilitiesCallCount;
        private int _destroyCallCount;
        private int _requestStopCallCount;

        public uint AbiVersion { get; init; } = NativeCaptureAbiContract.AbiVersion;

        public NativeCaptureCapabilities Capabilities { get; init; } =
            NativeCaptureCapabilities.PrivacyGuard
            | NativeCaptureCapabilities.EventQueue
            | NativeCaptureCapabilities.ScreenCapture
            | NativeCaptureCapabilities.H264Chunks;

        public int GetCapabilitiesCallCount => Volatile.Read(ref _getCapabilitiesCallCount);

        public int DestroyCallCount => Volatile.Read(ref _destroyCallCount);

        public int RequestStopCallCount => Volatile.Read(ref _requestStopCallCount);

        public uint GetAbiVersion() => AbiVersion;

        public NativeCaptureResult GetCapabilities(
            out NativeCaptureCapabilities capabilities)
        {
            Interlocked.Increment(ref _getCapabilitiesCallCount);
            capabilities = Capabilities;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult Create(
            ref NativeCaptureConfigV1 configuration,
            out nuint handle)
        {
            _ = configuration;
            handle = 1;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult UpdatePrivacyContext(
            SafeCaptureHandle handle,
            ref NativeCapturePrivacyContextV1 context)
        {
            _ = handle;
            _ = context;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult Start(SafeCaptureHandle handle)
        {
            _ = handle;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult Pause(SafeCaptureHandle handle)
        {
            _ = handle;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult Resume(SafeCaptureHandle handle)
        {
            _ = handle;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult RequestStop(SafeCaptureHandle handle)
        {
            _ = handle;
            Interlocked.Increment(ref _requestStopCallCount);
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult WaitStopped(
            SafeCaptureHandle handle,
            uint timeoutMilliseconds)
        {
            _ = handle;
            _ = timeoutMilliseconds;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult PollEvent(
            SafeCaptureHandle handle,
            uint timeoutMilliseconds,
            ref NativeCaptureEventV1 captureEvent,
            byte[] detailUtf8,
            uint detailUtf8Capacity,
            out uint detailUtf8Required)
        {
            _ = handle;
            FakeNativeEvent? queued;
            lock (_eventSync)
            {
                queued = _events.Count == 0 ? null : _events.Peek();
                if (queued is null && _closed)
                {
                    detailUtf8Required = 0;
                    return NativeCaptureResult.InvalidState;
                }
            }

            if (queued is null && timeoutMilliseconds > 0)
            {
                _eventAvailable.WaitOne(
                    checked((int)Math.Min(timeoutMilliseconds, 50U)));
                lock (_eventSync)
                {
                    queued = _events.Count == 0 ? null : _events.Peek();
                    if (queued is null && _closed)
                    {
                        detailUtf8Required = 0;
                        return NativeCaptureResult.InvalidState;
                    }
                }
            }

            if (queued is null)
            {
                detailUtf8Required = 0;
                return NativeCaptureResult.NoEvent;
            }

            detailUtf8Required = checked((uint)queued.DetailUtf8.Length + 1U);
            captureEvent = queued.Event;
            if (detailUtf8Capacity < detailUtf8Required)
            {
                return NativeCaptureResult.BufferTooSmall;
            }

            lock (_eventSync)
            {
                if (_events.Count == 0 || !ReferenceEquals(_events.Peek(), queued))
                {
                    return NativeCaptureResult.InternalError;
                }

                _events.Dequeue();
            }

            queued.DetailUtf8.CopyTo(detailUtf8, 0);
            detailUtf8[queued.DetailUtf8.Length] = 0;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult Destroy(ref nuint handle)
        {
            Interlocked.Increment(ref _destroyCallCount);
            lock (_eventSync)
            {
                _closed = true;
            }

            handle = 0;
            _eventAvailable.Set();
            return NativeCaptureResult.Ok;
        }

        public void Dispose()
        {
            _eventAvailable.Dispose();
        }

        public void Enqueue(
            ulong sequence,
            NativeCaptureEventKind kind,
            CaptureState state,
            string detail,
            CaptureReasonCode reason = CaptureReasonCode.None,
            CaptureErrorCode error = CaptureErrorCode.None,
            uint droppedBefore = 0)
        {
            var detailUtf8 = System.Text.Encoding.UTF8.GetBytes(detail);
            var captureEvent = NativeCaptureEventV1.Create();
            captureEvent.Sequence = sequence;
            captureEvent.TimestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            captureEvent.Kind = (int)kind;
            captureEvent.State = (int)state;
            captureEvent.Reason = (int)reason;
            captureEvent.Error = (int)error;
            captureEvent.DroppedBefore = droppedBefore;
            captureEvent.DetailUtf8Length = checked((uint)detailUtf8.Length);
            lock (_eventSync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _events.Enqueue(new FakeNativeEvent(captureEvent, detailUtf8));
            }

            _eventAvailable.Set();
        }

        private sealed record FakeNativeEvent(
            NativeCaptureEventV1 Event,
            byte[] DetailUtf8);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinDayFlow.NativeInterop.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
