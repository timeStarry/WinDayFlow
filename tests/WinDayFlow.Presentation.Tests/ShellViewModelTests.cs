using WinDayFlow.Application.Capture;
using WinDayFlow.Presentation.Capture;
using WinDayFlow.Presentation.Shell;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void ShellExposesStableLabelsAndSharedCaptureStatus()
    {
        using var captureStatus = new CaptureStatusViewModel(new UnavailableCaptureServiceStub());
        var shell = new ShellViewModel(captureStatus);

        Assert.Equal("WinDayFlow", shell.ApplicationTitle);
        Assert.Equal("时间线", shell.TimelineTitle);
        Assert.Equal("今天", shell.TodayTitle);
        Assert.Equal("洞察", shell.InsightsTitle);
        Assert.Equal("系统", shell.SystemTitle);
        Assert.Same(captureStatus, shell.CaptureStatus);
    }

    private sealed class UnavailableCaptureServiceStub : ICaptureService
    {
        public CaptureStatus CurrentStatus { get; } = new(
            CaptureState.Unavailable,
            new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

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
    }
}
