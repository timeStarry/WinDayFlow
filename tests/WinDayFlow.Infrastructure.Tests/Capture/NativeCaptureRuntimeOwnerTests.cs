using WinDayFlow.Application.Capture;
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

    private static NativeCaptureRuntimeOwner CreateOwner(
        ScriptedRuntimeBackend backend)
    {
        return new NativeCaptureRuntimeOwner(
            backend,
            NativeCapturePrivacyContext.FailClosed(runtimePolicyRevision: 1));
    }

    private sealed class ScriptedRuntimeBackend : INativeCaptureRuntimeBackend
    {
        private readonly TaskCompletionSource _authorizationUpdateStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseAuthorizationUpdate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _blockAuthorizationUpdate;
        private ulong _persistenceGeneration;

        public NativeCaptureCapabilities Capabilities =>
            NativeCaptureAbiContract.RuntimeSafetyCapabilities;

        public CaptureStatus CurrentStatus { get; } = new(
            CaptureState.Unavailable,
            DateTimeOffset.UnixEpoch,
            "test",
            Reason: CaptureReasonCode.BackendUnavailable);

        public List<string> Operations { get; } = [];

        public Exception? UpdateFailure { get; init; }

        public Exception? RevokeFailure { get; init; }

        public Exception? RequestStopFailure { get; init; }

        public Exception? WaitFailure { get; init; }

        public Exception? PumpFailure { get; init; }

        public NativeCaptureResult DestroyResult { get; init; } =
            NativeCaptureResult.Ok;

        public int DestroyCount { get; private set; }

        public Task AuthorizationUpdateStarted =>
            _authorizationUpdateStarted.Task;

        public CancellationToken AuthorizationUpdateToken { get; private set; }

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<ulong> UpdateRuntimeAuthorizationAsync(
            NativeCaptureRuntimeAuthorization authorization,
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

            return ++_persistenceGeneration;
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
    }
}
