using WinDayFlow.App.Services;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class CaptureAwareWindowCloseCoordinatorTests
{
    [Fact]
    public async Task CloseWaitsForCaptureStopAndAllowsOnlyTheRequestedRetry()
    {
        var operations = new List<string>();
        var stopStarted = NewCompletionSource();
        var releaseStop = NewCompletionSource();
        var releaseShutdown = NewCompletionSource();
        var shutdownStarted = NewCompletionSource();
        var closeRequested = NewCompletionSource();
        var stopCalls = 0;
        var shutdownCalls = 0;
        bool? retryWasCanceled = null;
        CaptureAwareWindowCloseCoordinator? coordinator = null;
        coordinator = new CaptureAwareWindowCloseCoordinator(
            async cancellationToken =>
            {
                Interlocked.Increment(ref stopCalls);
                operations.Add("stop-started");
                stopStarted.TrySetResult();
                await releaseStop.Task.WaitAsync(cancellationToken);
                operations.Add("stop-completed");
            },
            async () =>
            {
                Interlocked.Increment(ref shutdownCalls);
                operations.Add("shutdown-started");
                shutdownStarted.TrySetResult();
                await releaseShutdown.Task;
                operations.Add("shutdown-completed");
            },
            () =>
            {
                operations.Add("close-requested");
                retryWasCanceled = coordinator!.ShouldCancelClose();
                closeRequested.TrySetResult();
            },
            exception => throw new Xunit.Sdk.XunitException(exception.ToString()));

        Assert.True(coordinator.ShouldCancelClose());
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.ShouldCancelClose());
        Assert.Equal(1, Volatile.Read(ref stopCalls));
        Assert.Equal(["stop-started"], operations);

        releaseStop.TrySetResult();
        await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(coordinator.ShouldCancelClose());
        Assert.False(closeRequested.Task.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref shutdownCalls));

        releaseShutdown.TrySetResult();
        await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(retryWasCanceled);
        Assert.Equal(
            [
                "stop-started",
                "stop-completed",
                "shutdown-started",
                "shutdown-completed",
                "close-requested",
            ],
            operations);
    }

    [Fact]
    public async Task StopFailureIsReportedBeforeCloseAndDoesNotTrapTheWindow()
    {
        var failure = new InvalidOperationException("stop failed");
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = NewCompletionSource();
        var closeRequested = NewCompletionSource();
        var stopCalls = 0;
        bool? retryWasCanceled = null;
        CaptureAwareWindowCloseCoordinator? coordinator = null;
        coordinator = new CaptureAwareWindowCloseCoordinator(
            async _ =>
            {
                Interlocked.Increment(ref stopCalls);
                await releaseFailure.Task;
                throw failure;
            },
            () => Task.CompletedTask,
            () =>
            {
                retryWasCanceled = coordinator!.ShouldCancelClose();
                closeRequested.TrySetResult();
            },
            exception => reported.TrySetResult(exception));

        Assert.True(coordinator.ShouldCancelClose());
        Assert.True(coordinator.ShouldCancelClose());
        releaseFailure.TrySetResult();
        var actualFailure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(failure, actualFailure);
        Assert.False(retryWasCanceled);
        Assert.Equal(1, Volatile.Read(ref stopCalls));
    }

    [Fact]
    public async Task StopTimeoutCancelsStopAndAllowsTheWindowToClose()
    {
        var timeoutElapsed = NewCompletionSource();
        var stopStarted = NewCompletionSource();
        var closeRequested = NewCompletionSource();
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCompletion = NewCompletionSource();
        CancellationToken stopCancellation = default;
        bool? retryWasCanceled = null;
        CaptureAwareWindowCloseCoordinator? coordinator = null;
        coordinator = new CaptureAwareWindowCloseCoordinator(
            cancellationToken =>
            {
                stopCancellation = cancellationToken;
                cancellationToken.Register(
                    () => stopCompletion.TrySetCanceled(cancellationToken));
                stopStarted.TrySetResult();
                return stopCompletion.Task;
            },
            () => Task.CompletedTask,
            () =>
            {
                retryWasCanceled = coordinator!.ShouldCancelClose();
                closeRequested.TrySetResult();
            },
            exception => reported.TrySetResult(exception),
            TimeSpan.FromSeconds(15),
            (_, _) => timeoutElapsed.Task);

        Assert.True(coordinator.ShouldCancelClose());
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopCancellation.IsCancellationRequested);

        timeoutElapsed.TrySetResult();
        var actualFailure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<TimeoutException>(actualFailure);
        Assert.True(stopCancellation.IsCancellationRequested);
        Assert.False(retryWasCanceled);
    }

    [Fact]
    public async Task SynchronouslyBlockingStopStillTimesOutAndAllowsClose()
    {
        using var releaseStop = new ManualResetEventSlim();
        var stopStarted = NewCompletionSource();
        var stopExited = NewCompletionSource();
        var timeoutElapsed = NewCompletionSource();
        var closeRequested = NewCompletionSource();
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken stopCancellation = default;
        bool? retryWasCanceled = null;
        CaptureAwareWindowCloseCoordinator? coordinator = null;
        coordinator = new CaptureAwareWindowCloseCoordinator(
            cancellationToken =>
            {
                stopCancellation = cancellationToken;
                stopStarted.TrySetResult();
                try
                {
                    releaseStop.Wait(CancellationToken.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }
                finally
                {
                    stopExited.TrySetResult();
                }
            },
            () => Task.CompletedTask,
            () =>
            {
                retryWasCanceled = coordinator!.ShouldCancelClose();
                closeRequested.TrySetResult();
            },
            exception => reported.TrySetResult(exception),
            TimeSpan.FromSeconds(15),
            (_, _) => timeoutElapsed.Task);

        try
        {
            Assert.True(coordinator.ShouldCancelClose());
            await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closeRequested.Task.IsCompleted);

            timeoutElapsed.TrySetResult();
            var actualFailure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsType<TimeoutException>(actualFailure);
            Assert.True(stopCancellation.IsCancellationRequested);
            Assert.False(retryWasCanceled);
        }
        finally
        {
            releaseStop.Set();
            await stopExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ShutdownFailureIsReportedUnchangedBeforeClose()
    {
        var failure = new InvalidOperationException("wait_stopped failed");
        var reported = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeRequested = NewCompletionSource();
        bool? retryWasCanceled = null;
        CaptureAwareWindowCloseCoordinator? coordinator = null;
        coordinator = new CaptureAwareWindowCloseCoordinator(
            _ => Task.CompletedTask,
            () => Task.FromException(failure),
            () =>
            {
                retryWasCanceled = coordinator!.ShouldCancelClose();
                closeRequested.TrySetResult();
            },
            exception => reported.TrySetResult(exception));

        Assert.True(coordinator.ShouldCancelClose());
        var actualFailure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await closeRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(failure, actualFailure);
        Assert.False(retryWasCanceled);
    }

    private static TaskCompletionSource NewCompletionSource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
