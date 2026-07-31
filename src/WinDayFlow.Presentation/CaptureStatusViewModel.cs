using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Presentation.Capture;

public enum CaptureDisplayState
{
    Recording = 0,
    Paused = 1,
    Stopped = 2,
    NeedsAttention = 3,
}

public sealed partial class CaptureStatusViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureService _captureService;
    private readonly SynchronizationContext? _synchronizationContext;
    private CaptureStatus _status;
    private bool _isDisposed;

    public CaptureStatusViewModel(ICaptureService captureService)
        : this(
            captureService,
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            })
    {
    }

    public CaptureStatusViewModel(
        ICaptureService captureService,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _captureService = captureService
            ?? throw new ArgumentNullException(nameof(captureService));
        _ = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _synchronizationContext = SynchronizationContext.Current;
        _status = captureService.CurrentStatus;
        _captureService.StatusChanged += OnStatusChanged;
        ApplyStatus(_captureService.CurrentStatus);
    }

    public CaptureState State => _status.State;

    public CaptureDisplayState DisplayState => ProjectDisplayState(_status);

    public DateTimeOffset ChangedAt => _status.ChangedAt;

    public CaptureReasonCode Reason => _status.Reason;


    public string StatusText => DisplayState switch
    {
        CaptureDisplayState.Recording => "正在录制",
        CaptureDisplayState.Paused => "录制已暂停",
        CaptureDisplayState.Stopped => "录制已停止",
        CaptureDisplayState.NeedsAttention => "录制需要处理",
        _ => throw new InvalidOperationException("The capture display state is unsupported."),
    };

    public string DetailText => _status.Detail ?? DisplayState switch
    {
        CaptureDisplayState.Recording => "WinDayFlow 正在本地记录屏幕活动。",
        CaptureDisplayState.Paused => "本地活动记录已由用户暂停。",
        CaptureDisplayState.Stopped => "可以开始记录工作活动。",
        CaptureDisplayState.NeedsAttention => NeedsAttentionDetail(_status),
        _ => throw new InvalidOperationException("The capture display state is unsupported."),
    };

    public bool IsCaptureAvailable => State != CaptureState.Unavailable;

    public bool IsOperational => DisplayState != CaptureDisplayState.NeedsAttention;

    public bool IsRecording => DisplayState == CaptureDisplayState.Recording;

    public bool IsTransitioning => State is
        CaptureState.Starting or
        CaptureState.Pausing or
        CaptureState.Resuming or
        CaptureState.Stopping;

    public bool CanStartCapture => State is CaptureState.Stopped or CaptureState.Faulted;

    public bool CanPauseCapture => State is
        CaptureState.Starting or
        CaptureState.Recording or
        CaptureState.Resuming;

    public bool CanResumeCapture => State == CaptureState.Paused
        && Reason is CaptureReasonCode.None or CaptureReasonCode.UserPaused;

    public bool CanStopCapture => State is not (
        CaptureState.Unavailable or
        CaptureState.Stopped or
        CaptureState.Stopping);

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
    }

    private void OnStatusChanged(
        object? sender,
        CaptureStatusChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_isDisposed)
        {
            return;
        }

        var current = _captureService.CurrentStatus;
        if (_synchronizationContext is not null
            && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(
                static state =>
                {
                    var update = (StatusUpdate)state!;
                    update.ViewModel.ApplyStatus(
                        update.ViewModel._captureService.CurrentStatus);
                },
                new StatusUpdate(this));
            return;
        }

        ApplyStatus(current);
    }

    private void ApplyStatus(CaptureStatus status)
    {
        if (_isDisposed
            || status.Sequence < _status.Sequence
            || status == _status)
        {
            return;
        }

        _status = status;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(DisplayState));
        OnPropertyChanged(nameof(ChangedAt));
        OnPropertyChanged(nameof(Reason));
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

    private static CaptureDisplayState ProjectDisplayState(CaptureStatus status)
    {
        return status.State switch
        {
            CaptureState.Starting or
            CaptureState.Recording or
            CaptureState.Resuming => CaptureDisplayState.Recording,
            CaptureState.Pausing or CaptureState.Paused
                when status.Reason is CaptureReasonCode.None
                    or CaptureReasonCode.UserPaused =>
                CaptureDisplayState.Paused,
            CaptureState.Stopped or CaptureState.Stopping =>
                CaptureDisplayState.Stopped,
            _ => CaptureDisplayState.NeedsAttention,
        };
    }

    private static string NeedsAttentionDetail(CaptureStatus status)
    {
        return status.State switch
        {
            CaptureState.Unavailable => "当前设备上的录制组件不可用。",
            CaptureState.BlockedByConsent => "请先在设置中确认录制授权。",
            CaptureState.Faulted => "录制组件发生错误，请重试或重新启动应用。",
            _ => status.Reason switch
            {
                CaptureReasonCode.SessionLocked or
                CaptureReasonCode.SecureDesktop =>
                    "Windows 安全会话结束后将自动恢复录制。",
                CaptureReasonCode.SystemSleep =>
                    "系统恢复后将重新确认录制条件。",
                CaptureReasonCode.StorageConstrained =>
                    "请释放本地存储空间后再继续记录。",
                CaptureReasonCode.DisplayUnavailable or
                CaptureReasonCode.AccessLost =>
                    "正在等待可用显示器或录制权限恢复。",
                _ => "录制状态与当前意图不一致，请重试或重新启动应用。",
            },
        };
    }

    private sealed record StatusUpdate(CaptureStatusViewModel ViewModel);
}
