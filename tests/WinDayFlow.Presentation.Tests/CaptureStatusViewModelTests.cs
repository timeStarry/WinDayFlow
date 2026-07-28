using System.Collections.Concurrent;
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

    [Theory]
    [InlineData(CaptureReasonCode.ExcludedApplication, "当前内容已排除")]
    [InlineData(CaptureReasonCode.ExcludedWindow, "当前内容已排除")]
    [InlineData(CaptureReasonCode.StorageConstrained, "存储空间不足")]
    public void ExplicitProtectionPauseIsShownImmediately(
        CaptureReasonCode reason,
        string expectedStatusText)
    {
        var service = new StubCaptureService(CaptureState.Recording);
        using var viewModel = new CaptureStatusViewModel(service);

        service.TransitionTo(CaptureState.Paused, reason: reason);

        Assert.Equal(expectedStatusText, viewModel.StatusText);
        Assert.True(viewModel.IsPrivacyProtected);
        Assert.False(viewModel.ResumeCaptureCommand.CanExecute(null));
        Assert.True(viewModel.StopCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void ShortAutomaticTargetRebindKeepsRecordingStatusVisible()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        using var viewModel = new CaptureStatusViewModel(service, delay.WaitAsync);

        service.TransitionTo(
            CaptureState.Pausing,
            reason: CaptureReasonCode.PolicyBlocked);
        AssertRecordingPresentation(viewModel);

        service.TransitionTo(
            CaptureState.Paused,
            reason: CaptureReasonCode.PolicyBlocked);
        AssertRecordingPresentation(viewModel);

        service.TransitionTo(CaptureState.Resuming);
        AssertRecordingPresentation(viewModel);

        service.TransitionTo(CaptureState.Recording);
        AssertRecordingPresentation(viewModel);
        Assert.True(delay.IsCanceled);

        delay.Release();

        AssertRecordingPresentation(viewModel);
    }

    [Fact]
    public void QueuedAutomaticTargetRebindDoesNotPublishResumeStatus()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        CaptureStatusViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = new CaptureStatusViewModel(service, delay.WaitAsync);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            var publishedStatusTexts = ObserveStatusTexts(viewModel);

            service.TransitionTo(
                CaptureState.Pausing,
                reason: CaptureReasonCode.PolicyBlocked);
            service.TransitionTo(
                CaptureState.Paused,
                reason: CaptureReasonCode.PolicyBlocked);
            service.TransitionTo(CaptureState.Resuming);

            AssertRecordingPresentation(viewModel);

            context.RunAllPostedCallbacks();

            AssertRecordingPresentation(viewModel);
            Assert.DoesNotContain("正在恢复录制", publishedStatusTexts);
        }
    }

    [Fact]
    public void QueuedManualPauseAndResumeTransitionsRemainVisible()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        CaptureStatusViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = new CaptureStatusViewModel(service, delay.WaitAsync);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            var publishedStatusTexts = ObserveStatusTexts(viewModel);

            service.TransitionTo(CaptureState.Pausing);
            service.TransitionTo(
                CaptureState.Paused,
                reason: CaptureReasonCode.UserPaused);
            service.TransitionTo(CaptureState.Resuming);

            context.RunAllPostedCallbacks();

            Assert.Equal(
                ["正在暂停录制", "录制已暂停", "正在恢复录制"],
                publishedStatusTexts);
            Assert.Equal(CaptureState.Resuming, viewModel.State);
            Assert.Null(delay.RequestedDelay);
        }
    }

    [Fact]
    public async Task PersistentPolicyBlockIsShownAfterCoalescingWindow()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        using var viewModel = new CaptureStatusViewModel(service, delay.WaitAsync);
        var protectionShown = ObserveStateAsync(viewModel, CaptureState.Paused);

        service.TransitionTo(
            CaptureState.Pausing,
            reason: CaptureReasonCode.PolicyBlocked);
        service.TransitionTo(
            CaptureState.Paused,
            reason: CaptureReasonCode.PolicyBlocked);

        Assert.Equal(TimeSpan.FromMilliseconds(750), delay.RequestedDelay);
        AssertRecordingPresentation(viewModel);

        delay.Release();
        await protectionShown;

        Assert.Equal(CaptureState.Paused, viewModel.State);
        Assert.Equal(CaptureReasonCode.PolicyBlocked, viewModel.Reason);
        Assert.Equal("隐私保护中", viewModel.StatusText);
        Assert.True(viewModel.IsPrivacyProtected);
        Assert.False(viewModel.ResumeCaptureCommand.CanExecute(null));
    }

    [Fact]
    public async Task AutomaticRecoveryAfterVisibleProtectionDoesNotFlashResumeStatus()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        using var viewModel = CreateWithoutSynchronizationContext(
            service,
            delay.WaitAsync);
        var publishedStatusTexts = ObserveStatusTexts(viewModel);
        var protectionShown = ObserveStateAsync(viewModel, CaptureState.Paused);

        service.TransitionTo(
            CaptureState.Paused,
            reason: CaptureReasonCode.PolicyBlocked);
        delay.Release();
        await protectionShown;

        service.TransitionTo(CaptureState.Resuming);

        Assert.Equal(CaptureState.Paused, viewModel.State);
        Assert.Equal("隐私保护中", viewModel.StatusText);
        Assert.DoesNotContain("正在恢复录制", publishedStatusTexts);
        Assert.Equal(2, delay.RequestCount);

        service.TransitionTo(CaptureState.Recording);

        AssertRecordingPresentation(viewModel);
        Assert.DoesNotContain("正在恢复录制", publishedStatusTexts);
    }

    [Fact]
    public async Task PersistentAutomaticRecoveryIsShownAfterCoalescingWindow()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        using var viewModel = CreateWithoutSynchronizationContext(
            service,
            delay.WaitAsync);
        var protectionShown = ObserveStateAsync(viewModel, CaptureState.Paused);

        service.TransitionTo(
            CaptureState.Paused,
            reason: CaptureReasonCode.PolicyBlocked);
        delay.Release();
        await protectionShown;

        var recoveryShown = ObserveStateAsync(viewModel, CaptureState.Resuming);
        service.TransitionTo(CaptureState.Resuming);

        Assert.Equal(CaptureState.Paused, viewModel.State);
        Assert.Equal(2, delay.RequestCount);

        delay.Release();
        await recoveryShown;

        Assert.Equal(CaptureState.Resuming, viewModel.State);
        Assert.Equal("正在恢复录制", viewModel.StatusText);
        Assert.True(viewModel.IsTransitioning);
    }

    [Theory]
    [InlineData(CaptureReasonCode.ExcludedApplication)]
    [InlineData(CaptureReasonCode.ExcludedWindow)]
    public void ExplicitExclusionSupersedesPendingGenericRebindImmediately(
        CaptureReasonCode reason)
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        using var viewModel = new CaptureStatusViewModel(service, delay.WaitAsync);

        service.TransitionTo(
            CaptureState.Paused,
            reason: CaptureReasonCode.PolicyBlocked);
        AssertRecordingPresentation(viewModel);

        service.TransitionTo(CaptureState.Paused, reason: reason);

        Assert.Equal(CaptureState.Paused, viewModel.State);
        Assert.Equal(reason, viewModel.Reason);
        Assert.Equal("当前内容已排除", viewModel.StatusText);
        Assert.True(viewModel.IsPrivacyProtected);
        Assert.True(delay.IsCanceled);
    }

    [Fact]
    public void UserPauseKeepsManualResumeAvailable()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        using var viewModel = new CaptureStatusViewModel(service);

        service.TransitionTo(CaptureState.Paused, reason: CaptureReasonCode.UserPaused);

        Assert.Equal("录制已暂停", viewModel.StatusText);
        Assert.False(viewModel.IsPrivacyProtected);
        Assert.True(viewModel.ResumeCaptureCommand.CanExecute(null));
    }

    [Fact]
    public void ManualPauseAndResumeTransitionsAreNotCoalesced()
    {
        var service = new StubCaptureService(CaptureState.Recording);
        var delay = new ControlledDelay();
        using var viewModel = new CaptureStatusViewModel(service, delay.WaitAsync);

        service.TransitionTo(CaptureState.Pausing);
        Assert.Equal(CaptureState.Pausing, viewModel.State);
        Assert.Equal("正在暂停录制", viewModel.StatusText);

        service.TransitionTo(
            CaptureState.Paused,
            reason: CaptureReasonCode.UserPaused);
        Assert.Equal(CaptureState.Paused, viewModel.State);
        Assert.Equal("录制已暂停", viewModel.StatusText);

        service.TransitionTo(CaptureState.Resuming);
        Assert.Equal(CaptureState.Resuming, viewModel.State);
        Assert.Equal("正在恢复录制", viewModel.StatusText);
        Assert.Null(delay.RequestedDelay);
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

    [Fact]
    public void ConstructorResnapshotsStatusAfterSubscribing()
    {
        var service = new StubCaptureService(CaptureState.Stopped)
        {
            TransitionToRecordingWhenSubscriberIsAdded = true,
        };

        using var viewModel = new CaptureStatusViewModel(service);

        Assert.Equal(CaptureState.Recording, viewModel.State);
        Assert.Equal("正在录制", viewModel.StatusText);
    }

    [Fact]
    public void StaleNotificationUsesTheServiceCurrentSnapshot()
    {
        var service = new StubCaptureService(CaptureState.Stopped);
        using var viewModel = new CaptureStatusViewModel(service);
        service.TransitionTo(CaptureState.Recording);

        service.PublishStaleStatus(CaptureState.Stopped);

        Assert.Equal(CaptureState.Recording, viewModel.State);
        Assert.Equal("正在录制", viewModel.StatusText);
    }

    private static void AssertRecordingPresentation(
        CaptureStatusViewModel viewModel)
    {
        Assert.Equal(CaptureState.Recording, viewModel.State);
        Assert.Equal(CaptureReasonCode.None, viewModel.Reason);
        Assert.Equal("正在录制", viewModel.StatusText);
        Assert.True(viewModel.IsRecording);
        Assert.False(viewModel.IsTransitioning);
        Assert.False(viewModel.IsPrivacyProtected);
    }

    private static CaptureStatusViewModel CreateWithoutSynchronizationContext(
        ICaptureService captureService,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            return new CaptureStatusViewModel(captureService, delayAsync);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static Task ObserveStateAsync(
        CaptureStatusViewModel viewModel,
        CaptureState expectedState)
    {
        if (viewModel.State == expectedState)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += OnPropertyChanged;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        void OnPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs eventArgs)
        {
            _ = sender;
            if (eventArgs.PropertyName != nameof(CaptureStatusViewModel.State)
                || viewModel.State != expectedState)
            {
                return;
            }

            viewModel.PropertyChanged -= OnPropertyChanged;
            completion.TrySetResult();
        }
    }

    private static List<string> ObserveStatusTexts(
        CaptureStatusViewModel viewModel)
    {
        var statusTexts = new List<string>();
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(CaptureStatusViewModel.StatusText))
            {
                statusTexts.Add(viewModel.StatusText);
            }
        };
        return statusTexts;
    }

    private sealed class ControlledDelay
    {
        private TaskCompletionSource? _release;
        private CancellationTokenRegistration _cancellationRegistration;

        public TimeSpan? RequestedDelay { get; private set; }

        public int RequestCount { get; private set; }

        public bool IsCanceled { get; private set; }

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Assert.True(_release is null || _release.Task.IsCompleted);
            var release = new TaskCompletionSource();
            _release = release;
            RequestedDelay = delay;
            RequestCount++;
            _cancellationRegistration = cancellationToken.Register(
                () =>
                {
                    IsCanceled = true;
                    release.TrySetCanceled(cancellationToken);
                });
            return release.Task;
        }

        public void Release()
        {
            var release = _release;
            Assert.NotNull(release);
            _cancellationRegistration.Dispose();
            release.TrySetResult();
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

        public StubCaptureService(CaptureState state)
        {
            CurrentStatus = CreateStatus(state);
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

        public void TransitionTo(
            CaptureState state,
            string? detail = null,
            CaptureReasonCode reason = CaptureReasonCode.None)
        {
            TransitionTo(state, detail, reason, raiseEvent: true);
        }

        public void PublishStaleStatus(CaptureState state)
        {
            _statusChanged?.Invoke(
                this,
                new CaptureStatusChangedEventArgs(
                    CurrentStatus,
                    CreateStatus(state)));
        }

        private void TransitionTo(
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

        private static CaptureStatus CreateStatus(
            CaptureState state,
            string? detail = null,
            CaptureReasonCode reason = CaptureReasonCode.None)
        {
            return new CaptureStatus(
                state,
                new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
                detail,
                Reason: reason);
        }
    }
}
