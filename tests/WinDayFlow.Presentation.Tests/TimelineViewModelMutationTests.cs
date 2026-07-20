using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using WinDayFlow.Presentation.Timeline;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class TimelineViewModelMutationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 4, 30, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    [Fact]
    public async Task CreateManualEntryAsyncMakesEntryVisibleAndSelected()
    {
        var store = new MemoryTimelineStore();
        using var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();
        var draft = CreateDraft("Write release notes");

        var result = await viewModel.CreateManualEntryAsync(draft);

        Assert.True(result);
        Assert.False(viewModel.IsSaving);
        Assert.False(viewModel.HasMutationError);
        var visibleEntry = Assert.Single(viewModel.Entries);
        Assert.Same(visibleEntry, viewModel.SelectedEntry);
        Assert.Equal("Write release notes", visibleEntry.Title);
        Assert.Equal(TimelineEntryOrigin.Manual, visibleEntry.Origin);
        Assert.True(visibleEntry.HasUserEdits);
        Assert.False(visibleEntry.HasEvidence);
        Assert.NotNull(await store.GetByIdAsync(visibleEntry.Id));
    }

    [Fact]
    public async Task UpdateSelectedEntryAsyncRefreshesFieldsAndProvenanceAndRetainsSelection()
    {
        var store = new MemoryTimelineStore();
        var original = CreateAnalyzedEntry();
        await store.AddAsync(original);
        using var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();
        var originalSelection = Assert.Single(viewModel.Entries);
        viewModel.SelectedEntry = originalSelection;
        var updatedRange = new TimeRange(
            original.Range.Start.AddMinutes(15),
            original.Range.End.AddMinutes(30));
        var draft = new TimelineEntryDraft(
            updatedRange,
            "Refine implementation",
            "Document the completed changes.",
            ActivityCategory.Administration,
            ProductivityKind.Neutral,
            ["documentation", "release"]);

        var result = await viewModel.UpdateSelectedEntryAsync(draft);

        Assert.True(result);
        Assert.False(viewModel.IsSaving);
        var visibleEntry = Assert.Single(viewModel.Entries);
        Assert.NotSame(originalSelection, visibleEntry);
        Assert.Same(visibleEntry, viewModel.SelectedEntry);
        Assert.Equal(original.Id, visibleEntry.Id);
        Assert.Equal(updatedRange.Start, visibleEntry.Start);
        Assert.Equal(updatedRange.End, visibleEntry.End);
        Assert.Equal("Refine implementation", visibleEntry.Title);
        Assert.Equal("Document the completed changes.", visibleEntry.Summary);
        Assert.Equal(ActivityCategory.Administration, visibleEntry.Category);
        Assert.Equal(ProductivityKind.Neutral, visibleEntry.Productivity);
        Assert.Equal(["documentation", "release"], visibleEntry.Tags);
        Assert.Equal(TimelineEntryOrigin.Analyzed, visibleEntry.Origin);
        Assert.True(visibleEntry.HasEvidence);
        Assert.True(visibleEntry.HasUserEdits);
        Assert.Equal(Now, visibleEntry.Entry.UserEdits.RangeEditedAt);
        Assert.Equal(Now, visibleEntry.Entry.UserEdits.TitleEditedAt);
        Assert.Equal(Now, visibleEntry.Entry.UserEdits.SummaryEditedAt);
        Assert.Equal(Now, visibleEntry.Entry.UserEdits.CategoryEditedAt);
        Assert.Equal(Now, visibleEntry.Entry.UserEdits.ProductivityEditedAt);
        Assert.Equal(Now, visibleEntry.Entry.UserEdits.TagsEditedAt);
    }

    [Fact]
    public async Task DeleteSelectedEntryAsyncRemovesEntryAndClearsSelection()
    {
        var store = new MemoryTimelineStore();
        var original = CreateAnalyzedEntry();
        await store.AddAsync(original);
        using var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();
        viewModel.SelectedEntry = Assert.Single(viewModel.Entries);

        var result = await viewModel.DeleteSelectedEntryAsync();

        Assert.True(result);
        Assert.False(viewModel.IsSaving);
        Assert.Empty(viewModel.Entries);
        Assert.Null(viewModel.SelectedEntry);
        Assert.False(viewModel.CanMutateSelectedEntry);
        Assert.Null(await store.GetByIdAsync(original.Id));
    }

    [Fact]
    public async Task CreateManualEntryAsyncFailureExposesStableErrorAndResetsSavingState()
    {
        var store = new MemoryTimelineStore
        {
            AddException = new InvalidOperationException("Sensitive persistence details."),
        };
        using var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();

        var result = await viewModel.CreateManualEntryAsync(CreateDraft("Cannot persist"));

        Assert.False(result);
        Assert.False(viewModel.IsSaving);
        Assert.True(viewModel.HasMutationError);
        Assert.Equal("无法保存活动，请检查内容后重试。", viewModel.MutationErrorMessage);
        Assert.Empty(viewModel.Entries);
        Assert.Null(viewModel.SelectedEntry);
    }

    [Fact]
    public async Task CreateManualEntryAsyncKeepsNonMatchingEntryHiddenByActiveFilters()
    {
        var store = new MemoryTimelineStore();
        using var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();
        viewModel.SelectedCategory = ActivityCategory.Communication;

        var result = await viewModel.CreateManualEntryAsync(CreateDraft("Plan release"));

        Assert.True(result);
        Assert.True(viewModel.HasActiveFilters);
        Assert.Empty(viewModel.Entries);
        Assert.Null(viewModel.SelectedEntry);
        var persistedEntry = Assert.Single(await store.GetForDayAsync(Today));
        Assert.Equal("Plan release", persistedEntry.Title);
        Assert.Equal(ActivityCategory.Planning, persistedEntry.Category);
    }

    [Fact]
    public async Task ConcurrentMutationIsRejectedWithoutResettingActiveSavingState()
    {
        var addStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryTimelineStore
        {
            AddStarted = addStarted,
            ReleaseAdd = releaseAdd,
        };
        using var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();

        var firstSave = viewModel.CreateManualEntryAsync(CreateDraft("First"));
        await addStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondSave = await viewModel.CreateManualEntryAsync(CreateDraft("Second"));

        Assert.False(secondSave);
        Assert.True(viewModel.IsSaving);
        Assert.Equal("另一项时间线操作正在进行，请稍候。", viewModel.MutationErrorMessage);

        releaseAdd.SetResult(true);
        Assert.True(await firstSave);
        Assert.False(viewModel.IsSaving);
        Assert.Single(viewModel.Entries);
    }

    [Fact]
    public async Task DisposeCancelsAnInFlightMutation()
    {
        var addStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryTimelineStore
        {
            AddStarted = addStarted,
            WaitForCancellationOnAdd = true,
        };
        var viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync();
        var save = viewModel.CreateManualEntryAsync(CreateDraft("Cancelled"));
        await addStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.Dispose();

        Assert.False(await save);
        Assert.False(viewModel.IsSaving);
        Assert.Empty(viewModel.Entries);
    }

    private static TimelineViewModel CreateViewModel(ITimelineStore store)
    {
        var timeProvider = new FixedTimeProvider(Now);
        return new TimelineViewModel(
            new TimelineQueryService(store),
            new TimelineCommandService(store, timeProvider),
            timeProvider);
    }

    private static TimelineEntryDraft CreateDraft(string title)
    {
        return new TimelineEntryDraft(
            new TimeRange(Now.AddHours(1), Now.AddHours(2)),
            title,
            "Prepare the next release.",
            ActivityCategory.Planning,
            ProductivityKind.Focused,
            ["release"]);
    }

    private static TimelineEntry CreateAnalyzedEntry()
    {
        var range = new TimeRange(Now.AddHours(1), Now.AddHours(2));
        return new TimelineEntry(
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            range,
            "Implement timeline editing",
            "Add mutation support.",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            [new AppUsage("devenv", "Visual Studio", range.Duration)],
            ["implementation"],
            0.94,
            new EvidenceReference("chunk-101", "test://evidence/101"),
            "test-v1");
    }

    private sealed class MemoryTimelineStore : ITimelineStore
    {
        private readonly Dictionary<Guid, TimelineEntry> _entries = [];

        public Exception? AddException { get; init; }

        public TaskCompletionSource<bool>? AddStarted { get; init; }

        public TaskCompletionSource<bool>? ReleaseAdd { get; init; }

        public bool WaitForCancellationOnAdd { get; init; }

        public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
            DateOnly day,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TimelineEntry> entries = _entries.Values
                .Where(entry => DateOnly.FromDateTime(entry.Range.Start.UtcDateTime) == day)
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

        public async Task AddAsync(
            TimelineEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddStarted?.TrySetResult(true);

            if (WaitForCancellationOnAdd)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (ReleaseAdd is not null)
            {
                await ReleaseAdd.Task.WaitAsync(cancellationToken);
            }

            if (AddException is not null)
            {
                throw AddException;
            }

            _entries.Add(entry.Id, entry);
        }

        public Task<bool> UpdateAsync(
            TimelineEntry entry,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
