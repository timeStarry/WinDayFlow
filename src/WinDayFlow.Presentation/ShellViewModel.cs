using CommunityToolkit.Mvvm.ComponentModel;
using WinDayFlow.Presentation.Capture;

namespace WinDayFlow.Presentation.Shell;

public sealed class ShellViewModel : ObservableObject
{
    public ShellViewModel(CaptureStatusViewModel captureStatus)
    {
        CaptureStatus = captureStatus ?? throw new ArgumentNullException(nameof(captureStatus));
    }

    public string ApplicationTitle { get; } = "WinDayFlow";

    public string TimelineTitle { get; } = "时间线";

    public string TodayTitle { get; } = "今天";

    public string InsightsTitle { get; } = "洞察";

    public string SystemTitle { get; } = "系统";

    public CaptureStatusViewModel CaptureStatus { get; }
}
