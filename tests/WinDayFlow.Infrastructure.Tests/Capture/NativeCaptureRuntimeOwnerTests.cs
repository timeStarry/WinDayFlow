using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class NativeCaptureRuntimeOwnerTests
{
    private static readonly string[] ExpectedTerminationSequence =
    [
        "Block",
        "Revoke",
        "RequestStop",
        "WaitStopped:5000",
        "StopEventPump",
        "Destroy",
        "Complete",
    ];

    [Fact]
    public void OwnerRejectsTheLegacyCommandAdmissionProfile()
    {
        var backend = new ScriptedRuntimeBackend
        {
            AdvertisedCapabilities =
                NativeCaptureAbiContract.RuntimeSafetyCapabilities
                | NativeCaptureCapabilities.CommandAdmission,
        };

        Assert.Throws<NotSupportedException>(() => CreateOwner(backend));
        Assert.Equal("ConstructionFailureDispose", Assert.Single(backend.Operations));
    }

    [Fact]
    public void OwnerRejectsProfileWithoutDisplayWideContinuousAuthorization()
    {
        var backend = new ScriptedRuntimeBackend
        {
            AdvertisedCapabilities =
                NativeCaptureAbiContract.RuntimeOwnerCapabilities,
        };

        var exception = Assert.Throws<NotSupportedException>(
            () => CreateOwner(backend));

        Assert.Contains("display-wide continuous", exception.Message);
        Assert.Equal("ConstructionFailureDispose", Assert.Single(backend.Operations));
    }

    [Fact]
    public async Task ChunkCommitIsForwardedAsAnApplicationWakeHint()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = CreateOwner(backend);
        try
        {
            var observed = new TaskCompletionSource<CaptureChunkCommittedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            object? sender = null;
            owner.ChunkCommitted += (_, _) =>
                throw new InvalidOperationException("subscriber failed");
            owner.ChunkCommitted += (eventSender, eventArgs) =>
            {
                sender = eventSender;
                observed.TrySetResult(eventArgs);
            };

            backend.RaiseChunkCommitted();

            Assert.Same(
                CaptureChunkCommittedEventArgs.WakeHint,
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(owner, sender);
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentDisposeUsesOneStrictTerminationSequence()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = CreateOwner(backend);

        var first = owner.DisposeAsync().AsTask();
        var second = owner.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        Assert.Equal(
            ExpectedTerminationSequence,
            backend.Operations);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task TerminationProofExposesOnlyTheSharedStartedTermination()
    {
        var backend = new ScriptedRuntimeBackend();
        backend.BlockAuthorizationUpdate();
        var owner = CreateOwner(backend);
        var proof = Assert.IsAssignableFrom<
            INativeCapturePrivacySignalSinkTermination>(owner);

        Assert.False(proof.IsTerminationStarted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = proof.Termination;
        });

        var first = owner.DisposeAsync().AsTask();
        await backend.AuthorizationUpdateStarted.WaitAsync(
            TimeSpan.FromSeconds(5));
        var second = owner.DisposeAsync().AsTask();

        Assert.True(proof.IsTerminationStarted);
        Assert.Same(first, proof.Termination);
        Assert.Same(first, second);
        Assert.False(proof.Termination.IsCompleted);

        backend.ReleaseAuthorizationUpdate();
        await proof.Termination;
        Assert.True(proof.Termination.IsCompletedSuccessfully);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task TerminalAuthorizationInvalidStateIsOwnedByStopAndWait()
    {
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "update_runtime_authorization"),
            RevokeFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "revoke_runtime_authorization"),
        };
        var owner = CreateOwner(backend);

        backend.RaiseStatus(CaptureState.Faulted);
        await owner.Termination.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.DisposeAsync();

        Assert.Equal(ExpectedTerminationSequence, backend.Operations);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task TerminalAuthorizationRaceDoesNotHideWaitFailure()
    {
        var waitFailure = new NativeCaptureException(
            NativeCaptureResult.InternalError,
            "wait_stopped");
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "update_runtime_authorization"),
            RevokeFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "revoke_runtime_authorization"),
            WaitFailure = waitFailure,
        };
        var owner = CreateOwner(backend);

        backend.RaiseStatus(CaptureState.Faulted);
        var failure = await Assert.ThrowsAsync<NativeCaptureException>(
            () => owner.Termination);

        Assert.Same(waitFailure, failure);
        Assert.Equal("wait_stopped", failure.Operation);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task NonterminalAuthorizationInvalidStateRemainsVisible()
    {
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "update_runtime_authorization"),
            RevokeFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "revoke_runtime_authorization"),
        };
        var owner = CreateOwner(backend);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => owner.DisposeAsync().AsTask());

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.All(
            failure.InnerExceptions,
            static exception => Assert.IsType<NativeCaptureException>(exception));
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task ConcurrentDisposeSharesTheFilteredGenuineFailure()
    {
        var waitFailure = new NativeCaptureException(
            NativeCaptureResult.InternalError,
            "wait_stopped");
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "update_runtime_authorization"),
            RevokeFailure = new NativeCaptureException(
                NativeCaptureResult.InvalidState,
                "revoke_runtime_authorization"),
            WaitFailure = waitFailure,
        };
        var owner = CreateOwner(backend);

        backend.RaiseStatus(CaptureState.Faulted);
        var automaticTermination = owner.Termination;
        var hostedShutdown = owner.DisposeAsync().AsTask();

        Assert.Same(automaticTermination, hostedShutdown);
        Assert.Same(
            waitFailure,
            await Assert.ThrowsAsync<NativeCaptureException>(
                () => automaticTermination));
        Assert.Same(
            waitFailure,
            await Assert.ThrowsAsync<NativeCaptureException>(
                () => hostedShutdown));
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task EveryPreDestroyFailureIsAggregatedWithoutSkippingDestroy()
    {
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new InvalidOperationException("block failed"),
            RequestStopFailure = new InvalidOperationException("stop failed"),
            WaitFailure = new TimeoutException("wait failed"),
            PumpFailure = new InvalidOperationException("pump failed"),
            DestroyResult = NativeCaptureResult.InternalError,
        };
        var owner = CreateOwner(backend);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => owner.DisposeAsync().AsTask());

        Assert.Contains(
            failure.InnerExceptions,
            static exception => exception.Message.Contains("block failed", StringComparison.Ordinal));
        Assert.Contains(
            failure.InnerExceptions,
            static exception => exception.Message.Contains("stop failed", StringComparison.Ordinal));
        Assert.Contains(
            failure.InnerExceptions,
            static exception => exception is TimeoutException);
        Assert.Contains(
            failure.InnerExceptions,
            static exception => exception is NativeCaptureException);
        Assert.Equal("Destroy", backend.Operations[^2]);
        Assert.Equal("Complete", backend.Operations[^1]);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task QuiesceAndTeardownIgnoreCallerCancellationState()
    {
        var backend = new ScriptedRuntimeBackend();
        backend.BlockAuthorizationUpdate();
        var owner = CreateOwner(backend);

        var termination = owner.DisposeAsync().AsTask();
        await backend.AuthorizationUpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(backend.AuthorizationUpdateToken.CanBeCanceled);
        Assert.False(termination.IsCompleted);
        backend.ReleaseAuthorizationUpdate();
        await termination;
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task OwnerIsTheOnlySignalSinkAndRejectsSignalsAfterTerminationStarts()
    {
        var backend = new ScriptedRuntimeBackend();
        backend.BlockAuthorizationUpdate();
        var owner = CreateOwner(backend);
        var termination = owner.DisposeAsync().AsTask();
        await backend.AuthorizationUpdateStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => owner.UpdateSignalsAsync(NativeCapturePrivacySignals.FailClosed));
        Assert.Throws<ObjectDisposedException>(
            () => owner.InvalidatePrivacyObservation());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => owner.ApplyPrivacyInvalidationAsync(
                owner.PrivacyObservationGeneration));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => owner.TryUpdateSignalsAsync(
                owner.PrivacyObservationGeneration,
                NativeCapturePrivacySignals.FailClosed));

        backend.ReleaseAuthorizationUpdate();
        await termination;
    }

    [Fact]
    public async Task NativeAuthorizationFailureAutomaticallyTerminatesTheOwnedHandle()
    {
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new InvalidOperationException("authorization failed"),
        };
        var owner = CreateOwner(backend);
        var signals = new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.UpdateSignalsAsync(signals));
        await Assert.ThrowsAnyAsync<Exception>(() => owner.Termination);

        Assert.Contains("Revoke", backend.Operations);
        Assert.Contains("RequestStop", backend.Operations);
        Assert.Contains("WaitStopped:5000", backend.Operations);
        Assert.Contains("StopEventPump", backend.Operations);
        Assert.Equal("Destroy", backend.Operations[^2]);
        Assert.Equal("Complete", backend.Operations[^1]);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task PrivacyBarrierFailureAutomaticallyTerminatesTheOwnedHandle()
    {
        var backend = new ScriptedRuntimeBackend
        {
            UpdateFailure = new InvalidOperationException("privacy barrier failed"),
        };
        var owner = CreateOwner(backend);
        var generation = owner.InvalidatePrivacyObservation();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.ApplyPrivacyInvalidationAsync(generation));
        Assert.Equal("privacy barrier failed", failure.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.Termination);

        Assert.Contains("Revoke", backend.Operations);
        Assert.Contains("RequestStop", backend.Operations);
        Assert.Contains("WaitStopped:5000", backend.Operations);
        Assert.Contains("StopEventPump", backend.Operations);
        Assert.Equal("Destroy", backend.Operations[^2]);
        Assert.Equal("Complete", backend.Operations[^1]);
        Assert.Equal(1, backend.DestroyCount);
    }

    [Fact]
    public async Task CallerCancellationWithoutCoordinatorFaultDoesNotTerminateOwner()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = CreateOwner(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            owner.UpdateSignalsAsync(
                NativeCapturePrivacySignals.FailClosed,
                cancellation.Token));

        Assert.True(owner.Termination.IsCompletedSuccessfully);
        Assert.Equal(0, backend.DestroyCount);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task ForeignForgedOperationMismatchAndReplayStampsAreRejected()
    {
        var firstBackend = new ScriptedRuntimeBackend();
        var secondBackend = new ScriptedRuntimeBackend();
        var first = await CreateAuthorizedOwnerAsync(firstBackend);
        var second = await CreateAuthorizedOwnerAsync(secondBackend);
        try
        {
            var foreign = await first.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(foreign);
            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => second.StartAsync(foreign));
            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => first.StartAsync(new ForgedAdmissionStamp()));

            var wrongOperation = await first.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(wrongOperation);
            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => first.ResumeAsync(wrongOperation));
            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => first.StartAsync(wrongOperation));

            var valid = await first.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(valid);
            await first.StartAsync(valid);
            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => first.StartAsync(valid));

            Assert.Equal(1, firstBackend.StartCount);
            Assert.Equal(0, secondBackend.StartCount);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task TargetChangeInvalidatesOldStampWithoutRetrying()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = await CreateAuthorizedOwnerAsync(backend);
        try
        {
            var stale = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stale);

            await owner.UpdateSignalsAsync(CreateAllowedSignals(targetEpoch: 2));

            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => owner.StartAsync(stale));
            Assert.Equal(0, backend.StartCount);
            Assert.Equal(1, backend.IssueCount);

            var current = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(current);
            await owner.StartAsync(current);
            Assert.Equal(1, backend.StartCount);
            Assert.Equal(2, backend.IssueCount);
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationAfterAdmissionBeginsCannotProduceAmbiguousState()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = await CreateAuthorizedOwnerAsync(backend);
        backend.BlockNextStart();
        try
        {
            var stamp = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stamp);
            using var cancellation = new CancellationTokenSource();
            var start = owner.StartAsync(stamp, cancellation.Token);
            await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();
            Assert.False(backend.StartToken.CanBeCanceled);
            Assert.False(start.IsCompleted);

            backend.ReleaseStart();
            await start;
            Assert.Equal(1, backend.StartCount);
        }
        finally
        {
            backend.ReleaseStart();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationBeforeAdmissionConsumptionLeavesStampUsable()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = await CreateAuthorizedOwnerAsync(backend);
        try
        {
            var stamp = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stamp);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => owner.StartAsync(stamp, cancellation.Token));

            await owner.StartAsync(stamp);
            Assert.Equal(1, backend.StartCount);
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task SignalRevocationClosesManagedAdmissionBeforeWaitingForAnAdmittedCommand()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = await CreateAuthorizedOwnerAsync(backend);
        backend.BlockNextStart();
        try
        {
            var stamp = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stamp);
            var start = owner.StartAsync(stamp);
            await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));

            var revoke = owner.UpdateSignalsAsync(NativeCapturePrivacySignals.FailClosed);
            await Task.Yield();
            Assert.False(owner.IsCaptureAuthorized);
            Assert.False(revoke.IsCompleted);

            backend.ReleaseStart();
            await start;
            await revoke;
            Assert.False(owner.IsCaptureAuthorized);
            Assert.Equal(1, backend.StartCount);
        }
        finally
        {
            backend.ReleaseStart();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task TerminationWaitsForAnAdmittedCommandWithoutFaulting()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = await CreateAuthorizedOwnerAsync(backend);
        backend.BlockNextStart();
        try
        {
            var stamp = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stamp);
            var start = owner.StartAsync(stamp);
            await backend.StartStarted.WaitAsync(TimeSpan.FromSeconds(5));

            var termination = owner.DisposeAsync().AsTask();
            await Task.Yield();
            Assert.False(termination.IsCompleted);

            backend.ReleaseStart();
            await start;
            await termination;
            Assert.Equal(1, backend.StartCount);
            Assert.Equal(1, backend.DestroyCount);
        }
        finally
        {
            backend.ReleaseStart();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpectedAdmissionRejectionDoesNotTerminateOwner()
    {
        var backend = new ScriptedRuntimeBackend
        {
            RejectAdmissionIssue = true,
        };
        var owner = await CreateAuthorizedOwnerAsync(backend);
        try
        {
            var stamp = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);

            Assert.Null(stamp);
            Assert.True(owner.Termination.IsCompletedSuccessfully);
            Assert.Equal(0, backend.DestroyCount);
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task InternalAdmissionFailureQuarantinesAndTerminatesOwner()
    {
        var backend = new ScriptedRuntimeBackend
        {
            IssueFailure = new InvalidOperationException("issue failed"),
        };
        var owner = await CreateAuthorizedOwnerAsync(backend);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start).AsTask());
        Assert.Equal("issue failed", failure.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.Termination);

        Assert.Equal(1, backend.DestroyCount);
        Assert.Equal("Destroy", backend.Operations[^2]);
        Assert.Equal("Complete", backend.Operations[^1]);
    }

    [Fact]
    public async Task ExpectedStartRejectionDoesNotTerminateOwner()
    {
        var backend = new ScriptedRuntimeBackend
        {
            StartFailure = new CaptureRuntimeAdmissionRejectedException(),
        };
        var owner = await CreateAuthorizedOwnerAsync(backend);
        try
        {
            var stamp = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stamp);

            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => owner.StartAsync(stamp));
            Assert.True(owner.Termination.IsCompletedSuccessfully);
            Assert.Equal(0, backend.DestroyCount);
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task InternalStartFailureQuarantinesAndTerminatesOwner()
    {
        var backend = new ScriptedRuntimeBackend
        {
            StartFailure = new InvalidOperationException("start failed"),
        };
        var owner = await CreateAuthorizedOwnerAsync(backend);
        var stamp = await owner.TryIssueAdmissionAsync(
            CaptureAdmissionOperation.Start);
        Assert.NotNull(stamp);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.StartAsync(stamp));
        Assert.Equal("start failed", failure.Message);
        await Assert.ThrowsAnyAsync<Exception>(() => owner.Termination);

        Assert.Equal(1, backend.DestroyCount);
        Assert.Equal("Destroy", backend.Operations[^2]);
        Assert.Equal("Complete", backend.Operations[^1]);
    }

    [Fact]
    public async Task SuccessfulStopInvalidatesOldStampAndAllowsFreshAdmission()
    {
        var backend = new ScriptedRuntimeBackend();
        var owner = await CreateAuthorizedOwnerAsync(backend);
        try
        {
            var stale = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(stale);
            var previousGeneration = owner.InvalidationGeneration;

            await owner.StopAsync();

            Assert.True(owner.IsCaptureAuthorized);
            Assert.True(owner.InvalidationGeneration > previousGeneration);
            await Assert.ThrowsAsync<CaptureRuntimeAdmissionRejectedException>(
                () => owner.StartAsync(stale));

            var fresh = await owner.TryIssueAdmissionAsync(
                CaptureAdmissionOperation.Start);
            Assert.NotNull(fresh);
            await owner.StartAsync(fresh);
            Assert.Equal(1, backend.StopCount);
            Assert.Equal(1, backend.StartCount);
        }
        finally
        {
            await owner.DisposeAsync();
        }
    }

    private static NativeCaptureRuntimeOwner CreateOwner(
        ScriptedRuntimeBackend backend)
    {
        return new NativeCaptureRuntimeOwner(
            backend,
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1));
    }

    private static async Task<NativeCaptureRuntimeOwner> CreateAuthorizedOwnerAsync(
        ScriptedRuntimeBackend backend)
    {
        var enabled = CreateEnabledSettings();
        var owner = new NativeCaptureRuntimeOwner(
            backend,
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1),
            AppSettings.Default,
            CreateAllowedSignals(targetEpoch: 1));
        await owner.PrepareAsync(AppSettings.Default, enabled);
        await owner.CommittedAsync(AppSettings.Default, enabled);
        Assert.True(owner.IsCaptureAuthorized);
        return owner;
    }

    private static AppSettings CreateEnabledSettings()
    {
        var privacy = CapturePrivacySettings.Default;
        return new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion,
                new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
                privacy.Revision),
            privacy);
    }

    private static NativeCapturePrivacySignals CreateAllowedSignals(ulong targetEpoch)
    {
        return new NativeCapturePrivacySignals(
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCaptureConditionState.Inactive,
            NativeCaptureConditionState.Inactive,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            NativeCapturePolicyDecision.Allow,
            new NativeCaptureIdentitySnapshot(
                executableName: "notepad.exe",
                packageFamilyName: null,
                publisherCertificateSha256: null,
                windowTitle: "Untitled - Notepad"),
            Target: NativeCaptureTargetIdentity.Present(
                windowHandle: 0x1234 + targetEpoch,
                processId: checked((uint)(40 + targetEpoch)),
                processCreationTime100ns: 100 + targetEpoch,
                targetEpoch,
                displayMonitorHandle: 0x6000 + targetEpoch,
                displayDeviceKey: $@"\\.\DISPLAY{targetEpoch}"));
    }

    private sealed class ForgedAdmissionStamp : ICaptureRuntimeAdmissionStamp
    {
        public long InvalidationGeneration => 0;
    }

    private sealed class ScriptedRuntimeBackend : INativeCaptureRuntimeBackend
    {
        private readonly object _statusSync = new();
        private readonly TaskCompletionSource _authorizationUpdateStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAuthorizationUpdate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _blockAuthorizationUpdate;
        private bool _blockStart;
        private ulong _persistenceGeneration;
        private ulong _authorizationEpoch;
        private long _callbackInvalidationGeneration;
        private CaptureStatus _currentStatus = new(
            CaptureState.Unavailable,
            DateTimeOffset.UnixEpoch,
            "test",
            Reason: CaptureReasonCode.BackendUnavailable);
        private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;

        public NativeCaptureCapabilities AdvertisedCapabilities { get; init; } =
            NativeCaptureAbiContract.DisplayWideContinuousCapabilities;

        public NativeCaptureCapabilities Capabilities => AdvertisedCapabilities;

        public CaptureStatus CurrentStatus
        {
            get
            {
                lock (_statusSync)
                {
                    return _currentStatus;
                }
            }
        }

        public List<string> Operations { get; } = [];

        public Exception? UpdateFailure { get; init; }

        public Exception? RevokeFailure { get; init; }

        public Exception? RequestStopFailure { get; init; }

        public Exception? WaitFailure { get; init; }

        public Exception? PumpFailure { get; init; }

        public Exception? IssueFailure { get; init; }

        public Exception? StartFailure { get; init; }

        public bool RejectAdmissionIssue { get; init; }

        public NativeCaptureResult DestroyResult { get; init; } =
            NativeCaptureResult.Ok;

        public int DestroyCount { get; private set; }

        public int IssueCount { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task AuthorizationUpdateStarted =>
            _authorizationUpdateStarted.Task;

        public CancellationToken AuthorizationUpdateToken { get; private set; }

        public CancellationToken StartToken { get; private set; }

        public Task StartStarted => _startStarted.Task;

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add
            {
                lock (_statusSync)
                {
                    _statusChanged += value;
                }
            }
            remove
            {
                lock (_statusSync)
                {
                    _statusChanged -= value;
                }
            }
        }

        public event EventHandler<CaptureChunkCommittedEventArgs>? ChunkCommitted;

        public void RaiseChunkCommitted()
        {
            ChunkCommitted?.Invoke(
                this,
                CaptureChunkCommittedEventArgs.WakeHint);
        }

        public void RaiseStatus(CaptureState state)
        {
            CaptureStatus previous;
            CaptureStatus current;
            EventHandler<CaptureStatusChangedEventArgs>? handler;
            lock (_statusSync)
            {
                previous = _currentStatus;
                current = new CaptureStatus(
                    state,
                    DateTimeOffset.UtcNow,
                    "test",
                    previous.Sequence + 1,
                    state == CaptureState.Faulted
                        ? CaptureReasonCode.BackendFault
                        : CaptureReasonCode.None,
                    state == CaptureState.Faulted
                        ? CaptureErrorCode.NativeFailure
                        : CaptureErrorCode.None);
                _currentStatus = current;
                handler = _statusChanged;
            }

            handler?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(previous, current));
        }

        public Task<NativeCaptureCommandAdmissionV1?> TryIssueCommandAdmissionAsync(
            CaptureAdmissionOperation operation,
            ulong expectedRuntimePolicyRevision,
            ulong expectedPersistenceGeneration,
            ulong expectedTargetEpoch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IssueCount++;
            _ = operation;
            if (IssueFailure is { } issueFailure)
            {
                return Task.FromException<NativeCaptureCommandAdmissionV1?>(
                    issueFailure);
            }

            if (RejectAdmissionIssue)
            {
                return Task.FromResult<NativeCaptureCommandAdmissionV1?>(null);
            }

            var authorizationEpoch = ++_authorizationEpoch;
            return Task.FromResult<NativeCaptureCommandAdmissionV1?>(
                new NativeCaptureCommandAdmissionV1
                {
                    StructSize = NativeCaptureAbiContract.CommandAdmissionStructureSize,
                    AbiVersion = NativeCaptureAbiContract.AbiVersion,
                    InstanceEpoch = 1,
                    RuntimePolicyRevision = expectedRuntimePolicyRevision,
                    PersistenceGeneration = expectedPersistenceGeneration,
                    TargetEpoch = expectedTargetEpoch,
                    AuthorizationEpoch = authorizationEpoch,
                    NonceLow = authorizationEpoch,
                    NonceHigh = ~authorizationEpoch,
                });
        }

        public Task StartAuthorizedAsync(
            NativeCaptureCommandAdmissionV1 admission,
            CancellationToken cancellationToken = default)
        {
            _ = admission;
            StartToken = cancellationToken;
            StartCount++;
            if (StartFailure is { } startFailure)
            {
                return Task.FromException(startFailure);
            }

            if (!_blockStart)
            {
                return Task.CompletedTask;
            }

            _startStarted.TrySetResult();
            return _releaseStart.Task.WaitAsync(cancellationToken);
        }

        public Task PauseAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResumeAuthorizedAsync(
            NativeCaptureCommandAdmissionV1 admission,
            CancellationToken cancellationToken = default)
        {
            _ = admission;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.CompletedTask;
        }

        public long InvalidateRuntimeAuthorization()
        {
            Operations.Add("Invalidate");
            return Interlocked.Increment(
                ref _callbackInvalidationGeneration);
        }

        public async Task<NativeCaptureAuthorizationUpdateResult>
            UpdateRuntimeAuthorizationAsync(
            NativeCaptureRuntimeAuthorization authorization,
            long expectedCallbackInvalidationGeneration,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("Block");
            AuthorizationUpdateToken = cancellationToken;
            if (_blockAuthorizationUpdate)
            {
                _authorizationUpdateStarted.TrySetResult();
                await _releaseAuthorizationUpdate.Task.WaitAsync(cancellationToken);
            }

            if (UpdateFailure is { } failure)
            {
                throw failure;
            }

            if (expectedCallbackInvalidationGeneration
                != Volatile.Read(ref _callbackInvalidationGeneration))
            {
                return new NativeCaptureAuthorizationUpdateResult(
                    _persistenceGeneration,
                    NativeCaptureAuthorizationUpdateOutcome
                        .SupersededBeforeCommit);
            }

            return new NativeCaptureAuthorizationUpdateResult(
                ++_persistenceGeneration,
                NativeCaptureAuthorizationUpdateOutcome.Applied);
        }

        public Task<ulong> RevokeRuntimeAuthorizationAsync(
            CancellationToken cancellationToken = default)
        {
            Operations.Add("Revoke");
            cancellationToken.ThrowIfCancellationRequested();
            if (RevokeFailure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(_persistenceGeneration == 0
                ? ++_persistenceGeneration
                : _persistenceGeneration);
        }

        public Task RequestStopForShutdownAsync()
        {
            Operations.Add("RequestStop");
            return RequestStopFailure is null
                ? Task.CompletedTask
                : Task.FromException(RequestStopFailure);
        }

        public Task WaitStoppedForShutdownAsync(uint timeoutMilliseconds)
        {
            Operations.Add($"WaitStopped:{timeoutMilliseconds}");
            return WaitFailure is null
                ? Task.CompletedTask
                : Task.FromException(WaitFailure);
        }

        public Task StopEventPumpAsync()
        {
            Operations.Add("StopEventPump");
            return PumpFailure is null
                ? Task.CompletedTask
                : Task.FromException(PumpFailure);
        }

        public NativeCaptureResult DestroyForShutdown()
        {
            Operations.Add("Destroy");
            DestroyCount++;
            return DestroyResult;
        }

        public void CompleteOwnedShutdown()
        {
            Operations.Add("Complete");
        }

        public void DisposeSafelyAfterConstructionFailure()
        {
            Operations.Add("ConstructionFailureDispose");
        }

        public void BlockAuthorizationUpdate()
        {
            _blockAuthorizationUpdate = true;
        }

        public void ReleaseAuthorizationUpdate()
        {
            _releaseAuthorizationUpdate.TrySetResult();
        }

        public void BlockNextStart()
        {
            _blockStart = true;
        }

        public void ReleaseStart()
        {
            _releaseStart.TrySetResult();
        }
    }
}
