using System.Collections.ObjectModel;
using WinDayFlow.Domain;

namespace WinDayFlow.Application.Timeline;

public sealed record TimelineEntryDraft
{
    public TimelineEntryDraft(
        TimeRange range,
        string title,
        string summary,
        ActivityCategory category,
        ProductivityKind productivity,
        IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Any(static tag => string.IsNullOrWhiteSpace(tag)))
        {
            throw new ArgumentException("Tags cannot contain null or blank items.", nameof(tags));
        }

        var tagCopy = tags
            .Select(static tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Range = range;
        Title = title.Trim();
        Summary = summary.Trim();
        Category = category;
        Productivity = productivity;
        Tags = Array.AsReadOnly(tagCopy);
    }

    public TimeRange Range { get; }

    public string Title { get; }

    public string Summary { get; }

    public ActivityCategory Category { get; }

    public ProductivityKind Productivity { get; }

    public ReadOnlyCollection<string> Tags { get; }
}
