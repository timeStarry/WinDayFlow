using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using WinDayFlow.Presentation.Timeline;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class TimelineViewModelTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public async Task InitializeAsyncProjectsAndSortsRepositoryEntries()
    {
        var lateEntry = CreateEntry(
            2,
            14,
            "Review results",
            "Validate the implementation.",
            ActivityCategory.Administration,
            ProductivityKind.Focused,
            "Terminal",
            ["validation"]);
        var earlyEntry = CreateEntry(
            1,
            9,
            "Plan release",
            "Prepare the roadmap.",
            ActivityCategory.Planning,
            ProductivityKind.Focused,
            "Editor",
            ["roadmap"]);
        var repository = new StubTimelineRepository
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<TimelineEntry>>([lateEntry, earlyEntry]),
        };
        using var viewModel = CreateViewModel(repository);

        Assert.False(viewModel.IsInitialized);
        Assert.False(viewModel.IsEmpty);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsInitialized);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.IsEmpty);
        Assert.False(viewModel.HasError);
        Assert.Equal([lateEntry.Id, earlyEntry.Id], viewModel.Entries.Select(entry => entry.Id));
        Assert.Equal("Terminal", viewModel.Entries[0].PrimaryApplicationText);
        Assert.True(viewModel.Entries[0].HasTags);
        Assert.Equal("validation", viewModel.Entries[0].TagsText);
    }

    [Fact]
    public async Task FiltersCombineSearchCategoryAndProductivityAndCanBeCleared()
    {
        var planning = CreateEntry(
            1,
            9,
            "Plan release",
            "Prepare the roadmap.",
            ActivityCategory.Planning,
            ProductivityKind.Focused,
            "Editor",
            ["roadmap"]);
        var communication = CreateEntry(
            2,
            10,
            "Team sync",
            "Discuss delivery risks.",
            ActivityCategory.Communication,
            ProductivityKind.Neutral,
            "Teams",
            ["meeting"]);
        var distracting = CreateEntry(
            3,
            11,
            "Browse updates",
            "Read unrelated news.",
            ActivityCategory.Personal,
            ProductivityKind.Distracting,
            "Browser",
            ["break"]);
        var repository = new StubTimelineRepository
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<TimelineEntry>>(
                [planning, communication, distracting]),
        };
        using var viewModel = CreateViewModel(repository);
        await viewModel.InitializeAsync();

        viewModel.SearchText = "  TEAMS ";

        var searchedEntry = Assert.Single(viewModel.Entries);
        Assert.Equal(communication.Id, searchedEntry.Id);
        Assert.True(viewModel.HasActiveFilters);
        Assert.True(viewModel.ClearFiltersCommand.CanExecute(null));

        viewModel.SelectedCategory = ActivityCategory.Communication;
        viewModel.SelectedProductivity = ProductivityKind.Focused;

        Assert.Empty(viewModel.Entries);
        Assert.True(viewModel.IsEmpty);

        viewModel.ClearFiltersCommand.Execute(null);

        Assert.Equal(3, viewModel.Entries.Count);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Null(viewModel.SelectedCategory);
        Assert.Null(viewModel.SelectedProductivity);
        Assert.False(viewModel.HasActiveFilters);
        Assert.False(viewModel.ClearFiltersCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmptyStateIsHiddenUntilTheInitialLoadCompletes()
    {
        var result = new TaskCompletionSource<IReadOnlyList<TimelineEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new StubTimelineRepository
        {
            Handler = (_, _) => result.Task,
        };
        using var viewModel = CreateViewModel(repository);

        var load = viewModel.InitializeAsync();

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.IsEmpty);

        result.SetResult([]);
        await load;

        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.IsInitialized);
        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task LoadErrorExposesStableErrorStateAndRefreshCanRecover()
    {
        var repository = new StubTimelineRepository
        {
            Handler = (_, _) => Task.FromException<IReadOnlyList<TimelineEntry>>(
                new InvalidOperationException("Database path is sensitive.")),
        };
        using var viewModel = CreateViewModel(repository);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsInitialized);
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.HasError);
        Assert.Equal("无法加载时间线，请稍后重试。", viewModel.ErrorMessage);
        Assert.False(viewModel.IsEmpty);
        Assert.Empty(viewModel.Entries);

        var recoveredEntry = CreateEntry(
            4,
            13,
            "Recovered entry",
            "The retry succeeded.",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            "IDE",
            []);
        repository.Handler = (_, _) =>
            Task.FromResult<IReadOnlyList<TimelineEntry>>([recoveredEntry]);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal(recoveredEntry.Id, Assert.Single(viewModel.Entries).Id);
    }

    [Fact]
    public async Task DateCommandsLoadPreviousNextAndToday()
    {
        var repository = new StubTimelineRepository();
        using var viewModel = CreateViewModel(repository);

        await viewModel.InitializeAsync();
        await viewModel.PreviousDateCommand.ExecuteAsync(null);
        await viewModel.NextDateCommand.ExecuteAsync(null);
        await viewModel.NextDateCommand.ExecuteAsync(null);
        await viewModel.TodayCommand.ExecuteAsync(null);

        Assert.Equal(
            [Today, Today.AddDays(-1), Today, Today.AddDays(1), Today],
            repository.RequestedDates);
        Assert.Equal(Today, viewModel.SelectedDate);
        Assert.True(viewModel.IsToday);
    }

    [Fact]
    public async Task NewerDateRequestCannotBeOverwrittenByAnOlderResult()
    {
        var initialResult = new TaskCompletionSource<IReadOnlyList<TimelineEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextResult = new TaskCompletionSource<IReadOnlyList<TimelineEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new StubTimelineRepository
        {
            Handler = (date, _) => date == Today ? initialResult.Task : nextResult.Task,
        };
        using var viewModel = CreateViewModel(repository);
        var oldLoad = viewModel.InitializeAsync();
        var newLoad = viewModel.NextDateCommand.ExecuteAsync(null);
        var oldEntry = CreateEntry(
            5,
            9,
            "Old result",
            "Must not win.",
            ActivityCategory.Unknown,
            ProductivityKind.Unknown,
            "Old app",
            []);
        var newEntry = CreateEntry(
            6,
            10,
            "New result",
            "Must remain visible.",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            "New app",
            []);

        nextResult.SetResult([newEntry]);
        await newLoad;
        initialResult.SetResult([oldEntry]);
        await oldLoad;

        Assert.Equal(Today.AddDays(1), viewModel.SelectedDate);
        Assert.Equal(newEntry.Id, Assert.Single(viewModel.Entries).Id);
        Assert.False(viewModel.IsLoading);
    }

    private static TimelineViewModel CreateViewModel(ITimelineRepository repository)
    {
        return new TimelineViewModel(
            new TimelineQueryService(repository),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
    }

    private static TimelineEntry CreateEntry(
        int id,
        int startHour,
        string title,
        string summary,
        ActivityCategory category,
        ProductivityKind productivity,
        string applicationName,
        IReadOnlyList<string> tags)
    {
        var start = new DateTimeOffset(2026, 7, 15, startHour, 0, 0, TimeSpan.Zero);
        var range = new TimeRange(start, start.AddMinutes(45));

        return new TimelineEntry(
            new Guid(id, 0, 0, new byte[8]),
            range,
            title,
            summary,
            category,
            productivity,
            [new AppUsage($"app-{id}", applicationName, range.Duration)],
            tags,
            0.9,
            new EvidenceReference($"chunk-{id}", $"test://evidence/{id}"),
            "test-v1");
    }

    private sealed class StubTimelineRepository : ITimelineRepository
    {
        public Func<DateOnly, CancellationToken, Task<IReadOnlyList<TimelineEntry>>> Handler { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<TimelineEntry>>([]);

        public List<DateOnly> RequestedDates { get; } = [];

        public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
            DateOnly date,
            CancellationToken cancellationToken = default)
        {
            RequestedDates.Add(date);
            return Handler(date, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
