using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using Xunit;

namespace WinDayFlow.Application.Tests;

public sealed class TimelineCommandServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateManualAsyncPersistsUserOwnedEntryWithoutEvidence()
    {
        var store = new MemoryTimelineStore();
        var service = CreateService(store);
        var draft = CreateDraft("Draft roadmap");

        var entry = await service.CreateManualAsync(draft);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(TimelineEntryOrigin.Manual, entry.Origin);
        Assert.Equal("Draft roadmap", entry.Title);
        Assert.Null(entry.Evidence);
        Assert.Null(entry.Confidence);
        Assert.Null(entry.AnalysisVersion);
        Assert.Equal(Now, entry.UserEdits.TitleEditedAt);
        Assert.Same(entry, await store.GetByIdAsync(entry.Id));
    }

    [Fact]
    public async Task UpdateAsyncPersistsEditAndRefreshesProvenance()
    {
        var originalTime = Now.AddHours(-1);
        var store = new MemoryTimelineStore();
        var original = TimelineEntry.CreateManual(
            Guid.Parse("00000000-0000-0000-0000-000000000010"),
            CreateDraft("Original").Range,
            "Original",
            "Before edit",
            ActivityCategory.Planning,
            ProductivityKind.Neutral,
            ["before"],
            originalTime);
        await store.AddAsync(original);
        var service = CreateService(store);
        var replacement = new TimelineEntryDraft(
            new TimeRange(
                original.Range.Start.AddMinutes(15),
                original.Range.End.AddMinutes(30)),
            "Updated",
            "After edit",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            ["after"]);

        var updated = await service.UpdateAsync(original.Id, replacement);

        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("Updated", updated.Title);
        Assert.Equal(replacement.Range, updated.Range);
        Assert.Equal(["after"], updated.Tags);
        Assert.Equal(Now, updated.UserEdits.RangeEditedAt);
        Assert.Equal(Now, updated.UserEdits.TitleEditedAt);
        Assert.Equal(TimelineEntryOrigin.Manual, updated.Origin);
        Assert.Equal(updated, await store.GetByIdAsync(original.Id));
    }

    [Fact]
    public async Task DeleteAsyncReportsWhetherEntryExisted()
    {
        var store = new MemoryTimelineStore();
        var service = CreateService(store);
        var entry = await service.CreateManualAsync(CreateDraft("Delete me"));

        Assert.True(await service.DeleteAsync(entry.Id));
        Assert.False(await service.DeleteAsync(entry.Id));
        Assert.Null(await store.GetByIdAsync(entry.Id));
    }

    [Fact]
    public async Task UpdateAsyncRejectsMissingEntry()
    {
        var service = CreateService(new MemoryTimelineStore());

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateAsync(Guid.NewGuid(), CreateDraft("Missing")));
    }

    [Fact]
    public async Task UpdateAsyncMarksOnlyFieldsThatActuallyChanged()
    {
        var store = new MemoryTimelineStore();
        var range = CreateDraft("Original").Range;
        var original = TimelineEntry.FromActivity(
            Guid.Parse("00000000-0000-0000-0000-000000000020"),
            new Activity(
                range,
                "Original",
                "Keep this summary",
                ActivityCategory.Planning,
                ProductivityKind.Neutral,
                [],
                ["keep"],
                0.8,
                new EvidenceReference("chunk-20", "test://evidence/20")),
            "analysis-v1");
        await store.AddAsync(original);
        var service = CreateService(store);
        var titleOnly = new TimelineEntryDraft(
            range,
            "Changed title",
            original.Summary,
            original.Category,
            original.Productivity,
            original.Tags);

        var updated = await service.UpdateAsync(original.Id, titleOnly);

        Assert.Equal(Now, updated.UserEdits.TitleEditedAt);
        Assert.Null(updated.UserEdits.RangeEditedAt);
        Assert.Null(updated.UserEdits.SummaryEditedAt);
        Assert.Null(updated.UserEdits.CategoryEditedAt);
        Assert.Null(updated.UserEdits.ProductivityEditedAt);
        Assert.Null(updated.UserEdits.TagsEditedAt);
    }

    [Fact]
    public async Task UpdateAsyncSkipsPersistenceWhenDraftIsUnchanged()
    {
        var store = new MemoryTimelineStore();
        var original = TimelineEntry.CreateManual(
            Guid.Parse("00000000-0000-0000-0000-000000000021"),
            CreateDraft("Unchanged").Range,
            "Unchanged",
            "Test activity",
            ActivityCategory.Planning,
            ProductivityKind.Neutral,
            ["test"],
            Now.AddHours(-1));
        await store.AddAsync(original);
        var service = CreateService(store);

        var result = await service.UpdateAsync(original.Id, new TimelineEntryDraft(
            original.Range,
            original.Title,
            original.Summary,
            original.Category,
            original.Productivity,
            original.Tags));

        Assert.Same(original, result);
        Assert.Equal(0, store.UpdateCallCount);
    }

    private static TimelineCommandService CreateService(ITimelineStore store)
    {
        return new TimelineCommandService(store, new FixedTimeProvider(Now));
    }

    private static TimelineEntryDraft CreateDraft(string title)
    {
        return new TimelineEntryDraft(
            new TimeRange(
                new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(8))),
            title,
            "Test activity",
            ActivityCategory.Planning,
            ProductivityKind.Neutral,
            ["test"]);
    }

    private sealed class MemoryTimelineStore : ITimelineStore
    {
        private readonly Dictionary<Guid, TimelineEntry> _entries = [];

        public int UpdateCallCount { get; private set; }

        public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
            DateOnly day,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TimelineEntry> entries = _entries.Values
                .Where(entry => DateOnly.FromDateTime(entry.Range.Start.DateTime) == day)
                .ToArray();
            return Task.FromResult(entries);
        }

        public Task<TimelineEntry?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_entries.GetValueOrDefault(id));
        }

        public Task AddAsync(
            TimelineEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Add(entry.Id, entry);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(
            TimelineEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCallCount++;
            if (!_entries.ContainsKey(entry.Id))
            {
                return Task.FromResult(false);
            }

            _entries[entry.Id] = entry;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_entries.Remove(id));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
