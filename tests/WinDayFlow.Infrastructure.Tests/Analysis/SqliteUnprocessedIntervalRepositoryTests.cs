using System.Globalization;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Persistence;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Analysis;

public sealed class SqliteUnprocessedIntervalRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProviderId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task ProjectsLocalQueuedActiveRetryFailedAndCancelledStates()
    {
        using var context = await TestContext.CreateAsync();
        var local = await context.AddChunkAsync("chunk-state-local", minute: 0);

        var active = await context.AddChunkAsync("chunk-state-processing", minute: 1);
        var activeJob = await context.EnqueueAsync(active, "processing-v1");
        var activeClaim = await context.ClaimExpectedAsync(activeJob.Id);
        _ = await context.Store.TryTransitionAsync(
            activeClaim.Lease!,
            AnalysisJobState.Claimed,
            AnalysisJobState.Extracting,
            Now.AddSeconds(2));

        var retry = await context.AddChunkAsync("chunk-state-retry", minute: 2);
        var retryJob = await context.EnqueueAsync(retry, "retry-v1");
        var retryClaim = await context.ClaimExpectedAsync(retryJob.Id);
        _ = await context.Store.TryFailAsync(
            retryClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderUnavailable),
            AnalysisFailureDisposition.Retryable,
            Now.AddSeconds(2),
            TimeSpan.FromMinutes(10));

        var failed = await context.AddChunkAsync("chunk-state-failed", minute: 3);
        var failedJob = await context.EnqueueAsync(failed, "failed-v1");
        var failedClaim = await context.ClaimExpectedAsync(failedJob.Id);
        _ = await context.Store.TryFailAsync(
            failedClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderRejected),
            AnalysisFailureDisposition.Terminal,
            Now.AddSeconds(2),
            TimeSpan.Zero);

        var cancelled = await context.AddChunkAsync("chunk-state-cancelled", minute: 4);
        var cancelledJob = await context.EnqueueAsync(cancelled, "cancelled-v1");
        _ = await context.Store.TryCancelAsync(cancelledJob.Id, Now.AddSeconds(2));

        var queued = await context.AddChunkAsync("chunk-state-queued", minute: 5);
        var queuedJob = await context.EnqueueAsync(queued, "queued-v1");

        var intervals = await context.Repository.GetForUtcRangeAsync(
            new TimeRange(Now.AddMinutes(-1), Now.AddMinutes(10)));

        Assert.Equal(6, intervals.Count);
        var byChunk = intervals.ToDictionary(static interval => interval.CaptureChunkId);
        AssertInterval(
            byChunk[local.Id],
            UnprocessedIntervalState.LocalOnly,
            latestJobId: null,
            attempt: null,
            errorCode: null);
        AssertInterval(
            byChunk[active.Id],
            UnprocessedIntervalState.Processing,
            activeJob.Id,
            attempt: 1,
            errorCode: null);
        AssertInterval(
            byChunk[retry.Id],
            UnprocessedIntervalState.RetryScheduled,
            retryJob.Id,
            attempt: 1,
            AnalysisJobErrorCode.ProviderUnavailable);
        AssertInterval(
            byChunk[failed.Id],
            UnprocessedIntervalState.Failed,
            failedJob.Id,
            attempt: 1,
            AnalysisJobErrorCode.ProviderRejected);
        AssertInterval(
            byChunk[cancelled.Id],
            UnprocessedIntervalState.Cancelled,
            cancelledJob.Id,
            attempt: 0,
            errorCode: null);
        AssertInterval(
            byChunk[queued.Id],
            UnprocessedIntervalState.Queued,
            queuedJob.Id,
            attempt: 0,
            errorCode: null);
    }

    [Fact]
    public async Task AnyCompletedJobHidesChunkIncludingAfterAnOlderFailure()
    {
        using var context = await TestContext.CreateAsync();
        var completed = await context.AddChunkAsync("chunk-completed-empty", minute: 0);
        var completedJob = await context.EnqueueAsync(completed, "completed-v1");
        await context.CompleteAsync(await context.ClaimExpectedAsync(completedJob.Id));

        var recovered = await context.AddChunkAsync("chunk-failed-then-completed", minute: 1);
        var failedJob = await context.EnqueueAsync(recovered, "failed-v1");
        var failedClaim = await context.ClaimExpectedAsync(failedJob.Id);
        _ = await context.Store.TryFailAsync(
            failedClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderResponseInvalid),
            AnalysisFailureDisposition.Terminal,
            Now.AddSeconds(2),
            TimeSpan.Zero);
        var newerJob = await context.EnqueueAsync(
            recovered,
            "completed-v2",
            providerRevision: 2,
            createdAt: Now.AddMinutes(1));
        await context.CompleteAsync(await context.ClaimExpectedAsync(
            newerJob.Id,
            Now.AddMinutes(1)));

        var intervals = await context.Repository.GetForUtcRangeAsync(
            new TimeRange(Now.AddMinutes(-1), Now.AddMinutes(10)));

        Assert.Empty(intervals);
    }

    [Fact]
    public async Task LatestJobTieBreaksByRevisionThenIdAndResultsPreserveOffsetAndOrder()
    {
        using var context = await TestContext.CreateAsync();
        var sameStart = Now.ToOffset(TimeSpan.FromHours(8));
        var second = await context.AddChunkAsync("chunk-sort-b", sameStart);
        var first = await context.AddChunkAsync("chunk-sort-a", sameStart);
        var latest = await context.AddChunkAsync(
            "chunk-sort-latest",
            sameStart.AddMinutes(1));

        var oldRevision = await context.EnqueueAsync(
            latest,
            "revision-v1",
            providerRevision: 1,
            createdAt: Now);
        var oldClaim = await context.ClaimExpectedAsync(oldRevision.Id);
        _ = await context.Store.TryFailAsync(
            oldClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderRejected),
            AnalysisFailureDisposition.Terminal,
            Now.AddSeconds(2),
            TimeSpan.Zero);
        var latestByRevision = await context.EnqueueAsync(
            latest,
            "revision-v2",
            providerRevision: 2,
            createdAt: Now,
            jobId: Guid.Parse("10000000-0000-0000-0000-000000000001"));
        _ = await context.Store.TryCancelAsync(latestByRevision.Id, Now.AddSeconds(3));

        var latestByIdChunk = await context.AddChunkAsync(
            "chunk-sort-id",
            sameStart.AddMinutes(2));
        var lowerId = await context.EnqueueAsync(
            latestByIdChunk,
            "id-lower",
            providerRevision: 1,
            createdAt: Now,
            jobId: Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var lowerClaim = await context.ClaimExpectedAsync(lowerId.Id);
        _ = await context.Store.TryFailAsync(
            lowerClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderRejected),
            AnalysisFailureDisposition.Terminal,
            Now.AddSeconds(2),
            TimeSpan.Zero);
        var higherId = await context.EnqueueAsync(
            latestByIdChunk,
            "id-higher",
            providerRevision: 1,
            createdAt: Now,
            jobId: Guid.Parse("f0000000-0000-0000-0000-000000000001"));
        _ = await context.Store.TryCancelAsync(higherId.Id, Now.AddSeconds(3));

        _ = await context.AddChunkAsync(
            "chunk-outside-before",
            sameStart.AddMinutes(-2));
        _ = await context.AddChunkAsync(
            "chunk-outside-after",
            sameStart.AddMinutes(5));

        var intervals = await context.Repository.GetForUtcRangeAsync(
            new TimeRange(Now, Now.AddMinutes(5)));

        Assert.Equal(
            [first.Id, second.Id, latest.Id, latestByIdChunk.Id],
            intervals.Select(static interval => interval.CaptureChunkId));
        Assert.Equal(TimeSpan.FromHours(8), intervals[0].Range.Start.Offset);
        Assert.Equal(first.Range.Start, intervals[0].Range.Start);
        Assert.Equal(latestByRevision.Id, intervals[2].LatestJobId);
        Assert.Equal(UnprocessedIntervalState.Cancelled, intervals[2].State);
        Assert.Equal(higherId.Id, intervals[3].LatestJobId);
        Assert.Equal(UnprocessedIntervalState.Cancelled, intervals[3].State);
    }

    private static void AssertInterval(
        UnprocessedInterval interval,
        UnprocessedIntervalState state,
        Guid? latestJobId,
        int? attempt,
        AnalysisJobErrorCode? errorCode)
    {
        Assert.Equal(state, interval.State);
        Assert.Equal(latestJobId, interval.LatestJobId);
        Assert.Equal(attempt, interval.Attempt);
        Assert.Equal(errorCode, interval.ErrorCode);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TemporaryDatabase _database;
        private int _nextJobId = 1;

        private TestContext(
            TemporaryDatabase database,
            SqliteCaptureAnalysisStore store,
            SqliteUnprocessedIntervalRepository repository)
        {
            _database = database;
            Store = store;
            Repository = repository;
        }

        public SqliteCaptureAnalysisStore Store { get; }

        public SqliteUnprocessedIntervalRepository Repository { get; }

        public static async Task<TestContext> CreateAsync()
        {
            var database = new TemporaryDatabase();
            try
            {
                var factory = new SqliteConnectionFactory(database.DatabasePath);
                await new SqliteDatabaseInitializer(factory).InitializeAsync();
                return new TestContext(
                    database,
                    new SqliteCaptureAnalysisStore(factory, database.EvidenceRoot),
                    new SqliteUnprocessedIntervalRepository(factory));
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        public Task<CaptureChunk> AddChunkAsync(string id, int minute)
        {
            return AddChunkAsync(id, Now.AddMinutes(minute));
        }

        public async Task<CaptureChunk> AddChunkAsync(string id, DateTimeOffset start)
        {
            var chunk = new CaptureChunk(
                id,
                new EvidenceRelativePath($"chunks/{id}/capture.mp4"),
                new EvidenceRelativePath($"chunks/{id}/manifest.json"),
                new TimeRange(start, start.AddMinutes(1)),
                frameCount: 6,
                videoWidth: 1920,
                videoHeight: 1080,
                frameRateNumerator: 1,
                frameRateDenominator: 10,
                videoByteCount: 4_096,
                persistenceGeneration: 1,
                targetEpoch: 2,
                committedAtUtc: start.AddMinutes(1),
                ingestedAtUtc: start.AddMinutes(2));
            await Store.IngestCommittedAsync(chunk);
            return chunk;
        }

        public async Task<AnalysisJob> EnqueueAsync(
            CaptureChunk chunk,
            string analysisVersion,
            long providerRevision = 1,
            DateTimeOffset? createdAt = null,
            Guid? jobId = null)
        {
            var id = jobId ?? Guid.Parse(
                $"00000000-0000-0000-0000-{(_nextJobId++).ToString("D12", CultureInfo.InvariantCulture)}");
            var job = AnalysisJob.CreatePending(
                id,
                chunk.Id,
                ProviderId,
                providerRevision,
                analysisVersion,
                new string('A', 64),
                maxAttempts: 3,
                createdAt ?? Now);
            return (await Store.EnqueueAsync(job)).Job;
        }

        public async Task<AnalysisJob> ClaimExpectedAsync(
            Guid expectedJobId,
            DateTimeOffset? claimedAt = null)
        {
            var claimTime = claimedAt ?? Now.AddSeconds(1);
            var claimed = await Store.TryClaimNextAsync(
                "unprocessed-tests",
                claimTime,
                TimeSpan.FromMinutes(10));
            Assert.NotNull(claimed);
            Assert.Equal(expectedJobId, claimed.Id);
            return claimed;
        }

        public async Task CompleteAsync(AnalysisJob claimed)
        {
            var current = claimed;
            var transitions = new[]
            {
                (AnalysisJobState.Claimed, AnalysisJobState.Extracting),
                (AnalysisJobState.Extracting, AnalysisJobState.Observing),
                (AnalysisJobState.Observing, AnalysisJobState.Summarizing),
                (AnalysisJobState.Summarizing, AnalysisJobState.Committing),
                (AnalysisJobState.Committing, AnalysisJobState.Completed),
            };
            for (var index = 0; index < transitions.Length; index++)
            {
                var (expected, next) = transitions[index];
                current = await Store.TryTransitionAsync(
                    current.Lease!,
                    expected,
                    next,
                    claimed.UpdatedAtUtc.AddSeconds(index + 1))
                    ?? throw new InvalidOperationException("The test job did not advance.");
            }
        }

        public void Dispose() => _database.Dispose();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.UnprocessedIntervals.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_root, "data", "windayflow.db");

        public string EvidenceRoot => Path.Combine(_root, "evidence");

        public void Dispose()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
