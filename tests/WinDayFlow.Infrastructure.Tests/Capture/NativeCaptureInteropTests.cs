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
        Assert.Equal(112, layout.RuntimeAuthorizationSize);
        Assert.Equal(8, layout.RuntimeAuthorizationRevisionOffset);
        Assert.Equal(16, layout.RuntimeAuthorizationTargetEpochOffset);
        Assert.Equal(48, layout.RuntimeAuthorizationDecisionOffset);
        Assert.Equal(64, layout.CommandAdmissionSize);
        Assert.Equal(16, layout.CommandAdmissionRuntimeRevisionOffset);
        Assert.Equal(24, layout.CommandAdmissionPersistenceGenerationOffset);
        Assert.Equal(32, layout.CommandAdmissionTargetEpochOffset);
        Assert.Equal(40, layout.CommandAdmissionAuthorizationEpochOffset);
        Assert.Equal(48, layout.CommandAdmissionNonceOffset);
        Assert.Equal(80, layout.EventSize);
        Assert.Equal(8, layout.EventSequenceOffset);
        Assert.Equal(48, layout.EventPersistenceGenerationOffset);
        Assert.Equal(56, layout.EventTargetEpochOffset);
        Assert.Equal(-9, (int)NativeCaptureResult.TargetMismatch);
        Assert.Equal(-10, (int)NativeCaptureResult.PolicyRevisionGap);
        Assert.Equal(-11, (int)NativeCaptureResult.GenerationExhausted);
        Assert.Equal(-12, (int)NativeCaptureResult.AdmissionRequired);
        Assert.Equal(-13, (int)NativeCaptureResult.AdmissionRejected);
        Assert.Equal(
            1UL << 5,
            (ulong)NativeCaptureCapabilities.TargetScopedAuthorization);
        Assert.Equal(
            1UL << 6,
            (ulong)NativeCaptureCapabilities.PersistenceGenerationBarrier);
        Assert.Equal(
            1UL << 7,
            (ulong)NativeCaptureCapabilities.DeterministicStop);
        Assert.Equal(
            1UL << 8,
            (ulong)NativeCaptureCapabilities.CommandAdmission);
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

    [Theory]
    [InlineData((ulong)NativeCaptureCapabilities.TargetScopedAuthorization)]
    [InlineData((ulong)(NativeCaptureCapabilities.PrivacyGuard
        | NativeCaptureCapabilities.EventQueue
        | NativeCaptureCapabilities.CommandAdmission))]
    [InlineData((ulong)(NativeCaptureCapabilities.PrivacyGuard
        | NativeCaptureCapabilities.EventQueue
        | NativeCaptureCapabilities.ScreenCapture
        | NativeCaptureCapabilities.H264Chunks))]
    [InlineData((ulong)(NativeCaptureCapabilities.PrivacyGuard
        | NativeCaptureCapabilities.EventQueue
        | NativeCaptureCapabilities.H264Chunks))]
    public void KnownCapabilityDependencyViolationsAreRejected(ulong rawCapabilities)
    {
        using var nativeApi = new FakeNativeCaptureApi
        {
            Capabilities = (NativeCaptureCapabilities)rawCapabilities,
        };

        var probe = NativeCaptureBackend.Probe(nativeApi);

        Assert.True(probe.LibraryLoaded);
        Assert.False(probe.AbiCompatible);
        Assert.NotNull(probe.Failure);
        Assert.Throws<BadImageFormatException>(() => new NativeCaptureBackend(
            new NativeCaptureConfiguration(Path.GetTempPath()),
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
            nativeApi));
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

        var explicitCalls = 0;
        NativeCaptureResult FailingExplicitDestroy(ref nuint value)
        {
            _ = value;
            explicitCalls++;
            return NativeCaptureResult.InternalError;
        }

        var explicitHandle = new SafeCaptureHandle(2, FailingExplicitDestroy);
        Assert.Equal(NativeCaptureResult.InternalError, explicitHandle.DestroyExplicit());
        explicitHandle.Dispose();
        Assert.Equal(1, explicitCalls);
        Assert.True(explicitHandle.IsClosed);
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
            () => NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 0));
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
                RuntimePolicyRevision: 1));
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
        Assert.True(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.TargetScopedAuthorization));
        Assert.True(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.PersistenceGenerationBarrier));
        Assert.True(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.DeterministicStop));
        Assert.True(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.CommandAdmission));
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
        var authorization = CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2);
        var persistenceGeneration = await backend
            .UpdateRuntimeAuthorizationAsync(authorization);
        var startAdmission = await backend.TryIssueCommandAdmissionAsync(
            CaptureAdmissionOperation.Start,
            authorization.RuntimePolicyRevision,
            persistenceGeneration,
            authorization.Target.TargetEpoch);
        Assert.NotNull(startAdmission);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => backend.StartAuthorizedAsync(startAdmission.Value));
        await Assert.ThrowsAsync<NotSupportedException>(() => backend.PauseAsync());
        var resumeAdmission = await backend.TryIssueCommandAdmissionAsync(
            CaptureAdmissionOperation.Resume,
            authorization.RuntimePolicyRevision,
            persistenceGeneration,
            authorization.Target.TargetEpoch);
        Assert.Null(resumeAdmission);
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
        var revisionOne = NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1);
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
                revisionTwo.RuntimePolicyRevision,
                NativeCapturePolicyDecision.Allow)));
        Assert.Equal(-8, conflicting.ResultCode);
    }

    [Fact]
    public async Task RealRuntimeAuthorizationAdvancesAndRevokesPersistenceGeneration()
    {
        var probe = NativeCaptureBackend.Probe();
        if (!RequireNativeBinary(probe))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        using var backend = CreateBackend(directory.Path);
        var first = await backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2));
        var secondAuthorization = new NativeCaptureRuntimeAuthorization(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 3).PrivacyContext,
            NativeCaptureTargetIdentity.Present(
                windowHandle: 0x5678,
                processId: 43,
                processCreationTime100ns: 101,
                targetEpoch: 2));
        var second = await backend.UpdateRuntimeAuthorizationAsync(secondAuthorization);
        var revoked = await backend.RevokeRuntimeAuthorizationAsync();

        Assert.True(first > 0);
        Assert.True(second > first);
        Assert.True(revoked >= second);
    }

    [Fact]
    public async Task ManagedRuntimeAuthorizationMarshalsOneAtomicTargetSnapshot()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var authorization = CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2);

        var generation = await backend.UpdateRuntimeAuthorizationAsync(authorization);

        Assert.True(generation > 0);
        Assert.Equal<uint>(112, nativeApi.LastAuthorizationStructSize);
        Assert.Equal<ulong>(2, nativeApi.LastAuthorizationRevision);
        Assert.Equal<ulong>(1, nativeApi.LastAuthorizationTargetEpoch);
        Assert.Equal<ulong>(0x1234, nativeApi.LastAuthorizationWindowHandle);
        Assert.Equal<uint>(42, nativeApi.LastAuthorizationProcessId);
        Assert.Equal<ulong>(100, nativeApi.LastAuthorizationProcessCreationTime100ns);
        Assert.Equal<uint>(1, nativeApi.LastAuthorizationTargetFlags);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            nativeApi.LastAuthorizationConsent);
        Assert.True(await backend.RevokeRuntimeAuthorizationAsync() >= generation);
        Assert.Equal(1, nativeApi.RevokeRuntimeAuthorizationCallCount);
    }

    [Fact]
    public async Task BackendRejectsRegressedPersistenceGenerations()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var allowed = CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2);
        var current = await backend.UpdateRuntimeAuthorizationAsync(allowed);
        nativeApi.NextUpdatePersistenceGeneration = current;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.UpdateRuntimeAuthorizationAsync(
                allowed.WithRuntimePolicyRevision(3)));

        nativeApi.NextUpdatePersistenceGeneration = current - 1;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.UpdateRuntimeAuthorizationAsync(
                allowed.WithRuntimePolicyRevision(3)));

        nativeApi.NextRevokePersistenceGeneration = current - 1;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.RevokeRuntimeAuthorizationAsync());
    }

    [Fact]
    public async Task EqualGenerationAcceptsAValueEqualIdempotentAuthorization()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var first = CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2);
        var current = await backend.UpdateRuntimeAuthorizationAsync(first);
        var equalButDistinct = CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2);
        Assert.NotSame(first, equalButDistinct);
        nativeApi.NextUpdatePersistenceGeneration = current;

        var repeated = await backend.UpdateRuntimeAuthorizationAsync(equalButDistinct);

        Assert.Equal(current, repeated);
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
                NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 2)));
    }

    [Fact]
    public void StandaloneDisposeRevokesBeforeStopWaitAndExplicitDestroy()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        var backend = CreateBackend(directory.Path, nativeApi);

        backend.Dispose();
        backend.Dispose();

        Assert.True(nativeApi.RevokeSequence < nativeApi.RequestStopSequence);
        Assert.True(nativeApi.RequestStopSequence < nativeApi.WaitStoppedSequence);
        Assert.True(nativeApi.WaitStoppedSequence < nativeApi.DestroySequence);
        Assert.Equal(1, nativeApi.DestroyCallCount);
    }

    [Fact]
    public async Task ConcurrentStandaloneDisposeWaitsForOneCompletedDestroy()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        var backend = CreateBackend(directory.Path, nativeApi);
        nativeApi.BlockNextRuntimeAuthorizationUpdate();
        var update = Task.Run(() => backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2)));
        await nativeApi.RuntimeAuthorizationUpdateStarted
            .WaitAsync(TimeSpan.FromSeconds(2));

        var firstDispose = Task.Run(backend.Dispose);
        var secondDispose = Task.Run(backend.Dispose);
        Assert.False(firstDispose.IsCompleted && secondDispose.IsCompleted);
        nativeApi.ReleaseRuntimeAuthorizationUpdate();

        await update;
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, nativeApi.DestroyCallCount);
    }

    [Fact]
    public async Task QueuedAuthorizationCannotReallowAfterStandaloneDisposeBegins()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        var backend = CreateBackend(directory.Path, nativeApi);
        nativeApi.BlockNextRuntimeAuthorizationUpdate();
        var inFlight = Task.Run(() => backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2)));
        await nativeApi.RuntimeAuthorizationUpdateStarted
            .WaitAsync(TimeSpan.FromSeconds(2));
        var queued = backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 3));
        var dispose = Task.Run(backend.Dispose);
        await WaitUntilAsync(
            () => backend.IsShutdownStarted,
            TimeSpan.FromSeconds(2));

        nativeApi.ReleaseRuntimeAuthorizationUpdate();
        await inFlight;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, nativeApi.UpdateRuntimeAuthorizationCallCount);
        Assert.Equal(1, nativeApi.DestroyCallCount);
    }

    [Fact]
    public async Task ConcurrentDisposeSharesDestroyFailureAfterManagedCleanup()
    {
        using var directory = new TemporaryDirectory();
        var destroyFailure = new InvalidOperationException("destroy failed");
        using var nativeApi = new FakeNativeCaptureApi
        {
            DestroyException = destroyFailure,
        };
        var backend = CreateBackend(directory.Path, nativeApi);

        var first = Task.Run(() => Record.Exception(backend.Dispose));
        var second = Task.Run(() => Record.Exception(backend.Dispose));
        var failures = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(failures, failure => Assert.Same(destroyFailure, failure));
        Assert.Equal(1, nativeApi.DestroyCallCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            backend.UpdateRuntimeAuthorizationAsync(
                CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2)));
    }

    [Fact]
    public async Task ChunkCommittedEventsAreDeliveredAsTypedNotifications()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var persistenceGeneration = await backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2));
        var committed = new TaskCompletionSource<NativeCaptureChunkCommitted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backend.ChunkCommitted += (_, args) => committed.TrySetResult(args.Chunk);

        nativeApi.Enqueue(
            sequence: 1,
            NativeCaptureEventKind.ChunkCommitted,
            CaptureState.Recording,
            detail: "chunks/20260716-120000.mp4",
            persistenceGeneration: persistenceGeneration,
            targetEpoch: 1);

        var chunk = await committed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal<ulong>(1, chunk.Sequence);
        Assert.Equal("chunks/20260716-120000.mp4", chunk.ArtifactIdentifier);
        Assert.Equal(CaptureState.Recording, chunk.State);
        Assert.Equal<uint>(0, chunk.DroppedBefore);
        Assert.Equal(persistenceGeneration, chunk.PersistenceGeneration);
        Assert.Equal<ulong>(1, chunk.TargetEpoch);
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
    public async Task StaleChunkAuthorizationIsFaultedBeforeManagedPublication()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var currentGeneration = await backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2));
        var published = 0;
        backend.ChunkCommitted += (_, _) => Interlocked.Increment(ref published);

        nativeApi.Enqueue(
            sequence: 1,
            NativeCaptureEventKind.ChunkCommitted,
            CaptureState.Recording,
            detail: "chunks/stale.mp4",
            persistenceGeneration: currentGeneration - 1,
            targetEpoch: 1);

        await WaitUntilAsync(
            () => backend.CurrentStatus.State == CaptureState.Faulted,
            TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref published));
        Assert.True(nativeApi.RequestStopCallCount > 0);
    }

    [Fact]
    public async Task FoundationOnlyBackendRejectsEveryCommittedChunkEvent()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi
        {
            Capabilities = NativeCaptureAbiContract.FoundationCapabilities,
        };
        using var backend = CreateBackend(directory.Path, nativeApi);
        var published = 0;
        backend.ChunkCommitted += (_, _) => Interlocked.Increment(ref published);

        nativeApi.Enqueue(
            sequence: 1,
            NativeCaptureEventKind.ChunkCommitted,
            CaptureState.Recording,
            detail: "chunks/unsafe.mp4");

        await WaitUntilAsync(
            () => backend.CurrentStatus.State == CaptureState.Faulted,
            TimeSpan.FromSeconds(2));
        Assert.Equal(0, Volatile.Read(ref published));
        Assert.True(nativeApi.RequestStopCallCount > 0);
    }

    [Fact]
    public async Task ChunkPolledBeforeAuthorizationUpdateReturnsUsesTheNewBoundary()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        using var backend = CreateBackend(directory.Path, nativeApi);
        var committed = new TaskCompletionSource<NativeCaptureChunkCommitted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        backend.ChunkCommitted += (_, eventArgs) =>
            committed.TrySetResult(eventArgs.Chunk);
        nativeApi.EnqueueChunkDuringNextAuthorizationUpdate = true;

        var generation = await backend.UpdateRuntimeAuthorizationAsync(
            CreateAllowedRuntimeAuthorization(runtimePolicyRevision: 2));
        var chunk = await committed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(generation, chunk.PersistenceGeneration);
        Assert.Equal<ulong>(1, chunk.TargetEpoch);
        Assert.NotEqual(CaptureState.Faulted, backend.CurrentStatus.State);
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

    [Fact]
    public async Task RuntimeDestroyDoesNotWaitForABlockedManagedSubscriber()
    {
        using var directory = new TemporaryDirectory();
        using var nativeApi = new FakeNativeCaptureApi();
        var backend = CreateBackend(directory.Path, nativeApi);
        var owner = new NativeCaptureRuntimeOwner(
            backend,
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1));
        var subscriberStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubscriber = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        owner.StatusChanged += (_, _) =>
        {
            subscriberStarted.TrySetResult();
            releaseSubscriber.Task.GetAwaiter().GetResult();
        };
        try
        {
            nativeApi.Enqueue(
                sequence: 1,
                NativeCaptureEventKind.StateChanged,
                CaptureState.Recording,
                detail: "recording");
            await subscriberStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, nativeApi.DestroyCallCount);
        }
        finally
        {
            releaseSubscriber.TrySetResult();
            await owner.DisposeAsync();
        }
    }

    private static NativeCaptureBackend CreateBackend(string outputDirectory)
    {
        return new NativeCaptureBackend(
            new NativeCaptureConfiguration(outputDirectory),
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1));
    }

    private static NativeCaptureBackend CreateBackend(
        string outputDirectory,
        INativeCaptureApi nativeApi)
    {
        return new NativeCaptureBackend(
            new NativeCaptureConfiguration(outputDirectory),
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
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

    private static NativeCaptureRuntimeAuthorization CreateAllowedRuntimeAuthorization(
        ulong runtimePolicyRevision)
    {
        return new NativeCaptureRuntimeAuthorization(
            new NativeCapturePrivacyContext(
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                NativeCapturePolicyDecision.Allow,
                runtimePolicyRevision),
            NativeCaptureTargetIdentity.Present(
                windowHandle: 0x1234,
                processId: 42,
                processCreationTime100ns: 100,
                targetEpoch: 1));
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
        private readonly ManualResetEventSlim _eventPolled = new(initialState: false);
        private readonly ManualResetEventSlim _runtimeAuthorizationUpdateStarted =
            new(initialState: false);
        private readonly ManualResetEventSlim _releaseRuntimeAuthorizationUpdate =
            new(initialState: false);
        private bool _closed;
        private int _getCapabilitiesCallCount;
        private int _destroyCallCount;
        private int _requestStopCallCount;
        private int _revokeRuntimeAuthorizationCallCount;
        private int _updateRuntimeAuthorizationCallCount;
        private int _operationSequence;
        private int _signalNextPoll;
        private int _blockNextRuntimeAuthorizationUpdate;

        public uint AbiVersion { get; init; } = NativeCaptureAbiContract.AbiVersion;

        public NativeCaptureCapabilities Capabilities { get; init; } =
            NativeCaptureCapabilities.PrivacyGuard
            | NativeCaptureCapabilities.EventQueue
            | NativeCaptureCapabilities.ScreenCapture
            | NativeCaptureCapabilities.H264Chunks
            | NativeCaptureCapabilities.TargetScopedAuthorization
            | NativeCaptureCapabilities.PersistenceGenerationBarrier
            | NativeCaptureCapabilities.DeterministicStop
            | NativeCaptureCapabilities.CommandAdmission;

        private ulong _persistenceGeneration;
        private ulong _runtimePolicyRevision;
        private ulong _targetEpoch;
        private ulong _authorizationEpoch;
        private bool _runtimeAuthorizationRevoked;

        public int GetCapabilitiesCallCount => Volatile.Read(ref _getCapabilitiesCallCount);

        public int DestroyCallCount => Volatile.Read(ref _destroyCallCount);

        public int RequestStopCallCount => Volatile.Read(ref _requestStopCallCount);

        public int RevokeRuntimeAuthorizationCallCount =>
            Volatile.Read(ref _revokeRuntimeAuthorizationCallCount);

        public int UpdateRuntimeAuthorizationCallCount =>
            Volatile.Read(ref _updateRuntimeAuthorizationCallCount);

        public uint LastAuthorizationStructSize { get; private set; }

        public ulong LastAuthorizationRevision { get; private set; }

        public ulong LastAuthorizationTargetEpoch { get; private set; }

        public ulong LastAuthorizationWindowHandle { get; private set; }

        public ulong LastAuthorizationProcessCreationTime100ns { get; private set; }

        public uint LastAuthorizationProcessId { get; private set; }

        public uint LastAuthorizationTargetFlags { get; private set; }

        public NativeCapturePolicyDecision LastAuthorizationConsent { get; private set; }

        public ulong? NextUpdatePersistenceGeneration { get; set; }

        public ulong? NextRevokePersistenceGeneration { get; set; }

        public bool EnqueueChunkDuringNextAuthorizationUpdate { get; set; }

        public Exception? DestroyException { get; init; }

        public Task RuntimeAuthorizationUpdateStarted => Task.Run(
            _runtimeAuthorizationUpdateStarted.Wait);

        public int RevokeSequence { get; private set; }

        public int RequestStopSequence { get; private set; }

        public int WaitStoppedSequence { get; private set; }

        public int DestroySequence { get; private set; }

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

        public NativeCaptureResult UpdateRuntimeAuthorization(
            SafeCaptureHandle handle,
            ref NativeCaptureRuntimeAuthorizationV1 authorization,
            out ulong persistenceGeneration)
        {
            _ = handle;
            Interlocked.Increment(ref _updateRuntimeAuthorizationCallCount);
            if (Interlocked.Exchange(ref _blockNextRuntimeAuthorizationUpdate, 0) != 0)
            {
                _runtimeAuthorizationUpdateStarted.Set();
                _releaseRuntimeAuthorizationUpdate.Wait();
            }

            LastAuthorizationStructSize = authorization.StructSize;
            LastAuthorizationRevision = authorization.RuntimePolicyRevision;
            LastAuthorizationTargetEpoch = authorization.TargetEpoch;
            LastAuthorizationWindowHandle = authorization.TargetWindowHandle;
            LastAuthorizationProcessCreationTime100ns =
                authorization.TargetProcessCreationTime100ns;
            LastAuthorizationProcessId = authorization.TargetProcessId;
            LastAuthorizationTargetFlags = authorization.TargetFlags;
            LastAuthorizationConsent =
                (NativeCapturePolicyDecision)authorization.ConsentGranted;
            _runtimePolicyRevision = authorization.RuntimePolicyRevision;
            _targetEpoch = authorization.TargetEpoch;
            _persistenceGeneration++;
            _runtimeAuthorizationRevoked = authorization.TargetFlags == 0;
            persistenceGeneration = NextUpdatePersistenceGeneration
                ?? _persistenceGeneration;
            NextUpdatePersistenceGeneration = null;
            if (EnqueueChunkDuringNextAuthorizationUpdate)
            {
                EnqueueChunkDuringNextAuthorizationUpdate = false;
                _eventPolled.Reset();
                Interlocked.Exchange(ref _signalNextPoll, 1);
                Enqueue(
                    sequence: 1,
                    NativeCaptureEventKind.ChunkCommitted,
                    CaptureState.Recording,
                    detail: "chunks/racing.mp4",
                    persistenceGeneration: persistenceGeneration,
                    targetEpoch: authorization.TargetEpoch);
                if (!_eventPolled.Wait(TimeSpan.FromSeconds(2)))
                {
                    return NativeCaptureResult.Timeout;
                }
            }

            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult IssueCommandAdmission(
            SafeCaptureHandle handle,
            NativeCaptureCommand command,
            ulong expectedPersistenceGeneration,
            ulong expectedTargetEpoch,
            ref NativeCaptureCommandAdmissionV1 admission)
        {
            _ = handle;
            _ = command;
            if (_runtimeAuthorizationRevoked
                || expectedPersistenceGeneration != _persistenceGeneration
                || expectedTargetEpoch != _targetEpoch)
            {
                return NativeCaptureResult.AdmissionRejected;
            }

            admission.StructSize = NativeCaptureAbiContract.CommandAdmissionStructureSize;
            admission.AbiVersion = NativeCaptureAbiContract.AbiVersion;
            admission.InstanceEpoch = 1;
            admission.RuntimePolicyRevision = _runtimePolicyRevision;
            admission.PersistenceGeneration = _persistenceGeneration;
            admission.TargetEpoch = _targetEpoch;
            admission.AuthorizationEpoch = ++_authorizationEpoch;
            admission.NonceLow = _authorizationEpoch;
            admission.NonceHigh = ~_authorizationEpoch;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult StartAuthorized(
            SafeCaptureHandle handle,
            ref NativeCaptureCommandAdmissionV1 admission)
        {
            _ = handle;
            _ = admission;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult ResumeAuthorized(
            SafeCaptureHandle handle,
            ref NativeCaptureCommandAdmissionV1 admission)
        {
            _ = handle;
            _ = admission;
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult RevokeRuntimeAuthorization(
            SafeCaptureHandle handle,
            out ulong persistenceGeneration)
        {
            _ = handle;
            Interlocked.Increment(ref _revokeRuntimeAuthorizationCallCount);
            RevokeSequence = Interlocked.Increment(ref _operationSequence);
            if (!_runtimeAuthorizationRevoked)
            {
                _persistenceGeneration++;
                _runtimeAuthorizationRevoked = true;
            }

            persistenceGeneration = NextRevokePersistenceGeneration
                ?? _persistenceGeneration;
            NextRevokePersistenceGeneration = null;
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
            RequestStopSequence = Interlocked.Increment(ref _operationSequence);
            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult WaitStopped(
            SafeCaptureHandle handle,
            uint timeoutMilliseconds)
        {
            _ = handle;
            _ = timeoutMilliseconds;
            WaitStoppedSequence = Interlocked.Increment(ref _operationSequence);
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
            if (Interlocked.Exchange(ref _signalNextPoll, 0) != 0)
            {
                _eventPolled.Set();
            }

            return NativeCaptureResult.Ok;
        }

        public NativeCaptureResult Destroy(ref nuint handle)
        {
            Interlocked.Increment(ref _destroyCallCount);
            DestroySequence = Interlocked.Increment(ref _operationSequence);
            if (DestroyException is { } exception)
            {
                throw exception;
            }

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
            _releaseRuntimeAuthorizationUpdate.Set();
            _runtimeAuthorizationUpdateStarted.Dispose();
            _releaseRuntimeAuthorizationUpdate.Dispose();
            _eventPolled.Dispose();
            _eventAvailable.Dispose();
        }

        public void BlockNextRuntimeAuthorizationUpdate()
        {
            _runtimeAuthorizationUpdateStarted.Reset();
            _releaseRuntimeAuthorizationUpdate.Reset();
            Interlocked.Exchange(ref _blockNextRuntimeAuthorizationUpdate, 1);
        }

        public void ReleaseRuntimeAuthorizationUpdate()
        {
            _releaseRuntimeAuthorizationUpdate.Set();
        }

        public void Enqueue(
            ulong sequence,
            NativeCaptureEventKind kind,
            CaptureState state,
            string detail,
            CaptureReasonCode reason = CaptureReasonCode.None,
            CaptureErrorCode error = CaptureErrorCode.None,
            uint droppedBefore = 0,
            ulong persistenceGeneration = 0,
            ulong targetEpoch = 0)
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
            captureEvent.PersistenceGeneration = persistenceGeneration;
            captureEvent.TargetEpoch = targetEpoch;
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
