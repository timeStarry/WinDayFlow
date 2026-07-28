using System.Collections.Concurrent;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using WinDayFlow.Presentation.Timeline;
using Xunit;

namespace WinDayFlow.Presentation.Tests;

public sealed class TimelineAnalysisRefreshTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 15);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CompletedAnalysisRefreshesEntriesAndIntervalsWithoutLoading()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Current = [CreateInterval(UnprocessedIntervalState.Processing)],
        };
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource);
        await viewModel.InitializeAsync();
        var loadingNotifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.IsLoading))
            {
                Interlocked.Increment(ref loadingNotifications);
            }
        };

        timelineStore.Current = [CreateEntry()];
        intervalRepository.Current = [];
        statusSource.PublishDataRevision(1);

        await WaitUntilAsync(() => viewModel.Entries.Count == 1);

        Assert.Equal("分析完成", Assert.Single(viewModel.Entries).Title);
        Assert.Empty(viewModel.UnprocessedIntervals);
        Assert.False(viewModel.IsLoading);
        Assert.Equal(0, Volatile.Read(ref loadingNotifications));
    }

    [Fact]
    public async Task StatusBurstIsDebouncedIntoOneSilentRefresh()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource);
        await viewModel.InitializeAsync();

        statusSource.PublishDataRevision(1);
        statusSource.PublishDataRevision(2);
        statusSource.PublishDataRevision(3);

        await WaitUntilAsync(() => timelineStore.RequestCount >= 2);
        await Task.Delay(350);

        Assert.Equal(2, timelineStore.RequestCount);
        Assert.Equal(2, intervalRepository.RequestCount);
    }

    [Fact]
    public async Task SilentRefreshUpdatesCollectionsOnCapturedSynchronizationContext()
    {
        var context = new QueuedSynchronizationContext();
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        var previousContext = SynchronizationContext.Current;
        TimelineViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = CreateViewModel(
                timelineStore,
                intervalRepository,
                statusSource);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            await viewModel.InitializeAsync();
            var changedOnCapturedContext = false;
            viewModel.Entries.CollectionChanged += (_, _) =>
            {
                changedOnCapturedContext = ReferenceEquals(
                    SynchronizationContext.Current,
                    context);
            };
            timelineStore.Current = [CreateEntry()];

            await Task.Run(() => statusSource.PublishDataRevision(1));
            await WaitUntilAsync(() => context.PendingCount > 0);

            Assert.Empty(viewModel.Entries);
            await RunContextUntilAsync(
                context,
                () => viewModel.Entries.Count == 1);

            Assert.True(changedOnCapturedContext);
        }
    }

    [Fact]
    public async Task ExplicitLoadDefersStatusDrivenRefresh()
    {
        var explicitLoad = new TaskCompletionSource<IReadOnlyList<TimelineEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource);
        await viewModel.InitializeAsync();
        timelineStore.Handler = (_, _) => explicitLoad.Task;

        var refresh = viewModel.RefreshAsync();
        await WaitUntilAsync(() => viewModel.IsLoading);
        statusSource.PublishDataRevision(1);
        await Task.Delay(350);

        Assert.Equal(2, timelineStore.RequestCount);

        timelineStore.Handler = (_, _) =>
            Task.FromResult<IReadOnlyList<TimelineEntry>>([CreateEntry()]);
        explicitLoad.SetResult([]);
        await refresh;
        await WaitUntilAsync(() => timelineStore.RequestCount == 3);

        Assert.False(viewModel.IsLoading);
        Assert.Equal("分析完成", Assert.Single(viewModel.Entries).Title);
    }

    [Fact]
    public async Task DisposeUnsubscribesAndCancelsPendingRefresh()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource);
        await viewModel.InitializeAsync();

        statusSource.PublishDataRevision(1);
        viewModel.Dispose();
        await Task.Delay(350);

        Assert.Equal(0, statusSource.SubscriberCount);
        Assert.Equal(1, timelineStore.RequestCount);
        Assert.Equal(1, intervalRepository.RequestCount);
    }

    [Fact]
    public void PipelineFaultIsVisibleAndCanRequestAnImmediateRun()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        var scheduler = new StubAnalysisPipelineScheduler();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource,
            scheduler: scheduler);

        statusSource.PublishStatus(
            AnalysisPipelineActivityState.Faulted,
            AnalysisPipelineFaultCode.PipelineRunFailed);

        Assert.True(viewModel.HasAnalysisPipelineStatus);
        Assert.True(viewModel.HasAnalysisPipelineFault);
        Assert.False(viewModel.IsAnalysisPipelineRunning);
        Assert.Equal("后台分析暂时不可用", viewModel.AnalysisPipelineStatusTitle);
        Assert.Contains("现有录制和时间线数据未丢失", viewModel.AnalysisPipelineStatusText);
        Assert.True(viewModel.RetryAnalysisPipelineCommand.CanExecute(null));

        viewModel.RetryAnalysisPipelineCommand.Execute(null);

        Assert.Equal(1, scheduler.RequestCount);
    }

    [Fact]
    public void ConstructorResnapshotsPipelineStatusAfterSubscribing()
    {
        var statusSource = new StubAnalysisPipelineStatusSource
        {
            TransitionToFaultWhenSubscriberIsAdded = true,
        };

        using var viewModel = CreateViewModel(
            new StubTimelineStore(),
            new StubUnprocessedIntervalRepository(),
            statusSource);

        Assert.True(viewModel.HasAnalysisPipelineFault);
        Assert.Equal("后台分析暂时不可用", viewModel.AnalysisPipelineStatusTitle);
    }

    [Fact]
    public async Task StatusOnlyChangeDoesNotReloadTimelineRepositories()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource);
        await viewModel.InitializeAsync();

        statusSource.PublishStatus(AnalysisPipelineActivityState.Running);
        await Task.Delay(350);

        Assert.True(viewModel.IsAnalysisPipelineRunning);
        Assert.Equal("分析中", viewModel.AnalysisPipelineCompactStatusText);
        Assert.Equal(1, timelineStore.RequestCount);
        Assert.Equal(1, intervalRepository.RequestCount);
    }

    [Fact]
    public void StalePipelineNotificationCannotOverwriteNewerState()
    {
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            new StubTimelineStore(),
            new StubUnprocessedIntervalRepository(),
            statusSource);

        var stale = statusSource.PublishStatus(
            AnalysisPipelineActivityState.Faulted,
            AnalysisPipelineFaultCode.PipelineRunFailed);
        statusSource.PublishStatus(AnalysisPipelineActivityState.Running);
        statusSource.PublishNotification(stale);

        Assert.True(viewModel.IsAnalysisPipelineRunning);
        Assert.False(viewModel.HasAnalysisPipelineFault);
        Assert.Equal("分析中", viewModel.AnalysisPipelineCompactStatusText);
    }

    [Fact]
    public void PipelineStatusDistinguishesWaitingFailuresAndRecovery()
    {
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            new StubTimelineStore(),
            new StubUnprocessedIntervalRepository(),
            statusSource);

        Assert.Equal("正在检查分析状态", viewModel.AnalysisPipelineStatusTitle);
        Assert.Equal("检查中", viewModel.AnalysisPipelineCompactStatusText);

        statusSource.PublishStatus(
            AnalysisPipelineActivityState.Idle,
            summary: CreateRunSummary(
                scannedChunkCount: 3,
                analysisReady: false));

        Assert.Equal("录制内容等待分析", viewModel.AnalysisPipelineStatusTitle);
        Assert.Equal("等待分析", viewModel.AnalysisPipelineCompactStatusText);
        Assert.Contains("3 个本地录制分片", viewModel.AnalysisPipelineStatusText);
        Assert.Contains("不会发送", viewModel.AnalysisPipelineStatusText);
        Assert.DoesNotContain("分析服务已就绪", viewModel.AnalysisPipelineStatusText);
        Assert.False(viewModel.HasAnalysisPipelineWarning);

        statusSource.PublishStatus(
            AnalysisPipelineActivityState.Idle,
            summary: CreateRunSummary(
                scannedChunkCount: 3,
                analysisReady: true,
                completedJobCount: 1,
                retryableFailureCount: 2,
                terminalFailureCount: 1));

        Assert.True(viewModel.HasAnalysisPipelineWarning);
        Assert.False(viewModel.HasSuccessfulAnalysisPipelineRun);
        Assert.Equal(
            "最近一次后台分析有未完成内容",
            viewModel.AnalysisPipelineStatusTitle);
        Assert.Contains("1 个录制块已完成", viewModel.AnalysisPipelineStatusText);
        Assert.Contains("2 个等待重试", viewModel.AnalysisPipelineStatusText);
        Assert.Contains("1 个分析未完成", viewModel.AnalysisPipelineStatusText);

        statusSource.PublishStatus(
            AnalysisPipelineActivityState.Idle,
            summary: CreateRunSummary(
                scannedChunkCount: 3,
                analysisReady: true,
                completedJobCount: 2));

        Assert.False(viewModel.HasAnalysisPipelineFault);
        Assert.False(viewModel.HasAnalysisPipelineWarning);
        Assert.True(viewModel.HasSuccessfulAnalysisPipelineRun);
        Assert.Equal("最近一次后台分析已完成", viewModel.AnalysisPipelineStatusTitle);
        Assert.False(viewModel.RetryAnalysisPipelineCommand.CanExecute(null));
    }

    [Fact]
    public void PipelineSummaryUsesTheInjectedLocalTimeZone()
    {
        var localTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "timeline-test-utc-plus-eight",
            TimeSpan.FromHours(8),
            "UTC+08",
            "UTC+08");
        var timeProvider = new FixedTimeProvider(Now, localTimeZone);
        var statusSource = new StubAnalysisPipelineStatusSource();
        using var viewModel = CreateViewModel(
            new StubTimelineStore(),
            new StubUnprocessedIntervalRepository(),
            statusSource,
            timeProvider: timeProvider);

        var status = statusSource.PublishStatus(
            AnalysisPipelineActivityState.Idle,
            summary: CreateRunSummary(
                scannedChunkCount: 0,
                analysisReady: true));
        var expectedTime = TimeZoneInfo
            .ConvertTime(status.ChangedAtUtc, localTimeZone)
            .ToString("t", System.Globalization.CultureInfo.CurrentCulture);

        Assert.Contains(expectedTime, viewModel.AnalysisPipelineStatusText);
    }

    [Fact]
    public async Task PipelineStatusUsesTheCapturedSynchronizationContext()
    {
        var statusSource = new StubAnalysisPipelineStatusSource();
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        TimelineViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            viewModel = CreateViewModel(
                new StubTimelineStore(),
                new StubUnprocessedIntervalRepository(),
                statusSource);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            await Task.Run(() => statusSource.PublishStatus(
                AnalysisPipelineActivityState.Faulted,
                AnalysisPipelineFaultCode.SchedulerFailed));

            Assert.False(viewModel.HasAnalysisPipelineFault);
            Assert.True(context.PendingCount > 0);

            context.RunAll();

            Assert.True(viewModel.HasAnalysisPipelineFault);
            Assert.Contains("后台分析调度", viewModel.AnalysisPipelineStatusText);
        }
    }

    [Fact]
    public void PipelineRetryFailureUsesAStableUserFacingError()
    {
        var statusSource = new StubAnalysisPipelineStatusSource();
        var scheduler = new StubAnalysisPipelineScheduler
        {
            Failure = new InvalidOperationException("scheduler-test"),
        };
        using var viewModel = CreateViewModel(
            new StubTimelineStore(),
            new StubUnprocessedIntervalRepository(),
            statusSource,
            scheduler: scheduler);
        statusSource.PublishStatus(
            AnalysisPipelineActivityState.Faulted,
            AnalysisPipelineFaultCode.SchedulerFailed);

        viewModel.RetryAnalysisPipelineCommand.Execute(null);

        Assert.Equal(1, scheduler.RequestCount);
        Assert.Equal(
            "无法立即重试后台分析，请稍后再试。",
            viewModel.MutationErrorMessage);

        scheduler.Failure = null;
        viewModel.RetryAnalysisPipelineCommand.Execute(null);

        Assert.Equal(2, scheduler.RequestCount);
        Assert.False(viewModel.HasMutationError);
    }

    [Fact]
    public async Task RetryCommandSchedulesJobAndRefreshesIntervalImmediately()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Current = [CreateInterval(UnprocessedIntervalState.Failed)],
        };
        var statusSource = new StubAnalysisPipelineStatusSource();
        var retryStore = new StubAnalysisJobStore
        {
            RetryResult = new AnalysisJobRetryResult(
                AnalysisJobRetryOutcome.Scheduled,
                Job: null),
        };
        retryStore.OnRetry = () => intervalRepository.Current =
            [CreateInterval(UnprocessedIntervalState.RetryScheduled)];
        var scheduler = new StubAnalysisPipelineScheduler();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource,
            retryStore,
            scheduler);
        await viewModel.InitializeAsync();
        var failed = Assert.Single(viewModel.UnprocessedIntervals);

        Assert.True(viewModel.RetryAnalysisCommand.CanExecute(failed));
        await viewModel.RetryAnalysisCommand.ExecuteAsync(failed);

        Assert.Equal(failed.LatestJobId, retryStore.RequestedJobId);
        Assert.Equal(1, scheduler.RequestCount);
        Assert.Equal(2, intervalRepository.RequestCount);
        Assert.Equal(
            UnprocessedIntervalState.RetryScheduled,
            Assert.Single(viewModel.UnprocessedIntervals).State);
        Assert.False(viewModel.HasMutationError);
        Assert.False(viewModel.IsSaving);
    }

    [Fact]
    public async Task RetryRefreshSupersedesAnOlderBlockedStatusRefresh()
    {
        var staleEntries = new TaskCompletionSource<IReadOnlyList<TimelineEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var staleQueryCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntry = CreateEntry("重试后的最新结果");
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Current = [CreateInterval(UnprocessedIntervalState.Failed)],
        };
        var statusSource = new StubAnalysisPipelineStatusSource();
        var retryStore = new StubAnalysisJobStore
        {
            RetryResult = new AnalysisJobRetryResult(
                AnalysisJobRetryOutcome.Scheduled,
                Job: null),
        };
        retryStore.OnRetry = () => intervalRepository.Current =
            [CreateInterval(UnprocessedIntervalState.RetryScheduled)];
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource,
            retryStore);
        await viewModel.InitializeAsync();
        timelineStore.Handler = async (_, _) =>
        {
            if (timelineStore.RequestCount != 2)
            {
                return [latestEntry];
            }

            var entries = await staleEntries.Task.ConfigureAwait(false);
            staleQueryCompleted.TrySetResult();
            return entries;
        };

        statusSource.PublishDataRevision(1);
        await WaitUntilAsync(() => timelineStore.RequestCount == 2);
        var failed = Assert.Single(viewModel.UnprocessedIntervals);

        await viewModel.RetryAnalysisCommand.ExecuteAsync(failed);

        Assert.Equal(
            latestEntry.Id,
            Assert.Single(viewModel.Entries).Id);
        Assert.Equal(
            UnprocessedIntervalState.RetryScheduled,
            Assert.Single(viewModel.UnprocessedIntervals).State);

        staleEntries.TrySetResult([]);
        await staleQueryCompleted.Task.WaitAsync(Timeout);
        await Task.Delay(100);

        Assert.Equal(
            latestEntry.Id,
            Assert.Single(viewModel.Entries).Id);
        Assert.Equal(
            UnprocessedIntervalState.RetryScheduled,
            Assert.Single(viewModel.UnprocessedIntervals).State);
    }

    [Fact]
    public async Task ConcurrentRefreshRequestsAndDisposeDoNotLeakCancellationFailures()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository();
        var statusSource = new StubAnalysisPipelineStatusSource();
        var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            statusSource);
        await viewModel.InitializeAsync();
        var failures = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(initialState: false);
        var requests = Enumerable.Range(1, 32)
            .Select(revision => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    statusSource.PublishDataRevision(revision);
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }))
            .Append(Task.Run(() =>
            {
                start.Wait();
                try
                {
                    viewModel.Dispose();
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(requests);
        await Task.Delay(300);

        Assert.Empty(failures);
        Assert.Equal(0, statusSource.SubscriberCount);
    }

    [Theory]
    [InlineData(AnalysisJobRetryOutcome.NotFound)]
    [InlineData(AnalysisJobRetryOutcome.StateNotRetryable)]
    [InlineData(AnalysisJobRetryOutcome.StaleJob)]
    [InlineData(AnalysisJobRetryOutcome.AnalysisAlreadyCompleted)]
    public async Task StaleRetryOutcomeRefreshesWithoutReportingSystemFailure(
        AnalysisJobRetryOutcome outcome)
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Current = [CreateInterval(UnprocessedIntervalState.Failed)],
        };
        var retryStore = new StubAnalysisJobStore
        {
            RetryResult = new AnalysisJobRetryResult(
                outcome,
                Job: null),
        };
        var scheduler = new StubAnalysisPipelineScheduler();
        using var viewModel = CreateViewModel(
            timelineStore,
            intervalRepository,
            new StubAnalysisPipelineStatusSource(),
            retryStore,
            scheduler);
        await viewModel.InitializeAsync();

        await viewModel.RetryAnalysisCommand.ExecuteAsync(
            Assert.Single(viewModel.UnprocessedIntervals));

        Assert.False(viewModel.HasMutationError);
        Assert.Equal(0, scheduler.RequestCount);
        Assert.Equal(2, intervalRepository.RequestCount);
        Assert.False(viewModel.IsSaving);
    }

    [Theory]
    [InlineData(
        AnalysisJobRetryOutcome.EvidenceUnavailable,
        "本地录制证据已不可用，无法重试分析。")]
    [InlineData(
        AnalysisJobRetryOutcome.AttemptLimitReached,
        "此录制内容已达到重试次数上限。")]
    [InlineData((AnalysisJobRetryOutcome)999, "无法重新安排分析，请稍后重试。")]
    public async Task RetryRejectionReportsTheSpecificStableReason(
        AnalysisJobRetryOutcome outcome,
        string expectedMessage)
    {
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Current = [CreateInterval(UnprocessedIntervalState.Failed)],
        };
        var retryStore = new StubAnalysisJobStore
        {
            RetryResult = new AnalysisJobRetryResult(outcome, Job: null),
        };
        var scheduler = new StubAnalysisPipelineScheduler();
        using var viewModel = CreateViewModel(
            new StubTimelineStore(),
            intervalRepository,
            new StubAnalysisPipelineStatusSource(),
            retryStore,
            scheduler);
        await viewModel.InitializeAsync();

        await viewModel.RetryAnalysisCommand.ExecuteAsync(
            Assert.Single(viewModel.UnprocessedIntervals));

        Assert.Equal(expectedMessage, viewModel.MutationErrorMessage);
        Assert.Equal(0, scheduler.RequestCount);
        Assert.Equal(2, intervalRepository.RequestCount);
        Assert.False(viewModel.IsSaving);
    }

    private static TimelineViewModel CreateViewModel(
        StubTimelineStore timelineStore,
        StubUnprocessedIntervalRepository intervalRepository,
        StubAnalysisPipelineStatusSource statusSource,
        StubAnalysisJobStore? retryStore = null,
        StubAnalysisPipelineScheduler? scheduler = null,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= new FixedTimeProvider(Now);
        retryStore ??= new StubAnalysisJobStore();
        scheduler ??= new StubAnalysisPipelineScheduler();
        return new TimelineViewModel(
            new TimelineQueryService(timelineStore),
            new TimelineCommandService(timelineStore, timeProvider),
            intervalRepository,
            statusSource,
            new AnalysisJobRetryService(retryStore, scheduler, timeProvider),
            scheduler,
            timeProvider);
    }

    private static AnalysisPipelineRunSummary CreateRunSummary(
        int scannedChunkCount,
        bool analysisReady,
        int completedJobCount = 0,
        int retryableFailureCount = 0,
        int terminalFailureCount = 0)
    {
        return new AnalysisPipelineRunSummary(
            RecoveredLeaseCount: 0,
            new CaptureAnalysisIngestionResult(
                scannedChunkCount,
                CreatedChunkCount: 0,
                CreatedJobCount: 0,
                analysisReady),
            ProcessedJobCount: completedJobCount
                + retryableFailureCount
                + terminalFailureCount,
            completedJobCount,
            retryableFailureCount,
            terminalFailureCount,
            LeaseLostCount: 0,
            MoreWorkPossible: false);
    }

    private static UnprocessedInterval CreateInterval(
        UnprocessedIntervalState state)
    {
        var start = new DateTimeOffset(
            2026,
            7,
            15,
            9,
            0,
            0,
            TimeSpan.Zero);
        return new UnprocessedInterval(
            "chunk-refresh",
            new TimeRange(start, start.AddMinutes(1)),
            state,
            CreateGuid(7),
            attempt: 1,
            state is UnprocessedIntervalState.Failed
                or UnprocessedIntervalState.RetryScheduled
                    ? AnalysisJobErrorCode.ProviderUnavailable
                    : null);
    }

    private static TimelineEntry CreateEntry(string title = "分析完成")
    {
        var range = new TimeRange(Now, Now.AddMinutes(30));
        return new TimelineEntry(
            CreateGuid(42),
            range,
            title,
            "后台分析结果已写入时间线。",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            [new AppUsage("editor", "Editor", range.Duration)],
            [],
            0.9,
            new EvidenceReference("chunk-refresh", "test://evidence/refresh"),
            "test-v1");
    }

    private static Guid CreateGuid(int value) => new(value, 0, 0, new byte[8]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected timeline state was not observed.");
            }

            await Task.Delay(20);
        }
    }

    private static async Task RunContextUntilAsync(
        QueuedSynchronizationContext context,
        Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            context.RunAll();
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The expected synchronization-context state was not observed.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class StubTimelineStore : ITimelineStore
    {
        private int _requestCount;

        public IReadOnlyList<TimelineEntry> Current { get; set; } = [];

        public Func<DateOnly, CancellationToken, Task<IReadOnlyList<TimelineEntry>>>?
            Handler
        {
            get;
            set;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
            DateOnly day,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            return Handler?.Invoke(day, cancellationToken)
                ?? Task.FromResult(Current);
        }

        public Task<TimelineEntry?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TimelineEntry?>(null);

        public Task AddAsync(
            TimelineEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            TimelineEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubUnprocessedIntervalRepository
        : IUnprocessedIntervalRepository
    {
        private int _requestCount;

        public IReadOnlyList<UnprocessedInterval> Current { get; set; } = [];

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<IReadOnlyList<UnprocessedInterval>> GetForUtcRangeAsync(
            TimeRange utcRange,
            CancellationToken cancellationToken = default)
        {
            _ = utcRange;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(Current);
        }
    }

    private sealed class StubAnalysisPipelineStatusSource
        : IAnalysisPipelineStatusSource
    {
        private readonly object _sync = new();
        private EventHandler<AnalysisPipelineStatusChangedEventArgs>? _statusChanged;
        private bool _subscriberTransitionApplied;
        private AnalysisPipelineStatus _current = new(
            Sequence: 0,
            DataRevision: 0,
            AnalysisPipelineActivityState.Idle,
            Now,
            LastRunSummary: null,
            FaultCode: null);

        public int SubscriberCount { get; private set; }

        public bool TransitionToFaultWhenSubscriberIsAdded { get; init; }

        public AnalysisPipelineStatus Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public event EventHandler<AnalysisPipelineStatusChangedEventArgs>? StatusChanged
        {
            add
            {
                lock (_sync)
                {
                    if (TransitionToFaultWhenSubscriberIsAdded
                        && !_subscriberTransitionApplied)
                    {
                        _subscriberTransitionApplied = true;
                        _current = _current with
                        {
                            Sequence = _current.Sequence + 1,
                            State = AnalysisPipelineActivityState.Faulted,
                            FaultCode = AnalysisPipelineFaultCode.PipelineRunFailed,
                        };
                    }

                    _statusChanged += value;
                    SubscriberCount++;
                }
            }
            remove
            {
                lock (_sync)
                {
                    _statusChanged -= value;
                    SubscriberCount--;
                }
            }
        }

        public void PublishDataRevision(long dataRevision)
        {
            var current = Current;
            PublishStatus(
                current.State,
                current.FaultCode,
                current.LastRunSummary,
                dataRevision);
        }

        public AnalysisPipelineStatus PublishStatus(
            AnalysisPipelineActivityState state,
            AnalysisPipelineFaultCode? faultCode = null,
            AnalysisPipelineRunSummary? summary = null,
            long? dataRevision = null)
        {
            AnalysisPipelineStatus previous;
            AnalysisPipelineStatus current;
            EventHandler<AnalysisPipelineStatusChangedEventArgs>? handler;
            lock (_sync)
            {
                previous = _current;
                current = previous with
                {
                    Sequence = previous.Sequence + 1,
                    DataRevision = dataRevision ?? previous.DataRevision,
                    State = state,
                    ChangedAtUtc = Now.AddSeconds(previous.Sequence + 1),
                    LastRunSummary = summary ?? previous.LastRunSummary,
                    FaultCode = faultCode,
                };
                _current = current;
                handler = _statusChanged;
            }

            handler?.Invoke(
                this,
                new AnalysisPipelineStatusChangedEventArgs(previous, current));
            return current;
        }

        public void PublishNotification(AnalysisPipelineStatus status)
        {
            EventHandler<AnalysisPipelineStatusChangedEventArgs>? handler;
            AnalysisPipelineStatus current;
            lock (_sync)
            {
                current = _current;
                handler = _statusChanged;
            }

            handler?.Invoke(
                this,
                new AnalysisPipelineStatusChangedEventArgs(current, status));
        }
    }

    private sealed class StubAnalysisPipelineScheduler
        : IAnalysisPipelineScheduler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Exception? Failure { get; set; }

        public void RequestRun()
        {
            Interlocked.Increment(ref _requestCount);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class StubAnalysisJobStore : IAnalysisJobStore
    {
        public AnalysisJobRetryResult RetryResult { get; init; } =
            new(AnalysisJobRetryOutcome.StateNotRetryable, Job: null);

        public Action? OnRetry { get; set; }

        public Guid? RequestedJobId { get; private set; }

        public Task<AnalysisJobRetryResult> TryRetryAsync(
            Guid jobId,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _ = requestedAtUtc;
            cancellationToken.ThrowIfCancellationRequested();
            RequestedJobId = jobId;
            OnRetry?.Invoke();
            return Task.FromResult(RetryResult);
        }

        public Task<AnalysisJobEnqueueResult> EnqueueAsync(
            AnalysisJob pendingJob,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> GetAsync(
            Guid jobId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HasCompletedAnalysisAsync(
            string captureChunkId,
            string analysisVersion,
            string inputFingerprint,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> TryClaimNextAsync(
            string leaseOwner,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> TryTransitionAsync(
            AnalysisJobLease lease,
            AnalysisJobState expectedState,
            AnalysisJobState nextState,
            DateTimeOffset changedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> TryRenewLeaseAsync(
            AnalysisJobLease lease,
            DateTimeOffset renewedAtUtc,
            DateTimeOffset newExpiresAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> TryFailAsync(
            AnalysisJobLease lease,
            AnalysisJobFailure failure,
            AnalysisFailureDisposition disposition,
            DateTimeOffset failedAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisJob?> TryCancelAsync(
            Guid jobId,
            DateTimeOffset cancelledAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RecoverExpiredLeasesAsync(
            DateTimeOffset recoveredAtUtc,
            TimeSpan retryDelay,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)>
            _callbacks = new();

        public int PendingCount => _callbacks.Count;

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
        }

        public void RunAll()
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (_callbacks.TryDequeue(out var callback))
                {
                    callback.Callback(callback.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo? localTimeZone = null) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone =>
            localTimeZone ?? TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
