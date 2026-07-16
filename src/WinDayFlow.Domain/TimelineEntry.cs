using System.Collections.ObjectModel;

namespace WinDayFlow.Domain;

public sealed record TimelineEntry
{
    public TimelineEntry(
        Guid id,
        TimeRange range,
        string title,
        string summary,
        ActivityCategory category,
        ProductivityKind productivity,
        IReadOnlyList<AppUsage> apps,
        IReadOnlyList<string> tags,
        double? confidence,
        EvidenceReference? evidence,
        string? analysisVersion,
        UserEditProvenance? userEdits = null,
        TimelineEntryOrigin origin = TimelineEntryOrigin.Analyzed,
        long revision = 0)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry must have a non-empty identifier.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(tags);

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown activity category.");
        }

        if (!Enum.IsDefined(productivity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(productivity),
                productivity,
                "Unknown productivity kind.");
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown timeline entry origin.");
        }

        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "A timeline entry revision cannot be negative.");
        }

        ValidateRange(range);

        if (analysisVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(analysisVersion);
        }

        if (confidence.HasValue
            && (confidence.Value is < 0 or > 1 || double.IsNaN(confidence.Value)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                confidence,
                "Confidence must be a number from zero through one.");
        }

        if (origin == TimelineEntryOrigin.Analyzed
            && (!confidence.HasValue || evidence is null || analysisVersion is null))
        {
            throw new ArgumentException(
                "Analyzed timeline entries require confidence, evidence, and an analysis version.",
                nameof(origin));
        }

        if (origin == TimelineEntryOrigin.Manual
            && (confidence.HasValue || evidence is not null || analysisVersion is not null))
        {
            throw new ArgumentException(
                "Manual timeline entries cannot contain generated analysis evidence.",
                nameof(origin));
        }

        Id = id;
        Range = range;
        Title = title;
        Summary = summary;
        Category = category;
        Productivity = productivity;
        Apps = CopyApps(apps);
        Tags = CopyTags(tags);
        Confidence = confidence;
        Evidence = evidence;
        AnalysisVersion = analysisVersion;
        UserEdits = userEdits ?? UserEditProvenance.Empty;
        Origin = origin;
        Revision = revision;
    }

    public Guid Id { get; private init; }

    public TimeRange Range { get; private init; }

    public string Title { get; private init; }

    public string Summary { get; private init; }

    public ActivityCategory Category { get; private init; }

    public ProductivityKind Productivity { get; private init; }

    public IReadOnlyList<AppUsage> Apps { get; private init; }

    public IReadOnlyList<string> Tags { get; private init; }

    public double? Confidence { get; private init; }

    public EvidenceReference? Evidence { get; private init; }

    public string? AnalysisVersion { get; private init; }

    public UserEditProvenance UserEdits { get; private init; }

    public TimelineEntryOrigin Origin { get; private init; }

    public long Revision { get; private init; }

    public bool HasUserEdits => UserEdits.HasEdits;

    public bool HasEvidence => Evidence is not null;

    public static TimelineEntry FromActivity(Guid id, Activity activity, string analysisVersion)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return new TimelineEntry(
            id,
            activity.Range,
            activity.Title,
            activity.Summary,
            activity.Category,
            activity.Productivity,
            activity.Apps,
            activity.Tags,
            activity.Confidence,
            activity.Evidence,
            analysisVersion);
    }

    public static TimelineEntry CreateManual(
        Guid id,
        TimeRange range,
        string title,
        string summary,
        ActivityCategory category,
        ProductivityKind productivity,
        IReadOnlyList<string> tags,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(range);

        var provenance = new UserEditProvenance(
            rangeEditedAt: createdAt,
            titleEditedAt: createdAt,
            summaryEditedAt: createdAt,
            categoryEditedAt: createdAt,
            productivityEditedAt: createdAt,
            tagsEditedAt: createdAt);

        return new TimelineEntry(
            id,
            range,
            title,
            summary,
            category,
            productivity,
            [],
            tags,
            confidence: null,
            evidence: null,
            analysisVersion: null,
            userEdits: provenance,
            origin: TimelineEntryOrigin.Manual);
    }

    public TimelineEntry ApplyUserEdit(TimelineEntryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (edit.Range is not null)
        {
            ValidateRange(edit.Range);
        }

        return this with
        {
            Range = edit.Range ?? Range,
            Title = edit.Title ?? Title,
            Summary = edit.Summary ?? Summary,
            Category = edit.Category ?? Category,
            Productivity = edit.Productivity ?? Productivity,
            Tags = edit.Tags ?? Tags,
            UserEdits = UserEdits.Mark(edit),
        };
    }

    public TimelineEntry ApplyReanalysis(Activity activity, string analysisVersion)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (Origin == TimelineEntryOrigin.Manual)
        {
            throw new InvalidOperationException("Manual timeline entries cannot be reanalyzed.");
        }

        return new TimelineEntry(
            Id,
            UserEdits.RangeEditedAt.HasValue ? Range : activity.Range,
            UserEdits.TitleEditedAt.HasValue ? Title : activity.Title,
            UserEdits.SummaryEditedAt.HasValue ? Summary : activity.Summary,
            UserEdits.CategoryEditedAt.HasValue ? Category : activity.Category,
            UserEdits.ProductivityEditedAt.HasValue ? Productivity : activity.Productivity,
            activity.Apps,
            UserEdits.TagsEditedAt.HasValue ? Tags : activity.Tags,
            activity.Confidence,
            activity.Evidence,
            analysisVersion,
            UserEdits,
            TimelineEntryOrigin.Analyzed,
            Revision);
    }

    private static ReadOnlyCollection<AppUsage> CopyApps(IReadOnlyList<AppUsage> apps)
    {
        var copy = apps.ToArray();
        if (copy.Any(static app => app is null))
        {
            throw new ArgumentException("Application usage cannot contain null items.", nameof(apps));
        }

        return Array.AsReadOnly(copy);
    }

    private static ReadOnlyCollection<string> CopyTags(IReadOnlyList<string> tags)
    {
        var copy = tags.ToArray();
        if (copy.Any(static tag => string.IsNullOrWhiteSpace(tag)))
        {
            throw new ArgumentException("Tags cannot contain null or blank items.", nameof(tags));
        }

        return Array.AsReadOnly(copy);
    }

    private static void ValidateRange(TimeRange range)
    {
        var lastIncludedInstant = range.End.AddTicks(-1);
        if (DateOnly.FromDateTime(range.Start.DateTime)
            != DateOnly.FromDateTime(lastIncludedInstant.DateTime))
        {
            throw new ArgumentException(
                "A timeline entry must remain within one local calendar day.",
                nameof(range));
        }
    }
}
