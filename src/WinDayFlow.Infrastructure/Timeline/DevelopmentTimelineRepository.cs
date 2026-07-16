using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;

namespace WinDayFlow.Infrastructure.Timeline;

public sealed class DevelopmentTimelineRepository : ITimelineRepository
{
    public static DateOnly SampleDate { get; } = new(2026, 7, 15);

    private static readonly TimeSpan SampleOffset = TimeSpan.FromHours(8);

    private static readonly IReadOnlyList<TimelineEntry> SampleEntries =
    [
        CreateSampleEntry(
            id: "11111111-1111-1111-1111-111111111111",
            startHour: 9,
            startMinute: 0,
            endHour: 10,
            endMinute: 10,
            title: "[样例] 规划 WinDayFlow 基础架构",
            summary: "梳理初始架构与交付范围的时间线样例数据。",
            category: ActivityCategory.Planning,
            productivity: ProductivityKind.Focused,
            applicationId: "sample.docs",
            applicationName: "架构文档",
            tags: ["sample", "planning"]),
        CreateSampleEntry(
            id: "22222222-2222-2222-2222-222222222222",
            startHour: 10,
            startMinute: 25,
            endHour: 11,
            endMinute: 40,
            title: "[样例] 搭建时间线界面",
            summary: "实现时间线查询与界面展示流程的样例开发时段。",
            category: ActivityCategory.FocusedWork,
            productivity: ProductivityKind.Focused,
            applicationId: "sample.ide",
            applicationName: "开发环境",
            tags: ["sample", "implementation"]),
        CreateSampleEntry(
            id: "33333333-3333-3333-3333-333333333333",
            startHour: 13,
            startMinute: 30,
            endHour: 14,
            endMinute: 15,
            title: "[样例] 评审采集边界",
            summary: "评审后续原生采集组件托管契约的样例记录。",
            category: ActivityCategory.Research,
            productivity: ProductivityKind.Neutral,
            applicationId: "sample.browser",
            applicationName: "浏览器",
            tags: ["sample", "capture"]),
        CreateSampleEntry(
            id: "44444444-4444-4444-4444-444444444444",
            startHour: 15,
            startMinute: 0,
            endHour: 16,
            endMinute: 5,
            title: "[样例] 验证基础功能",
            summary: "验证确定性时间线数据与采集不可用行为的样例记录。",
            category: ActivityCategory.Administration,
            productivity: ProductivityKind.Focused,
            applicationId: "sample.terminal",
            applicationName: "终端",
            tags: ["sample", "validation"]),
    ];

    public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<IReadOnlyList<TimelineEntry>>(cancellationToken);
        }

        IReadOnlyList<TimelineEntry> entries = SampleEntries
            .Where(entry => DateOnly.FromDateTime(entry.Range.Start.DateTime) == day)
            .ToArray();

        return Task.FromResult(entries);
    }

    private static TimelineEntry CreateSampleEntry(
        string id,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        string title,
        string summary,
        ActivityCategory category,
        ProductivityKind productivity,
        string applicationId,
        string applicationName,
        IReadOnlyList<string> tags)
    {
        var start = new DateTimeOffset(
            SampleDate.Year,
            SampleDate.Month,
            SampleDate.Day,
            startHour,
            startMinute,
            0,
            SampleOffset);
        var end = new DateTimeOffset(
            SampleDate.Year,
            SampleDate.Month,
            SampleDate.Day,
            endHour,
            endMinute,
            0,
            SampleOffset);
        var range = new TimeRange(start, end);

        return new TimelineEntry(
            Guid.Parse(id),
            range,
            title,
            summary,
            category,
            productivity,
            [new AppUsage(applicationId, applicationName, range.Duration)],
            tags,
            1.0,
            new EvidenceReference($"sample-chunk-{id[..8]}", $"sample://unavailable/{id}"),
            "development-sample-v1");
    }
}
