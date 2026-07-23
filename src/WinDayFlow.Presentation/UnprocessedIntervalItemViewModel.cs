using WinDayFlow.Application.Analysis;

namespace WinDayFlow.Presentation.Timeline;

public sealed class UnprocessedIntervalItemViewModel
{
    public UnprocessedIntervalItemViewModel(UnprocessedInterval interval)
    {
        ArgumentNullException.ThrowIfNull(interval);

        Start = interval.Range.Start;
        End = interval.Range.End;
        Duration = interval.Range.Duration;
        State = interval.State;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration { get; }

    public UnprocessedIntervalState State { get; }

    public string TimeText => Start.Date == End.Date
        ? $"{Start:HH:mm} - {End:HH:mm}"
        : $"{Start:MM-dd HH:mm} - {End:MM-dd HH:mm}";

    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours} 小时 {Duration.Minutes} 分钟"
        : $"{Math.Max(1, (int)Math.Ceiling(Duration.TotalMinutes))} 分钟";

    public string StateText => State switch
    {
        UnprocessedIntervalState.LocalOnly => "仅保存在本机",
        UnprocessedIntervalState.Queued => "等待分析",
        UnprocessedIntervalState.Processing => "正在分析",
        UnprocessedIntervalState.RetryScheduled => "等待重试",
        UnprocessedIntervalState.Failed => "分析未完成",
        UnprocessedIntervalState.Cancelled => "分析已取消",
        _ => "状态未知",
    };

    public string StateDescription => State switch
    {
        UnprocessedIntervalState.LocalOnly => "录制内容已安全保存在此设备。",
        UnprocessedIntervalState.Queued => "录制内容已进入分析队列。",
        UnprocessedIntervalState.Processing => "正在生成时间线活动。",
        UnprocessedIntervalState.RetryScheduled => "分析将在稍后自动重试。",
        UnprocessedIntervalState.Failed => "本次分析未生成时间线活动。",
        UnprocessedIntervalState.Cancelled => "本次分析已停止。",
        _ => "暂时无法识别处理状态。",
    };

    public string StatusGlyph => State switch
    {
        UnprocessedIntervalState.LocalOnly => "\uE74E",
        UnprocessedIntervalState.Queued => "\uE823",
        UnprocessedIntervalState.Processing => "\uE895",
        UnprocessedIntervalState.RetryScheduled => "\uE72C",
        UnprocessedIntervalState.Failed => "\uEA39",
        UnprocessedIntervalState.Cancelled => "\uE711",
        _ => "\uE946",
    };
}
