using System.Collections.Concurrent;
using WinDayFlow.Application.Capture;
using WinDayFlow.Presentation.Capture;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class CaptureStatusViewModelTests
{
    public static TheoryData<CaptureState, CaptureReasonCode, CaptureDisplayState, string>
        DisplayStates => new()
        {
            { CaptureState.Starting, CaptureReasonCode.None, CaptureDisplayState.Recording, "正在录制" },
            { CaptureState.Recording, CaptureReasonCode.None, CaptureDisplayState.Recording, "正在录制" },
            { CaptureState.Resuming, CaptureReasonCode.None, CaptureDisplayState.Recording, "正在录制" },
            { CaptureState.Pausing, CaptureReasonCode.UserPaused, CaptureDisplayState.Paused, "录制已暂停" },
            { CaptureState.Paused, CaptureReasonCode.UserPaused, CaptureDisplayState.Paused, "录制已暂停" },
            { CaptureState.Stopping, CaptureReasonCode.None, CaptureDisplayState.Stopped, "录制已停止" },
            { CaptureState.Stopped, CaptureReasonCode.None, CaptureDisplayState.Stopped, "录制已停止" },
            { CaptureState.Unavailable, CaptureReasonCode.None, CaptureDisplayState.NeedsAttention, "录制需要处理" },
            { CaptureState.BlockedByConsent, CaptureReasonCode.ConsentRequired, CaptureDisplayState.NeedsAttention, "录制需要处理" },
            { CaptureState.NeedsAttention, CaptureReasonCode.StorageConstrained, CaptureDisplayState.NeedsAttention, "录制需要处理" },
        };

    [Theory]
    [MemberData(nameof(DisplayStates))]
    public void ProjectsInternalStatesToFourDisplayStates(
        CaptureState state,
        CaptureReasonCode reason,
        CaptureDisplayState expectedDisplayState,
        string expectedStatusText)
    {
        var service = new StubCaptureService(state, reason);
        using var viewModel = new CaptureStatusViewModel(service);

        Assert.Equal(expectedDisplayState, viewModel.DisplayState);
        Assert.Equal(expectedStatusText, viewModel.StatusText);
    }

    [Theory]
    [InlineData(CaptureReasonCode.ExcludedApplication)]
    [InlineData(CaptureReasonCode.ExcludedWindow)]
    [InlineData(CaptureReasonCode.RemoteSession)]
    [InlineData(CaptureReasonCode.PresentationMode)]
    [InlineData(CaptureReasonCode.PolicyBlocked)]
    public void LegacyPrivacyReasonsNeverPresentAsPrivacyProtection(
        CaptureReasonCode reason)
    {
        var service = new StubCaptureService(CaptureState.Paused, reason);
        using var viewModel = new CaptureStatusViewModel(service);

        Assert.Equal(CaptureDisplayState.NeedsAttention, viewModel.DisplayState);
        Assert.Equal("录制需要处理", viewModel.StatusText);

        Assert.DoesNotContain("隐私", viewModel.DetailText);
        Assert.False(viewModel.ResumeCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void StatusChangeUpdatesTextAndCapabilities()
    {
        var service = new StubCaptureService(CaptureState.Stopped);
        using var viewModel = new CaptureStatusViewModel(service);

        service.TransitionTo(CaptureState.Recording, "正在记录主显示器");

        Assert.Equal(CaptureDisplayState.Recording, viewModel.DisplayState);
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
    public void UserPauseKeepsManualResumeAvailable()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        using var viewModel = new CaptureStatusViewModel(service);

        service.TransitionTo(CaptureState.Paused, reason: CaptureReasonCode.UserPaused);

        Assert.Equal(CaptureDisplayState.Paused, viewModel.DisplayState);
        Assert.Equal("录制已暂停", viewModel.StatusText);
        Assert.True(viewModel.ResumeCaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task CaptureCommandRefreshesSnapshotWhenServiceDoesNotRaiseEvent()
    {
        var service = new StubCaptureService(CaptureState.Stopped)
        {
            RaiseEventsForCommands = false,
        };
        using var viewModel = new CaptureStatusViewModel(service);

        await viewModel.StartCaptureCommand.ExecuteAsync(null);

        Assert.Equal(1, service.StartCount);
        Assert.Equal(CaptureDisplayState.Recording, viewModel.DisplayState);
        Assert.True(viewModel.PauseCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void ConstructorResnapshotsStatusAfterSubscribing()
    {
        var service = new StubCaptureService(CaptureState.Stopped)
        {
            TransitionToRecordingWhenSubscriberIsAdded = true,
        };

        using var viewModel = new CaptureStatusViewModel(service);

        Assert.Equal(CaptureDisplayState.Recording, viewModel.DisplayState);
    }

    [Fact]
    public void StaleNotificationUsesServiceCurrentSnapshot()
    {
        var service = new StubCaptureService(CaptureState.Stopped);
        using var viewModel = new CaptureStatusViewModel(service);
        service.TransitionTo(CaptureState.Recording);

        service.PublishStaleStatus(CaptureState.Stopped);

        Assert.Equal(CaptureDisplayState.Recording, viewModel.DisplayState);
    }

    [Fact]
    public async Task BackgroundNotificationIsMarshaledToCapturedContext()
    {
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var service = new StubCaptureService(CaptureState.Stopped);
        CaptureStatusViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = new CaptureStatusViewModel(service);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (viewModel)
        {
            await Task.Run(() => service.TransitionTo(CaptureState.Recording));
            Assert.Equal(CaptureDisplayState.Stopped, viewModel.DisplayState);

            context.RunAllPostedCallbacks();

            Assert.Equal(CaptureDisplayState.Recording, viewModel.DisplayState);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)>
            _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
        }

        public void RunAllPostedCallbacks()
        {
            while (_callbacks.TryDequeue(out var callback))
            {
                callback.Callback(callback.State);
            }
        }
    }

    private sealed class StubCaptureService : ICaptureService
    {
        private EventHandler<CaptureStatusChangedEventArgs>? _statusChanged;
        private bool _subscriberTransitionApplied;
        private ulong _sequence;

        public StubCaptureService(
            CaptureState state,
            CaptureReasonCode reason = CaptureReasonCode.None)
        {
            CurrentStatus = CreateStatus(state, reason: reason);
        }

        public CaptureStatus CurrentStatus { get; private set; }

        public bool RaiseEventsForCommands { get; init; } = true;

        public bool TransitionToRecordingWhenSubscriberIsAdded { get; init; }

        public int StartCount { get; private set; }

        public event EventHandler<CaptureStatusChangedEventArgs>? StatusChanged
        {
            add
            {
                if (TransitionToRecordingWhenSubscriberIsAdded
                    && !_subscriberTransitionApplied)
                {
                    _subscriberTransitionApplied = true;
                    CurrentStatus = CreateStatus(CaptureState.Recording);
                }

                _statusChanged += value;
            }
            remove => _statusChanged -= value;
        }

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
            TransitionTo(
                CaptureState.Paused,
                reason: CaptureReasonCode.UserPaused,
                raiseEvent: RaiseEventsForCommands);
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

        public void TransitionTo(
            CaptureState state,
            string? detail = null,
            CaptureReasonCode reason = CaptureReasonCode.None,
            bool raiseEvent = true)
        {
            var previous = CurrentStatus;
            CurrentStatus = CreateStatus(state, detail, reason);
            if (raiseEvent)
            {
                _statusChanged?.Invoke(
                    this,
                    new CaptureStatusChangedEventArgs(previous, CurrentStatus));
            }
        }

        public void PublishStaleStatus(CaptureState state)
        {
            var stalePrevious = new CaptureStatus(
                CaptureState.Starting,
                DateTimeOffset.UtcNow,
                Sequence: 0);
            var staleCurrent = new CaptureStatus(
                state,
                DateTimeOffset.UtcNow,
                Sequence: 0);
            _statusChanged?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(stalePrevious, staleCurrent));
        }

        private CaptureStatus CreateStatus(
            CaptureState state,
            string? detail = null,
            CaptureReasonCode reason = CaptureReasonCode.None)
        {
            _sequence++;
            return new CaptureStatus(
                state,
                new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero),
                detail,
                _sequence,
                reason,
                state == CaptureState.Faulted
                    ? CaptureErrorCode.NativeFailure
                    : CaptureErrorCode.None);
        }
    }
}
