using WinDayFlow.Application.Capture;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class UnavailableCaptureBackendTests
{
    [Fact]
    public void CurrentStatusIsDeterministicallyUnavailable()
    {
        var service = new UnavailableCaptureBackend();

        Assert.Equal(CaptureState.Unavailable, service.CurrentStatus.State);
        Assert.Equal(DateTimeOffset.UnixEpoch, service.CurrentStatus.ChangedAt);
        Assert.Equal("当前开发版本尚未接入原生录制组件。", service.CurrentStatus.Detail);
        Assert.False(service.CurrentStatus.IsOperational);
        Assert.IsAssignableFrom<ICaptureChunkCommitNotifier>(service);
    }

    [Fact]
    public async Task LifecycleCommandsAreConsistentlyNotSupported()
    {
        var service = new UnavailableCaptureBackend();
        var admissionStamp = new TestAdmissionStamp();
        var commands = new Func<Task>[]
        {
            () => service.StartAsync(admissionStamp),
            () => service.PauseAsync(),
            () => service.ResumeAsync(admissionStamp),
            () => service.StopAsync(),
        };

        foreach (var command in commands)
        {
            var error = await Assert.ThrowsAsync<NotSupportedException>(command);
            Assert.Equal(
                "当前开发版本尚未接入原生录制组件。",
                error.Message);
            Assert.Equal(CaptureState.Unavailable, service.CurrentStatus.State);
        }
    }

    [Fact]
    public async Task FailedCommandDoesNotRaiseAStatusChange()
    {
        var service = new UnavailableCaptureBackend();
        var eventCount = 0;
        service.StatusChanged += (_, _) => eventCount++;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.StartAsync(new TestAdmissionStamp()));

        Assert.Equal(0, eventCount);
    }

    private sealed class TestAdmissionStamp : ICaptureRuntimeAdmissionStamp
    {
        public long InvalidationGeneration => 0;
    }
}
