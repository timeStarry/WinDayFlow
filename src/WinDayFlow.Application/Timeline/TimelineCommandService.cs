using WinDayFlow.Domain;

namespace WinDayFlow.Application.Timeline;

public sealed class TimelineCommandService
{
    private readonly ITimelineStore _store;
    private readonly TimeProvider _timeProvider;

    public TimelineCommandService(
        ITimelineStore store,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TimelineEntry> CreateManualAsync(
        TimelineEntryDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var entry = TimelineEntry.CreateManual(
            Guid.NewGuid(),
            draft.Range,
            draft.Title,
            draft.Summary,
            draft.Category,
            draft.Productivity,
            draft.Tags,
            _timeProvider.GetUtcNow());

        await _store.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<TimelineEntry> UpdateAsync(
        Guid id,
        TimelineEntryDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry identifier cannot be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(draft);

        var current = await _store
            .GetByIdAsync(id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Timeline entry '{id}' was not found.");

        var range = current.Range == draft.Range ? null : draft.Range;
        var title = string.Equals(current.Title, draft.Title, StringComparison.Ordinal)
            ? null
            : draft.Title;
        var summary = string.Equals(current.Summary, draft.Summary, StringComparison.Ordinal)
            ? null
            : draft.Summary;
        ActivityCategory? category = current.Category == draft.Category
            ? null
            : draft.Category;
        ProductivityKind? productivity = current.Productivity == draft.Productivity
            ? null
            : draft.Productivity;
        IReadOnlyList<string>? tags = current.Tags.SequenceEqual(
            draft.Tags,
            StringComparer.Ordinal)
                ? null
                : draft.Tags;

        if (range is null
            && title is null
            && summary is null
            && !category.HasValue
            && !productivity.HasValue
            && tags is null)
        {
            return current;
        }

        var updated = current.ApplyUserEdit(new TimelineEntryEdit(
            _timeProvider.GetUtcNow(),
            range,
            title,
            summary,
            category,
            productivity,
            tags));

        if (!await _store.UpdateAsync(updated, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Timeline entry '{id}' changed before this edit could be saved.");
        }

        return await _store
            .GetByIdAsync(id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Timeline entry '{id}' no longer exists.");
    }

    public Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry identifier cannot be empty.", nameof(id));
        }

        return _store.DeleteAsync(id, cancellationToken);
    }
}
