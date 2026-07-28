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
            context.RunAll();
            await WaitUntilAsync(() => viewModel.Entries.Count == 1);

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

    [Fact]
    public async Task RejectedRetryShowsStableMutationError()
    {
        var timelineStore = new StubTimelineStore();
        var intervalRepository = new StubUnprocessedIntervalRepository
        {
            Current = [CreateInterval(UnprocessedIntervalState.Failed)],
        };
        var retryStore = new StubAnalysisJobStore
        {
            RetryResult = new AnalysisJobRetryResult(
                AnalysisJobRetryOutcome.StateNotRetryable,
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

        Assert.Equal(
            "无法重新安排分析，请稍后重试。",
            viewModel.MutationErrorMessage);
        Assert.Equal(0, scheduler.RequestCount);
        Assert.Equal(1, intervalRepository.RequestCount);
        Assert.False(viewModel.IsSaving);
    }

    private static TimelineViewModel CreateViewModel(
        StubTimelineStore timelineStore,
        StubUnprocessedIntervalRepository intervalRepository,
        StubAnalysisPipelineStatusSource statusSource,
        StubAnalysisJobStore? retryStore = null,
        StubAnalysisPipelineScheduler? scheduler = null)
    {
        var timeProvider = new FixedTimeProvider(Now);
        retryStore ??= new StubAnalysisJobStore();
        scheduler ??= new StubAnalysisPipelineScheduler();
        return new TimelineViewModel(
            new TimelineQueryService(timelineStore),
            new TimelineCommandService(timelineStore, timeProvider),
            intervalRepository,
            statusSource,
            new AnalysisJobRetryService(retryStore, scheduler, timeProvider),
            timeProvider);
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
        private AnalysisPipelineStatus _current = new(
            Sequence: 0,
            DataRevision: 0,
            AnalysisPipelineActivityState.Idle,
            Now,
            LastRunSummary: null,
            FaultCode: null);

        public int SubscriberCount { get; private set; }

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
            AnalysisPipelineStatus previous;
            AnalysisPipelineStatus current;
            EventHandler<AnalysisPipelineStatusChangedEventArgs>? handler;
            lock (_sync)
            {
                previous = _current;
                current = previous with
                {
                    Sequence = previous.Sequence + 1,
                    DataRevision = dataRevision,
                    ChangedAtUtc = Now.AddSeconds(previous.Sequence + 1),
                };
                _current = current;
                handler = _statusChanged;
            }

            handler?.Invoke(
                this,
                new AnalysisPipelineStatusChangedEventArgs(previous, current));
        }
    }

    private sealed class StubAnalysisPipelineScheduler
        : IAnalysisPipelineScheduler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void RequestRun()
        {
            Interlocked.Increment(ref _requestCount);
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
