using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;

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
        Attempt = interval.Attempt;
        ErrorCode = interval.ErrorCode;
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration { get; }

    public UnprocessedIntervalState State { get; }

    public int? Attempt { get; }

    public AnalysisJobErrorCode? ErrorCode { get; }

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
        UnprocessedIntervalState.Processing => Attempt is > 0
            ? $"正在执行第 {Attempt} 次分析尝试。"
            : "正在生成时间线活动。",
        UnprocessedIntervalState.RetryScheduled => DescribeFailure(
            retryScheduled: true),
        UnprocessedIntervalState.Failed => DescribeFailure(
            retryScheduled: false),
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

    private string DescribeFailure(bool retryScheduled)
    {
        var suffix = retryScheduled
            ? Attempt is > 0
                ? $"系统稍后会自动执行第 {Attempt + 1} 次尝试。"
                : "系统稍后会自动重试。"
            : "本次分析已停止，不会自动重试。";
        var reason = ErrorCode switch
        {
            AnalysisJobErrorCode.EvidenceMissing => "找不到对应的本地录制证据。",
            AnalysisJobErrorCode.EvidenceInvalid => "本地录制证据未通过完整性检查。",
            AnalysisJobErrorCode.ExtractionFailed => "无法从本地录制中提取分析截图。",
            AnalysisJobErrorCode.ProviderUnavailable => "暂时无法连接分析提供方。",
            AnalysisJobErrorCode.ProviderRateLimited => "分析提供方暂时限制了请求频率。",
            AnalysisJobErrorCode.ProviderRejected => "分析提供方拒绝了当前凭据、模型或请求。",
            AnalysisJobErrorCode.ProviderResponseInvalid => "分析提供方返回了不兼容的结果。",
            AnalysisJobErrorCode.OperationTimedOut => "证据提取或提供方请求已超时。",
            AnalysisJobErrorCode.PersistenceFailure => "分析结果暂时无法写入本地数据库。",
            AnalysisJobErrorCode.LeaseExpired => "后台任务中断，执行租约已过期。",
            _ => "分析任务遇到未知错误。",
        };
        return $"{reason}{suffix}";
    }
}
