using WinDayFlow.Domain;

namespace WinDayFlow.Presentation.Timeline;

public sealed class TimelineEntryItemViewModel
{
    public TimelineEntryItemViewModel(TimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        Id = entry.Id;
        Start = entry.Range.Start;
        End = entry.Range.End;
        Duration = entry.Range.Duration;
        Title = entry.Title;
        Summary = entry.Summary;
        Category = entry.Category;
        Productivity = entry.Productivity;
        Origin = entry.Origin;
        PrimaryApplicationText = entry.Apps
            .OrderByDescending(static app => app.Duration)
            .Select(static app => app.DisplayName)
            .FirstOrDefault() ?? string.Empty;
        Tags = entry.Tags;
        HasUserEdits = entry.HasUserEdits;
        HasEvidence = entry.HasEvidence;
    }

    public TimelineEntry Entry { get; }

    public Guid Id { get; }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration { get; }

    public string Title { get; }

    public string Summary { get; }

    public ActivityCategory Category { get; }

    public ProductivityKind Productivity { get; }

    public TimelineEntryOrigin Origin { get; }

    public string PrimaryApplicationText { get; }

    public IReadOnlyList<string> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    public string TagsText => string.Join(" · ", Tags);

    public bool HasUserEdits { get; }

    public bool HasEvidence { get; }

    public string ConfidenceText => Entry.Confidence.HasValue
        ? Entry.Confidence.Value.ToString("P0", System.Globalization.CultureInfo.CurrentCulture)
        : "不适用";

    public string AnalysisVersionText => Entry.AnalysisVersion ?? "不适用";

    public string EvidenceChunkText => Entry.Evidence?.CaptureChunkId ?? "无";

    public string EvidencePathText => Entry.Evidence?.ArtifactPath ?? "无";

    public string TimeText => $"{Start:HH:mm} - {End:HH:mm}";

    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours} 小时 {Duration.Minutes} 分钟"
        : $"{Math.Max(1, (int)Math.Ceiling(Duration.TotalMinutes))} 分钟";

    public string CategoryText => Category switch
    {
        ActivityCategory.FocusedWork => "专注工作",
        ActivityCategory.Communication => "沟通",
        ActivityCategory.Meeting => "会议",
        ActivityCategory.Planning => "规划",
        ActivityCategory.Research => "调研",
        ActivityCategory.Administration => "行政事务",
        ActivityCategory.Learning => "学习",
        ActivityCategory.Break => "休息",
        ActivityCategory.Personal => "个人事务",
        _ => "未分类",
    };

    public string ProductivityText => Productivity switch
    {
        ProductivityKind.Focused => "专注",
        ProductivityKind.Neutral => "中性",
        ProductivityKind.Distracting => "分心",
        ProductivityKind.Break => "休息",
        _ => "未知",
    };

    public string OriginText => Origin switch
    {
        TimelineEntryOrigin.Manual => "手工记录",
        _ when HasUserEdits => "分析生成（用户已修订）",
        _ => "分析生成",
    };

    public string UserEditsText
    {
        get
        {
            if (!HasUserEdits)
            {
                return "未修订";
            }

            var edits = Entry.UserEdits;
            var fields = new List<string>(6);
            AddEditedField(fields, edits.RangeEditedAt, "时间");
            AddEditedField(fields, edits.TitleEditedAt, "标题");
            AddEditedField(fields, edits.SummaryEditedAt, "摘要");
            AddEditedField(fields, edits.CategoryEditedAt, "类别");
            AddEditedField(fields, edits.ProductivityEditedAt, "效率");
            AddEditedField(fields, edits.TagsEditedAt, "标签");
            return $"已修订：{string.Join("、", fields)}";
        }
    }

    private static void AddEditedField(
        List<string> fields,
        DateTimeOffset? editedAt,
        string label)
    {
        if (editedAt.HasValue)
        {
            fields.Add(label);
        }
    }
}
