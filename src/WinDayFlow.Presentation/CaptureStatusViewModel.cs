using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinDayFlow.Application.Capture;

namespace WinDayFlow.Presentation.Capture;

public sealed partial class CaptureStatusViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureService _captureService;
    private readonly SynchronizationContext? _synchronizationContext;
    private CaptureStatus _status;
    private bool _isDisposed;

    public CaptureStatusViewModel(ICaptureService captureService)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _synchronizationContext = SynchronizationContext.Current;
        _status = captureService.CurrentStatus;
        _captureService.StatusChanged += OnStatusChanged;
    }

    public CaptureState State => _status.State;

    public DateTimeOffset ChangedAt => _status.ChangedAt;

    public string StatusText => State switch
    {
        CaptureState.Unavailable => "录制不可用",
        CaptureState.BlockedByConsent => "需要录制授权",
        CaptureState.Stopped => "录制已停止",
        CaptureState.Starting => "正在启动录制",
        CaptureState.Recording => "正在录制",
        CaptureState.Pausing => "正在暂停录制",
        CaptureState.Paused => "录制已暂停",
        CaptureState.Resuming => "正在恢复录制",
        CaptureState.Stopping => "正在停止录制",
        CaptureState.Faulted => "录制发生错误",
        _ => "录制状态未知",
    };

    public string DetailText => _status.Detail ?? State switch
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

    public bool CanResumeCapture => State == CaptureState.Paused;

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

        _captureService.StatusChanged -= OnStatusChanged;
        _isDisposed = true;
    }

    private void OnStatusChanged(object? sender, CaptureStatusChangedEventArgs eventArgs)
    {
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

        ApplyStatus(eventArgs.Current);
    }

    private void ApplyStatus(CaptureStatus status)
    {
        if (_isDisposed || status == _status)
        {
            return;
        }

        _status = status;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ChangedAt));
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

    private sealed record StatusUpdate(
        CaptureStatusViewModel ViewModel,
        CaptureStatus Status);
}
