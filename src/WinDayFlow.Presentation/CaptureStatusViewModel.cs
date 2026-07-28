using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Presentation.Capture;

public sealed partial class CaptureStatusViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AutomaticRebindStatusDelay =
        TimeSpan.FromMilliseconds(750);

    private readonly ICaptureService _captureService;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CaptureStatus _status;
    private CaptureStatus _observedStatus;
    private CaptureStatus? _pendingAutomaticRebindStatus;
    private CancellationTokenSource? _automaticRebindDelayCancellation;
    private bool _isDisposed;

    public CaptureStatusViewModel(ICaptureService captureService)
        : this(
            captureService,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    public CaptureStatusViewModel(
        ICaptureService captureService,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _synchronizationContext = SynchronizationContext.Current;
        _status = captureService.CurrentStatus;
        _observedStatus = _status;
        _captureService.StatusChanged += OnStatusChanged;
        ApplyStatus(_captureService.CurrentStatus);
    }

    public CaptureState State => _status.State;

    public DateTimeOffset ChangedAt => _status.ChangedAt;

    public CaptureReasonCode Reason => _status.Reason;

    public bool IsPrivacyProtected =>
        (State is CaptureState.Pausing or CaptureState.Paused)
        && IsAutomaticProtectionReason(Reason);

    public string StatusText => State switch
    {
        CaptureState.Unavailable => "录制不可用",
        CaptureState.BlockedByConsent => "需要录制授权",
        CaptureState.Stopped => "录制已停止",
        CaptureState.Starting => "正在启动录制",
        CaptureState.Recording => "正在录制",
        CaptureState.Pausing when IsPrivacyProtected => ProtectionStatusText(Reason),
        CaptureState.Pausing => "正在暂停录制",
        CaptureState.Paused when IsPrivacyProtected => ProtectionStatusText(Reason),
        CaptureState.Paused => "录制已暂停",
        CaptureState.Resuming => "正在恢复录制",
        CaptureState.Stopping => "正在停止录制",
        CaptureState.Faulted => "录制发生错误",
        _ => "录制状态未知",
    };

    public string DetailText => ProtectionDetailText() ?? _status.Detail ?? State switch
    {
        CaptureState.Unavailable => "原生录制组件尚未接入。",
        CaptureState.BlockedByConsent => "请先在设置中确认录制授权。",
        CaptureState.Stopped => "可以开始记录工作活动。",
        CaptureState.Recording => "WinDayFlow 正在本地记录屏幕活动。",
        CaptureState.Paused => "活动记录已暂停。",
        CaptureState.Faulted => "请检查录制组件后重试。",
        _ => string.Empty,
    };

    public bool IsCaptureAvailable => State != CaptureState.Unavailable;

    public bool IsOperational => _status.IsOperational;

    public bool IsRecording => State == CaptureState.Recording;

    public bool IsTransitioning => State is
        CaptureState.Starting or
        CaptureState.Pausing or
        CaptureState.Resuming or
        CaptureState.Stopping;

    public bool CanStartCapture => State is CaptureState.Stopped or CaptureState.Faulted;

    public bool CanPauseCapture => State is
        CaptureState.Recording or
        CaptureState.BlockedByConsent;

    public bool CanResumeCapture => State == CaptureState.Paused
        && !IsAutomaticProtectionReason(Reason);

    public bool CanStopCapture => State is
        CaptureState.Starting or
        CaptureState.Recording or
        CaptureState.Pausing or
        CaptureState.Paused or
        CaptureState.Resuming or
        CaptureState.BlockedByConsent or
        CaptureState.Faulted;

    [RelayCommand(CanExecute = nameof(CanStartCapture))]
    private async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        await _captureService.StartAsync(cancellationToken);
        ApplyStatus(_captureService.CurrentStatus);
    }

    [RelayCommand(CanExecute = nameof(CanPauseCapture))]
    private async Task PauseCaptureAsync(CancellationToken cancellationToken)
    {
        await _captureService.PauseAsync(cancellationToken);
        ApplyStatus(_captureService.CurrentStatus);
    }

    [RelayCommand(CanExecute = nameof(CanResumeCapture))]
    private async Task ResumeCaptureAsync(CancellationToken cancellationToken)
    {
        await _captureService.ResumeAsync(cancellationToken);
        ApplyStatus(_captureService.CurrentStatus);
    }

    [RelayCommand(CanExecute = nameof(CanStopCapture))]
    private async Task StopCaptureAsync(CancellationToken cancellationToken)
    {
        await _captureService.StopAsync(cancellationToken);
        ApplyStatus(_captureService.CurrentStatus);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _captureService.StatusChanged -= OnStatusChanged;
        CancelPendingAutomaticRebind();
    }

    private void OnStatusChanged(object? sender, CaptureStatusChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_isDisposed)
        {
            return;
        }

        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var update = (StatusUpdate)state!;
                    update.ViewModel.ApplyStatus(update.Status);
                },
                new StatusUpdate(this, eventArgs.Current));
            return;
        }

        ApplyStatus(_captureService.CurrentStatus);
    }

    private void ApplyStatus(CaptureStatus status)
    {
        if (_isDisposed
            || status.Sequence < _observedStatus.Sequence
            || status == _observedStatus)
        {
            return;
        }

        _observedStatus = status;

        if (ShouldCoalesceAutomaticRebind(status))
        {
            QueueAutomaticRebindStatus(status);
            return;
        }

        CancelPendingAutomaticRebind();
        PublishStatus(status);
    }

    private bool ShouldCoalesceAutomaticRebind(CaptureStatus status)
    {
        if (_pendingAutomaticRebindStatus is not null)
        {
            return status.State == CaptureState.Resuming
                || IsGenericAutomaticRebindPause(status);
        }

        return (_status.State == CaptureState.Recording
                && IsGenericAutomaticRebindPause(status))
            || (status.State == CaptureState.Resuming
                && IsAutomaticProtectionPause(_status));
    }

    private void QueueAutomaticRebindStatus(CaptureStatus status)
    {
        _pendingAutomaticRebindStatus = status;
        if (status.State == CaptureState.Resuming)
        {
            CancelAutomaticRebindDelay();
            return;
        }

        if (_automaticRebindDelayCancellation is not null)
        {
            return;
        }

        StartAutomaticRebindDelay();
    }

    private void StartAutomaticRebindDelay()
    {
        var delayCancellation = new CancellationTokenSource();
        _automaticRebindDelayCancellation = delayCancellation;
        _ = RevealAutomaticRebindStatusAfterDelayAsync(delayCancellation);
    }

    private async Task RevealAutomaticRebindStatusAfterDelayAsync(
        CancellationTokenSource delayCancellation)
    {
        try
        {
            await _delayAsync(
                    AutomaticRebindStatusDelay,
                    delayCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
            return;
        }

        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var request = ((CaptureStatusViewModel ViewModel,
                        CancellationTokenSource DelayCancellation))state!;
                    request.ViewModel.RevealPendingAutomaticRebind(
                        request.DelayCancellation);
                },
                (this, delayCancellation));
            return;
        }

        RevealPendingAutomaticRebind(delayCancellation);
    }

    private void RevealPendingAutomaticRebind(
        CancellationTokenSource delayCancellation)
    {
        if (_isDisposed
            || delayCancellation.IsCancellationRequested
            || !ReferenceEquals(
                _automaticRebindDelayCancellation,
                delayCancellation))
        {
            return;
        }

        var currentStatus = _captureService.CurrentStatus;
        if (currentStatus.Sequence >= _observedStatus.Sequence
            && currentStatus != _observedStatus)
        {
            ApplyStatus(currentStatus);
            if (!ReferenceEquals(
                _automaticRebindDelayCancellation,
                delayCancellation))
            {
                return;
            }
        }

        var pendingStatus = _pendingAutomaticRebindStatus;
        _pendingAutomaticRebindStatus = null;
        _automaticRebindDelayCancellation = null;
        delayCancellation.Dispose();

        if (pendingStatus is not null)
        {
            PublishStatus(pendingStatus);
        }
    }

    private void CancelPendingAutomaticRebind()
    {
        _pendingAutomaticRebindStatus = null;
        CancelAutomaticRebindDelay();
    }

    private void CancelAutomaticRebindDelay()
    {
        var delayCancellation = _automaticRebindDelayCancellation;
        _automaticRebindDelayCancellation = null;
        if (delayCancellation is null)
        {
            return;
        }

        delayCancellation.Cancel();
        delayCancellation.Dispose();
    }

    private void PublishStatus(CaptureStatus status)
    {
        if (_isDisposed || status == _status)
        {
            return;
        }

        _status = status;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ChangedAt));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(IsPrivacyProtected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(IsCaptureAvailable));
        OnPropertyChanged(nameof(IsOperational));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsTransitioning));
        OnPropertyChanged(nameof(CanStartCapture));
        OnPropertyChanged(nameof(CanPauseCapture));
        OnPropertyChanged(nameof(CanResumeCapture));
        OnPropertyChanged(nameof(CanStopCapture));

        StartCaptureCommand.NotifyCanExecuteChanged();
        PauseCaptureCommand.NotifyCanExecuteChanged();
        ResumeCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
    }

    private static bool IsGenericAutomaticRebindPause(CaptureStatus status)
    {
        return status.State is CaptureState.Pausing or CaptureState.Paused
            && status.Reason == CaptureReasonCode.PolicyBlocked;
    }

    private static bool IsAutomaticProtectionPause(CaptureStatus status)
    {
        return status.State is CaptureState.Pausing or CaptureState.Paused
            && IsAutomaticProtectionReason(status.Reason);
    }

    private string? ProtectionDetailText()
    {
        if (!IsPrivacyProtected)
        {
            return null;
        }

        return Reason switch
        {
            CaptureReasonCode.ExcludedApplication =>
                "当前应用已按隐私规则排除；切换到其他应用后将自动恢复。",
            CaptureReasonCode.ExcludedWindow =>
                "当前窗口已按隐私规则排除；切换窗口后将自动恢复。",
            CaptureReasonCode.SessionLocked => "会话锁定期间不会记录屏幕活动。",
            CaptureReasonCode.SecureDesktop => "安全桌面期间不会记录屏幕活动。",
            CaptureReasonCode.RemoteSession => "远程会话期间已按当前设置暂停记录。",
            CaptureReasonCode.PresentationMode => "屏幕共享或演示期间已按当前设置暂停记录。",
            CaptureReasonCode.SystemSleep => "系统恢复后将重新确认录制条件。",
            CaptureReasonCode.StorageConstrained => "请释放本地存储空间后再继续记录。",
            CaptureReasonCode.DisplayUnavailable or CaptureReasonCode.AccessLost =>
                "正在等待可用屏幕；恢复后将自动继续。",
            _ => "正在重新确认当前录制范围；确认后将自动恢复。",
        };
    }

    private static string ProtectionStatusText(CaptureReasonCode reason)
    {
        return reason switch
        {
            CaptureReasonCode.ExcludedApplication or CaptureReasonCode.ExcludedWindow =>
                "当前内容已排除",
            CaptureReasonCode.StorageConstrained => "存储空间不足",
            CaptureReasonCode.DisplayUnavailable or CaptureReasonCode.AccessLost =>
                "等待屏幕恢复",
            _ => "隐私保护中",
        };
    }

    private static bool IsAutomaticProtectionReason(CaptureReasonCode reason)
    {
        return reason is
            CaptureReasonCode.ExcludedApplication or
            CaptureReasonCode.ExcludedWindow or
            CaptureReasonCode.SessionLocked or
            CaptureReasonCode.SecureDesktop or
            CaptureReasonCode.RemoteSession or
            CaptureReasonCode.PresentationMode or
            CaptureReasonCode.SystemSleep or
            CaptureReasonCode.DisplayUnavailable or
            CaptureReasonCode.AccessLost or
            CaptureReasonCode.StorageConstrained or
            CaptureReasonCode.PolicyBlocked;
    }

    private sealed record StatusUpdate(
        CaptureStatusViewModel ViewModel,
        CaptureStatus Status);
}
