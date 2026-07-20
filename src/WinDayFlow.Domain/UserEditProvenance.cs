namespace WinDayFlow.Domain;

public sealed record UserEditProvenance
{
    public UserEditProvenance(
        DateTimeOffset? rangeEditedAt = null,
        DateTimeOffset? titleEditedAt = null,
        DateTimeOffset? summaryEditedAt = null,
        DateTimeOffset? categoryEditedAt = null,
        DateTimeOffset? productivityEditedAt = null,
        DateTimeOffset? tagsEditedAt = null)
    {
        RangeEditedAt = rangeEditedAt;
        TitleEditedAt = titleEditedAt;
        SummaryEditedAt = summaryEditedAt;
        CategoryEditedAt = categoryEditedAt;
        ProductivityEditedAt = productivityEditedAt;
        TagsEditedAt = tagsEditedAt;
    }

    public static UserEditProvenance Empty { get; } = new();

    public DateTimeOffset? RangeEditedAt { get; private init; }

    public DateTimeOffset? TitleEditedAt { get; private init; }

    public DateTimeOffset? SummaryEditedAt { get; private init; }

    public DateTimeOffset? CategoryEditedAt { get; private init; }

    public DateTimeOffset? ProductivityEditedAt { get; private init; }

    public DateTimeOffset? TagsEditedAt { get; private init; }

    public bool HasEdits =>
        RangeEditedAt.HasValue
        || TitleEditedAt.HasValue
        || SummaryEditedAt.HasValue
        || CategoryEditedAt.HasValue
        || ProductivityEditedAt.HasValue
        || TagsEditedAt.HasValue;

    internal UserEditProvenance Mark(TimelineEntryEdit edit)
    {
        return this with
        {
            RangeEditedAt = edit.Range is null ? RangeEditedAt : edit.EditedAt,
            TitleEditedAt = edit.Title is null ? TitleEditedAt : edit.EditedAt,
            SummaryEditedAt = edit.Summary is null ? SummaryEditedAt : edit.EditedAt,
            CategoryEditedAt = edit.Category.HasValue ? edit.EditedAt : CategoryEditedAt,
            ProductivityEditedAt = edit.Productivity.HasValue
                ? edit.EditedAt
                : ProductivityEditedAt,
            TagsEditedAt = edit.Tags is null ? TagsEditedAt : edit.EditedAt,
        };
    }
}
