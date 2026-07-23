#if WDF_DEV_LIVE_CAPTURE
using System.Runtime.InteropServices;
using WinDayFlow.App.Services;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class DevLiveCaptureHostedServiceTests
{
    [Fact]
    public void DevNativeBinaryAdvertisesLiveWriterButNotExtractionCapability()
    {
        if (!OperatingSystem.IsWindows()
            || !Environment.Is64BitProcess
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var probe = NativeCaptureBackend.Probe();

        Assert.True(probe.LibraryLoaded, probe.Failure);
        Assert.True(probe.AbiCompatible, probe.Failure);
        Assert.Equal(
            NativeCaptureAbiContract.RuntimeOwnerCapabilities,
            probe.Capabilities & NativeCaptureAbiContract.RuntimeOwnerCapabilities);
        Assert.True(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.ScreenCapture));
        Assert.True(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.H264Chunks));
        Assert.False(probe.Capabilities.HasFlag(
            NativeCaptureCapabilities.EvidenceExtraction));
    }

    [Fact]
    public async Task StopDisposesMonitorBeforeOwnerExactlyOnce()
    {
        var operations = new List<string>();
        var lifetime = CreateLifetime(
            start: _ =>
            {
                operations.Add("start");
                return Task.CompletedTask;
            },
            disposeMonitor: () =>
            {
                operations.Add("monitor");
                return ValueTask.CompletedTask;
            },
            disposeOwner: () =>
            {
                operations.Add("owner");
                return ValueTask.CompletedTask;
            });

        await lifetime.StartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
        await lifetime.DisposeAsync();

        Assert.Equal(["start", "monitor", "owner"], operations);
    }

    [Fact]
    public async Task StartFailureStillDisposesMonitorBeforeOwner()
    {
        var failure = new InvalidOperationException("start failed");
        var operations = new List<string>();
        var lifetime = CreateLifetime(
            start: _ => Task.FromException(failure),
            disposeMonitor: () =>
            {
                operations.Add("monitor");
                return ValueTask.CompletedTask;
            },
            disposeOwner: () =>
            {
                operations.Add("owner");
                return ValueTask.CompletedTask;
            });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.StartAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(["monitor", "owner"], operations);
        await lifetime.DisposeAsync();
        Assert.Equal(["monitor", "owner"], operations);
    }

    [Fact]
    public async Task MonitorDisposeFailureDoesNotSkipOwnerDispose()
    {
        var failure = new IOException("monitor failed");
        var ownerDisposeCount = 0;
        var lifetime = CreateLifetime(
            start: _ => Task.CompletedTask,
            disposeMonitor: () => ValueTask.FromException(failure),
            disposeOwner: () =>
            {
                Interlocked.Increment(ref ownerDisposeCount);
                return ValueTask.CompletedTask;
            });

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => lifetime.StopAsync(CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Equal(1, ownerDisposeCount);
        await Assert.ThrowsAsync<IOException>(
            () => lifetime.DisposeAsync().AsTask());
        Assert.Equal(1, ownerDisposeCount);
    }

    [Fact]
    public async Task CanceledStopStillStartsSafetyShutdown()
    {
        var releaseMonitor = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerDisposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = CreateLifetime(
            start: _ => Task.CompletedTask,
            disposeMonitor: async () => await releaseMonitor.Task,
            disposeOwner: () =>
            {
                ownerDisposed.TrySetResult();
                return ValueTask.CompletedTask;
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifetime.StopAsync(cancellation.Token));
        releaseMonitor.TrySetResult();
        await ownerDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lifetime.DisposeAsync();
    }

    private static DevLiveCaptureHostedService CreateLifetime(
        Func<CancellationToken, Task> start,
        Func<ValueTask> disposeMonitor,
        Func<ValueTask> disposeOwner) => new(
            start,
            disposeMonitor,
            disposeOwner);
}
#endif
