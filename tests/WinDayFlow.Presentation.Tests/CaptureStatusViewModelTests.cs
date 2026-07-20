using WinDayFlow.Application.Capture;
using WinDayFlow.Presentation.Capture;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class CaptureStatusViewModelTests
{
    [Fact]
    public void UnavailableStatusDisablesEveryCaptureAction()
    {
        var service = new StubCaptureService(CaptureState.Unavailable);
        using var viewModel = new CaptureStatusViewModel(service);

        Assert.Equal(CaptureState.Unavailable, viewModel.State);
        Assert.Equal("录制不可用", viewModel.StatusText);
        Assert.Equal("原生录制组件尚未接入。", viewModel.DetailText);
        Assert.False(viewModel.IsCaptureAvailable);
        Assert.False(viewModel.IsOperational);
        Assert.False(viewModel.StartCaptureCommand.CanExecute(null));
        Assert.False(viewModel.PauseCaptureCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCaptureCommand.CanExecute(null));
        Assert.False(viewModel.StopCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void StatusChangeUpdatesTextAndCapabilities()
    {
        var service = new StubCaptureService(CaptureState.Stopped);
        using var viewModel = new CaptureStatusViewModel(service);

        service.TransitionTo(CaptureState.Recording, "正在记录主显示器");

        Assert.Equal(CaptureState.Recording, viewModel.State);
        Assert.Equal("正在录制", viewModel.StatusText);
        Assert.Equal("正在记录主显示器", viewModel.DetailText);
        Assert.True(viewModel.IsCaptureAvailable);
        Assert.True(viewModel.IsOperational);
        Assert.True(viewModel.IsRecording);
        Assert.False(viewModel.StartCaptureCommand.CanExecute(null));
        Assert.True(viewModel.PauseCaptureCommand.CanExecute(null));
        Assert.True(viewModel.StopCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void ConsentBlockedStatusKeepsSafetyActionsAvailable()
    {
        var service = new StubCaptureService(CaptureState.BlockedByConsent);
        using var viewModel = new CaptureStatusViewModel(service);

        Assert.Equal("需要录制授权", viewModel.StatusText);
        Assert.Equal("请先在设置中确认录制授权。", viewModel.DetailText);
        Assert.True(viewModel.IsCaptureAvailable);
        Assert.False(viewModel.IsOperational);
        Assert.False(viewModel.StartCaptureCommand.CanExecute(null));
        Assert.True(viewModel.PauseCaptureCommand.CanExecute(null));
        Assert.False(viewModel.ResumeCaptureCommand.CanExecute(null));
        Assert.True(viewModel.StopCaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task CaptureCommandRefreshesSnapshotWhenServiceDoesNotRaiseAnEvent()
    {
        var service = new StubCaptureService(CaptureState.Stopped)
        {
            RaiseEventsForCommands = false,
        };
        using var viewModel = new CaptureStatusViewModel(service);

        await viewModel.StartCaptureCommand.ExecuteAsync(null);

        Assert.Equal(1, service.StartCount);
        Assert.Equal(CaptureState.Recording, viewModel.State);
        Assert.True(viewModel.PauseCaptureCommand.CanExecute(null));
    }

    private sealed class StubCaptureService : ICaptureService
    {
        public StubCaptureService(CaptureState state)
        {
            CurrentStatus = CreateStatus(state);
        }

        public CaptureStatus CurrentStatus { get; private set; }

        public bool RaiseEventsForCommands { get; init; } = true;

        public int StartCount { get; private set; }

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            TransitionTo(CaptureState.Recording, raiseEvent: RaiseEventsForCommands);
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Paused, raiseEvent: RaiseEventsForCommands);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Recording, raiseEvent: RaiseEventsForCommands);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitionTo(CaptureState.Stopped, raiseEvent: RaiseEventsForCommands);
            return Task.CompletedTask;
        }

        public void TransitionTo(CaptureState state, string? detail = null)
        {
            TransitionTo(state, detail, raiseEvent: true);
        }

        private void TransitionTo(
            CaptureState state,
            string? detail = null,
            bool raiseEvent = true)
        {
            var previous = CurrentStatus;
            CurrentStatus = CreateStatus(state, detail);
            if (raiseEvent)
            {
                StatusChanged?.Invoke(
                    this,
                    new CaptureStatusChangedEventArgs(previous, CurrentStatus));
            }
        }

        private static CaptureStatus CreateStatus(CaptureState state, string? detail = null)
        {
            return new CaptureStatus(
                state,
                new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
                detail);
        }
    }
}
