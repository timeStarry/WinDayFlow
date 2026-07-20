using System.Collections.ObjectModel;

namespace WinDayFlow.Domain;

public sealed record TimelineEntryEdit
{
    public TimelineEntryEdit(
        DateTimeOffset editedAt,
        TimeRange? range = null,
        string? title = null,
        string? summary = null,
        ActivityCategory? category = null,
        ProductivityKind? productivity = null,
        IReadOnlyList<string>? tags = null)
    {
        if (title is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
        }

        if (category.HasValue && !Enum.IsDefined(category.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown activity category.");
        }

        if (productivity.HasValue && !Enum.IsDefined(productivity.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(productivity),
                productivity,
                "Unknown productivity kind.");
        }

        if (range is null
            && title is null
            && summary is null
            && !category.HasValue
            && !productivity.HasValue
            && tags is null)
        {
            throw new ArgumentException("A user edit must change at least one field.");
        }

        EditedAt = editedAt;
        Range = range;
        Title = title;
        Summary = summary;
        Category = category;
        Productivity = productivity;
        Tags = tags is null ? null : CopyTags(tags);
    }

    public DateTimeOffset EditedAt { get; }

    public TimeRange? Range { get; }

    public string? Title { get; }

    public string? Summary { get; }

    public ActivityCategory? Category { get; }

    public ProductivityKind? Productivity { get; }

    public IReadOnlyList<string>? Tags { get; }

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
