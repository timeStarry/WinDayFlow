using System.Reflection;
using WinDayFlow.Application.Capture;
using Xunit;

namespace WinDayFlow.Application.Tests.Capture;

public sealed class CaptureChunkCommittedEventArgsTests
{
    [Fact]
    public void ApplicationNotificationIsDeliberatelyOnlyAWakeHint()
    {
        var publicInstanceProperties = typeof(CaptureChunkCommittedEventArgs)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(publicInstanceProperties);
        Assert.Empty(typeof(CaptureChunkCommittedEventArgs).GetConstructors());
        Assert.Same(
            CaptureChunkCommittedEventArgs.WakeHint,
            CaptureChunkCommittedEventArgs.WakeHint);
    }
}
