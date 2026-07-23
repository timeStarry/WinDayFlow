using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using WinDayFlow.Presentation.Timeline;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class TimelineUnprocessedIntervalTests
{
    private static readonly TimeZoneInfo ChinaTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "WinDayFlow.Tests.UTC+08",
        TimeSpan.FromHours(8),
        "UTC+08",
        "UTC+08");

    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 4, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public async Task InitializeLoadsAndProjectsEveryUnprocessedStateForTheLocalDay()
    {
        var timelineRepository = new StubTimelineRepository();
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<UnprocessedInterval>>(
            [
                CreateInterval(6, UnprocessedIntervalState.Cancelled),
                CreateInterval(5, UnprocessedIntervalState.Failed),
                CreateInterval(4, UnprocessedIntervalState.RetryScheduled),
                CreateInterval(3, UnprocessedIntervalState.Processing),
                CreateInterval(2, UnprocessedIntervalState.Queued),
                CreateInterval(1, UnprocessedIntervalState.LocalOnly),
            ]),
        };
        using var viewModel = CreateViewModel(timelineRepository, intervalRepository);

        await viewModel.InitializeAsync();

        var requestedRange = Assert.Single(intervalRepository.RequestedRanges);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero),
            requestedRange.Start);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero),
            requestedRange.End);
        Assert.Equal(
            [
                UnprocessedIntervalState.LocalOnly,
                UnprocessedIntervalState.Queued,
                UnprocessedIntervalState.Processing,
                UnprocessedIntervalState.RetryScheduled,
                UnprocessedIntervalState.Failed,
                UnprocessedIntervalState.Cancelled,
            ],
            viewModel.UnprocessedIntervals.Select(static interval => interval.State));
        Assert.Equal(
            [
                "仅保存在本机",
                "等待分析",
                "正在分析",
                "等待重试",
                "分析未完成",
                "分析已取消",
            ],
            viewModel.UnprocessedIntervals.Select(static interval => interval.StateText));
        Assert.True(viewModel.HasUnprocessedIntervals);
        Assert.False(viewModel.HasUnprocessedIntervalLoadError);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task RefreshReplacesUnprocessedIntervalsAndDateNavigationUsesANewUtcRange()
    {
        var timelineRepository = new StubTimelineRepository();
        var call = 0;
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<UnprocessedInterval>>(
                ++call == 1
                    ? [CreateInterval(1, UnprocessedIntervalState.Queued)]
                    : [CreateInterval(2, UnprocessedIntervalState.Processing)]),
        };
        using var viewModel = CreateViewModel(timelineRepository, intervalRepository);

        await viewModel.InitializeAsync();
        await viewModel.RefreshAsync();
        await viewModel.NextDateCommand.ExecuteAsync(null);

        Assert.Equal(3, intervalRepository.RequestedRanges.Count);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero),
            intervalRepository.RequestedRanges[2].Start);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
            intervalRepository.RequestedRanges[2].End);
        Assert.Equal(
            UnprocessedIntervalState.Processing,
            Assert.Single(viewModel.UnprocessedIntervals).State);
    }

    [Fact]
    public async Task UnprocessedIntervalFailureDoesNotHideLoadedTimelineEntries()
    {
        var entry = CreateEntry();
        var timelineRepository = new StubTimelineRepository
        {
            Handler = (_, _) => Task.FromResult<IReadOnlyList<TimelineEntry>>([entry]),
        };
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Handler = (_, _) => throw new InvalidOperationException(
                "Sensitive database path."),
        };
        using var viewModel = CreateViewModel(timelineRepository, intervalRepository);

        await viewModel.InitializeAsync();

        Assert.Equal(entry.Id, Assert.Single(viewModel.Entries).Id);
        Assert.Empty(viewModel.UnprocessedIntervals);
        Assert.False(viewModel.HasError);
        Assert.True(viewModel.HasUnprocessedIntervalLoadError);
        Assert.Equal(
            "暂时无法读取录制处理状态。",
            viewModel.UnprocessedIntervalLoadErrorMessage);
    }

    [Fact]
    public async Task OlderIntervalResultCannotOverwriteANewerDate()
    {
        var oldResult = new TaskCompletionSource<IReadOnlyList<UnprocessedInterval>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = new TaskCompletionSource<IReadOnlyList<UnprocessedInterval>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Handler = (range, _) => range.Start
                == new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero)
                    ? oldResult.Task
                    : newResult.Task,
        };
        using var viewModel = CreateViewModel(
            new StubTimelineRepository(),
            intervalRepository);
        var oldLoad = viewModel.InitializeAsync();
        var newLoad = viewModel.NextDateCommand.ExecuteAsync(null);

        newResult.SetResult([CreateInterval(2, UnprocessedIntervalState.Processing)]);
        await newLoad;
        oldResult.SetResult([CreateInterval(1, UnprocessedIntervalState.Failed)]);
        await oldLoad;

        Assert.Equal(Today.AddDays(1), viewModel.SelectedDate);
        Assert.Equal(
            UnprocessedIntervalState.Processing,
            Assert.Single(viewModel.UnprocessedIntervals).State);
        Assert.False(viewModel.IsLoading);
    }

    private static TimelineViewModel CreateViewModel(
        ITimelineRepository timelineRepository,
        IUnprocessedIntervalRepository intervalRepository)
    {
        return new TimelineViewModel(
            new TimelineQueryService(timelineRepository),
            intervalRepository,
            new FixedTimeProvider(Now, ChinaTimeZone));
    }

    private static UnprocessedInterval CreateInterval(
        int minute,
        UnprocessedIntervalState state)
    {
        var start = new DateTimeOffset(2026, 7, 15, 9, minute, 0, TimeSpan.FromHours(8));
        var hasJob = state != UnprocessedIntervalState.LocalOnly;
        int? attempt = state switch
        {
            UnprocessedIntervalState.LocalOnly => null,
            UnprocessedIntervalState.Queued or UnprocessedIntervalState.Cancelled => 0,
            _ => 1,
        };
        var errorCode = state switch
        {
            UnprocessedIntervalState.RetryScheduled => AnalysisJobErrorCode.ProviderUnavailable,
            UnprocessedIntervalState.Failed => AnalysisJobErrorCode.ProviderRejected,
            _ => (AnalysisJobErrorCode?)null,
        };
        return new UnprocessedInterval(
            $"chunk-{minute}",
            new TimeRange(start, start.AddMinutes(1)),
            state,
            hasJob ? CreateGuid(minute) : null,
            attempt,
            errorCode);
    }

    private static TimelineEntry CreateEntry()
    {
        var start = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(8));
        var range = new TimeRange(start, start.AddMinutes(30));
        return new TimelineEntry(
            CreateGuid(42),
            range,
            "已分析活动",
            "时间线内容保持可见。",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            [new AppUsage("editor", "Editor", range.Duration)],
            [],
            0.9,
            new EvidenceReference("chunk-42", "test://evidence/42"),
            "test-v1");
    }

    private static Guid CreateGuid(int value) => new(value, 0, 0, new byte[8]);

    private sealed class StubTimelineRepository : ITimelineRepository
    {
        public Func<DateOnly, CancellationToken, Task<IReadOnlyList<TimelineEntry>>> Handler
        { get; init; } = (_, _) => Task.FromResult<IReadOnlyList<TimelineEntry>>([]);

        public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
            DateOnly day,
            CancellationToken cancellationToken = default)
        {
            return Handler(day, cancellationToken);
        }
    }

    private sealed class StubUnprocessedIntervalRepository : IUnprocessedIntervalRepository
    {
        public Func<TimeRange, CancellationToken, Task<IReadOnlyList<UnprocessedInterval>>> Handler
        { get; init; } = (_, _) => Task.FromResult<IReadOnlyList<UnprocessedInterval>>([]);

        public List<TimeRange> RequestedRanges { get; } = [];

        public Task<IReadOnlyList<UnprocessedInterval>> GetForUtcRangeAsync(
            TimeRange utcRange,
            CancellationToken cancellationToken = default)
        {
            RequestedRanges.Add(utcRange);
            return Handler(utcRange, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => localTimeZone;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
