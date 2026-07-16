using System.Collections.ObjectModel;

namespace WinDayFlow.Domain;

public sealed record Activity
{
    public Activity(
        TimeRange range,
        string title,
        string summary,
        ActivityCategory category,
        ProductivityKind productivity,
        IReadOnlyList<AppUsage> apps,
        IReadOnlyList<string> tags,
        double confidence,
        EvidenceReference evidence)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(evidence);

        if (confidence is < 0 or > 1 || double.IsNaN(confidence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                confidence,
                "Confidence must be a number from zero through one.");
        }

        Range = range;
        Title = title;
        Summary = summary;
        Category = category;
        Productivity = productivity;
        Apps = CopyApps(apps);
        Tags = CopyTags(tags);
        Confidence = confidence;
        Evidence = evidence;
    }

    public TimeRange Range { get; }

    public string Title { get; }

    public string Summary { get; }

    public ActivityCategory Category { get; }

    public ProductivityKind Productivity { get; }

    public IReadOnlyList<AppUsage> Apps { get; }

    public IReadOnlyList<string> Tags { get; }

    public double Confidence { get; }

    public EvidenceReference Evidence { get; }

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
}
