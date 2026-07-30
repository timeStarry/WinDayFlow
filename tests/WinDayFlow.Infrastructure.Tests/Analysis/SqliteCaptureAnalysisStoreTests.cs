using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Persistence;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Analysis;

public sealed class SqliteCaptureAnalysisStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);

    private static readonly Guid ProviderId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task VersionFiveMigrationCreatesDurableQueueTablesOnce()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM schema_migrations WHERE version = 5),
                (SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name = 'capture_chunks'),
                (SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name = 'analysis_jobs');
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task VersionNineBackfillsLegacyJobsWithTheirAnchorWindowMember()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-v9-backfill");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("09000000-0000-0000-0000-000000000001"),
            chunk.Id);
        await store.EnqueueAsync(job);

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TABLE analysis_job_window_members;
                DROP TABLE timeline_entry_evidence;
                DELETE FROM schema_migrations WHERE version = 9;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var window = Assert.IsType<AnalysisWindowSnapshot>(
            await store.GetWindowAsync(job.Id));
        var member = Assert.Single(window.Members);

        Assert.Equal(chunk.Id, member.Chunk.Id);
        Assert.Equal(job.InputFingerprint, member.SourceFingerprint.Value);
        Assert.Equal(chunk.Range, member.ContributionRange);
    }

    [Fact]
    public async Task IngestIsIdempotentAndPersistsUnsignedAuthorityAsUppercaseHex()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk(
            "chunk-00000000000000000000000000000001",
            persistenceGeneration: ulong.MaxValue,
            targetEpoch: ulong.MaxValue - 1,
            ingestedAt: Now.AddMinutes(2));

        var first = await store.IngestCommittedAsync(chunk);
        var duplicate = await store.IngestCommittedAsync(CreateChunk(
            chunk.Id,
            persistenceGeneration: ulong.MaxValue,
            targetEpoch: ulong.MaxValue - 1,
            ingestedAt: Now.AddDays(1)));
        var restored = await ((ICaptureChunkStore)store).GetAsync(chunk.Id);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Chunk, duplicate.Chunk);
        Assert.Equal(chunk.Id, restored?.Id);
        Assert.Equal(ulong.MaxValue, restored?.PersistenceGeneration);
        Assert.Equal(ulong.MaxValue - 1, restored?.TargetEpoch);
        Assert.Equal(chunk.IngestedAtUtc, duplicate.Chunk.IngestedAtUtc);

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT persistence_generation_hex, target_epoch_hex
            FROM capture_chunks
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", chunk.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("FFFFFFFFFFFFFFFF", reader.GetString(0));
        Assert.Equal("FFFFFFFFFFFFFFFE", reader.GetString(1));
    }

    [Fact]
    public async Task IngestRoundTripsForegroundProcessTelemetry()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var telemetry = new CaptureProcessTelemetry(
            "Code.exe",
            4242,
            1250,
            536_870_912,
            402_653_184);
        var chunk = CreateChunk("chunk-process-telemetry", processTelemetry: telemetry);

        await store.IngestCommittedAsync(chunk);
        var restored = Assert.IsType<CaptureChunk>(
            await ((ICaptureChunkStore)store).GetAsync(chunk.Id));

        Assert.Equal(telemetry, restored.ProcessTelemetry);
    }

    [Fact]
    public async Task IngestRejectsConflictingMetadataWithoutChangingStoredChunk()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000002");
        await store.IngestCommittedAsync(chunk);
        var conflict = CreateChunk(chunk.Id, frameByteCount: chunk.FrameByteCount + 1);

        await Assert.ThrowsAsync<CaptureChunkConflictException>(
            () => store.IngestCommittedAsync(conflict));

        var restored = await ((ICaptureChunkStore)store).GetAsync(chunk.Id);
        Assert.Equal(chunk.FrameByteCount, restored?.FrameByteCount);
    }

    [Theory]
    [InlineData(CaptureChunkAvailability.Missing)]
    [InlineData(CaptureChunkAvailability.Deleted)]
    public async Task IngestRejectsChunkThatIsNotAvailable(
        CaptureChunkAvailability availability)
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk(
            "chunk-00000000000000000000000000000011",
            availability: availability);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => store.IngestCommittedAsync(chunk));

        Assert.Equal("chunk", exception.ParamName);
        Assert.Null(await ((ICaptureChunkStore)store).GetAsync(chunk.Id));
    }

    [Fact]
    public async Task EnqueueIsIdempotentByStableInputKeyAndAllowsChangedFingerprint()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000003");
        await store.IngestCommittedAsync(chunk);
        var firstJob = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            chunk.Id,
            fingerprintCharacter: 'A');
        var duplicateJob = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            chunk.Id,
            fingerprintCharacter: 'A',
            createdAt: Now.AddMinutes(1));

        var first = await store.EnqueueAsync(firstJob);
        var duplicate = await store.EnqueueAsync(duplicateJob);

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(firstJob.Id, duplicate.Job.Id);
        Assert.Equal(AnalysisJobState.Pending, duplicate.Job.State);

        var changedInput = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
            chunk.Id,
            fingerprintCharacter: 'B');
        var changed = await store.EnqueueAsync(changedInput);

        Assert.True(changed.Created);
        Assert.Equal(changedInput.Id, changed.Job.Id);
    }

    [Fact]
    public async Task EnqueueRejectsMissingSourceChunkWithoutPersistingJob()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var job = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000004"),
            "chunk-00000000000000000000000000000012");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.EnqueueAsync(job));

        Assert.Null(await ((IAnalysisJobStore)store).GetAsync(job.Id));
    }

    [Theory]
    [InlineData(CaptureChunkAvailability.Missing)]
    [InlineData(CaptureChunkAvailability.Deleted)]
    public async Task EnqueueRejectsSourceChunkThatIsNotAvailable(
        CaptureChunkAvailability availability)
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000013");
        await store.IngestCommittedAsync(chunk);
        await SetChunkAvailabilityAsync(factory, chunk.Id, availability);
        var job = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000005"),
            chunk.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnqueueAsync(job));

        Assert.Null(await ((IAnalysisJobStore)store).GetAsync(job.Id));
    }

    [Fact]
    public async Task CompletedAnalysisQueryOnlyMatchesCompletedJobAtSameVersionAndFingerprint()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var jobStore = (IAnalysisJobStore)store;

        var completedChunk = CreateChunk("chunk-completed");
        await store.IngestCommittedAsync(completedChunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000006"),
            completedChunk.Id));
        await CompleteNextJobAsync(store, "worker-completed");

        var failedChunk = CreateChunk("chunk-failed");
        await store.IngestCommittedAsync(failedChunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000007"),
            failedChunk.Id,
            maxAttempts: 1));
        var failedClaim = Assert.IsType<AnalysisJob>(await store.TryClaimNextAsync(
            "worker-failed",
            Now,
            TimeSpan.FromMinutes(5)));
        var failed = await store.TryFailAsync(
            failedClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderUnavailable),
            AnalysisFailureDisposition.Retryable,
            Now.AddSeconds(1),
            TimeSpan.FromSeconds(30));
        Assert.Equal(AnalysisJobState.FailedTerminal, failed?.State);

        var cancelledChunk = CreateChunk("chunk-cancelled");
        await store.IngestCommittedAsync(cancelledChunk);
        var cancelledJob = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000008"),
            cancelledChunk.Id);
        await store.EnqueueAsync(cancelledJob);
        var cancelled = await store.TryCancelAsync(cancelledJob.Id, Now.AddSeconds(1));
        Assert.Equal(AnalysisJobState.Cancelled, cancelled?.State);

        var pendingChunk = CreateChunk("chunk-pending");
        await store.IngestCommittedAsync(pendingChunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000009"),
            pendingChunk.Id));

        Assert.True(await jobStore.HasCompletedAnalysisAsync(
            completedChunk.Id,
            "analysis-v1",
            new string('A', 64)));
        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            completedChunk.Id,
            "analysis-v1",
            new string('B', 64)));
        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            completedChunk.Id,
            "analysis-v2",
            new string('A', 64)));
        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            failedChunk.Id,
            "analysis-v1",
            new string('A', 64)));
        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            cancelledChunk.Id,
            "analysis-v1",
            new string('A', 64)));
        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            pendingChunk.Id,
            "analysis-v1",
            new string('A', 64)));
    }

    [Fact]
    public async Task CompletedAnalysisQueryMatchesEachCompletedFingerprintIndependently()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var jobStore = (IAnalysisJobStore)store;
        var chunk = CreateChunk("chunk-conflicting-completed");
        await store.IngestCommittedAsync(chunk);

        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-00000000000a"),
            chunk.Id,
            fingerprintCharacter: 'A'));
        await CompleteNextJobAsync(store, "worker-completed-a");
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-00000000000b"),
            chunk.Id,
            fingerprintCharacter: 'B',
            providerProfileRevision: 2));
        await CompleteNextJobAsync(store, "worker-completed-b");

        Assert.True(await jobStore.HasCompletedAnalysisAsync(
            chunk.Id,
            "analysis-v1",
            new string('A', 64)));
        Assert.True(await jobStore.HasCompletedAnalysisAsync(
            chunk.Id,
            "analysis-v1",
            new string('B', 64)));
    }

    [Fact]
    public async Task CompletedAnalysisQueryReturnsFalseForChangedFingerprintWhileEarlierRevisionIsPending()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var jobStore = (IAnalysisJobStore)store;
        var chunk = CreateChunk("chunk-pending-earlier-revision");
        await store.IngestCommittedAsync(chunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-00000000000c"),
            chunk.Id,
            fingerprintCharacter: 'A',
            providerProfileRevision: 1));

        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            chunk.Id,
            "analysis-v1",
            new string('B', 64)));
        Assert.False(await jobStore.HasCompletedAnalysisAsync(
            chunk.Id,
            "analysis-v1",
            new string('A', 64)));
    }

    [Fact]
    public async Task CompletedAnalysisQueryValidatesArgumentsAndCancellation()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var jobStore = (IAnalysisJobStore)store;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobStore.HasCompletedAnalysisAsync(
                "INVALID",
                "analysis-v1",
                new string('A', 64)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobStore.HasCompletedAnalysisAsync(
                "chunk-valid",
                " analysis-v1",
                new string('A', 64)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobStore.HasCompletedAnalysisAsync(
                "chunk-valid",
                "analysis-v1",
                new string('A', 63)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            jobStore.HasCompletedAnalysisAsync(
                "chunk-valid",
                "analysis-v1",
                new string('a', 64)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            jobStore.HasCompletedAnalysisAsync(
                "chunk-valid",
                "analysis-v1",
                new string('A', 64),
                cancellation.Token));
    }

    [Theory]
    [InlineData(CaptureChunkAvailability.Missing)]
    [InlineData(CaptureChunkAvailability.Deleted)]
    public async Task ClaimSkipsJobWhoseSourceChunkIsNotAvailable(
        CaptureChunkAvailability availability)
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000014");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            chunk.Id);
        await store.EnqueueAsync(job);
        await SetChunkAvailabilityAsync(factory, chunk.Id, availability);

        Assert.Null(await store.TryClaimNextAsync(
            "worker-a",
            Now,
            TimeSpan.FromMinutes(5)));

        var persisted = await ((IAnalysisJobStore)store).GetAsync(job.Id);
        Assert.NotNull(persisted);
        Assert.Equal(AnalysisJobState.Pending, persisted.State);
        Assert.Equal(0, persisted.Attempt);
    }

    [Fact]
    public async Task ConcurrentClaimHasExactlyOneWinner()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000004");
        await store.IngestCommittedAsync(chunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            chunk.Id));

        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            store.TryClaimNextAsync(
                $"worker-{index}",
                Now,
                TimeSpan.FromMinutes(5))));

        var winner = Assert.Single(claims, static claim => claim is not null);
        Assert.Equal(AnalysisJobState.Claimed, winner!.State);
        Assert.Equal(1, winner.Attempt);
        Assert.Equal(winner.Id, winner.Lease?.JobId);
        Assert.Equal(32, winner.Lease?.Token.Length);
    }

    [Fact]
    public async Task LeaseCanBeRenewedAndNormalStatesAdvanceWithCompareAndSwap()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000005");
        await store.IngestCommittedAsync(chunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            chunk.Id));
        var claimed = await store.TryClaimNextAsync(
            "worker-a",
            Now,
            TimeSpan.FromMinutes(2));
        Assert.NotNull(claimed);

        var extracting = await store.TryTransitionAsync(
            claimed.Lease!,
            AnalysisJobState.Claimed,
            AnalysisJobState.Extracting,
            Now.AddSeconds(1));
        Assert.NotNull(extracting);
        Assert.Null(await store.TryTransitionAsync(
            claimed.Lease!,
            AnalysisJobState.Claimed,
            AnalysisJobState.Extracting,
            Now.AddSeconds(2)));

        var renewed = await store.TryRenewLeaseAsync(
            extracting.Lease!,
            Now.AddSeconds(2),
            Now.AddMinutes(10));
        Assert.NotNull(renewed);
        Assert.Equal(Now.AddMinutes(10), renewed.Lease?.ExpiresAtUtc);

        var current = renewed;
        var transitions = new[]
        {
            (AnalysisJobState.Extracting, AnalysisJobState.Observing),
            (AnalysisJobState.Observing, AnalysisJobState.Summarizing),
            (AnalysisJobState.Summarizing, AnalysisJobState.Committing),
            (AnalysisJobState.Committing, AnalysisJobState.Completed),
        };
        for (var index = 0; index < transitions.Length; index++)
        {
            var (expected, next) = transitions[index];
            var transitioned = await store.TryTransitionAsync(
                current.Lease!,
                expected,
                next,
                Now.AddSeconds(index + 3));
            Assert.NotNull(transitioned);
            current = transitioned;
        }

        Assert.Equal(AnalysisJobState.Completed, current.State);
        Assert.Null(current.Lease);
        Assert.Equal(Now.AddSeconds(6), current.CompletedAtUtc);
    }

    [Fact]
    public async Task RetryableFailureHonorsBackoffAndBecomesTerminalAtAttemptLimit()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-00000000000000000000000000000006");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            chunk.Id,
            maxAttempts: 2);
        await store.EnqueueAsync(job);

        var firstClaim = await store.TryClaimNextAsync(
            "worker-a",
            Now,
            TimeSpan.FromMinutes(5));
        Assert.NotNull(firstClaim);
        var retryable = await store.TryFailAsync(
            firstClaim.Lease!,
            new AnalysisJobFailure(
                AnalysisJobErrorCode.ProviderUnavailable,
                "provider offline"),
            AnalysisFailureDisposition.Retryable,
            Now.AddSeconds(1),
            TimeSpan.FromSeconds(30));
        Assert.NotNull(retryable);

        Assert.Equal(AnalysisJobState.FailedRetryable, retryable.State);
        Assert.Equal(Now.AddSeconds(31), retryable.NotBeforeUtc);
        Assert.Null(await store.TryClaimNextAsync(
            "worker-b",
            Now.AddSeconds(30),
            TimeSpan.FromMinutes(5)));

        var secondClaim = await store.TryClaimNextAsync(
            "worker-b",
            Now.AddSeconds(31),
            TimeSpan.FromMinutes(5));
        Assert.NotNull(secondClaim);
        var terminal = await store.TryFailAsync(
            secondClaim.Lease!,
            new AnalysisJobFailure(AnalysisJobErrorCode.ProviderUnavailable),
            AnalysisFailureDisposition.Retryable,
            Now.AddSeconds(32),
            TimeSpan.FromSeconds(30));
        Assert.NotNull(terminal);

        Assert.Equal(AnalysisJobState.FailedTerminal, terminal.State);
        Assert.Equal(2, terminal.Attempt);
        Assert.Null(terminal.NotBeforeUtc);
        Assert.Equal(Now.AddSeconds(32), terminal.CompletedAtUtc);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 3)]
    public async Task ManualRetryReschedulesTerminalJobAndOnlyExtendsExhaustedAttemptBudget(
        int maxAttempts,
        int expectedMaxAttempts)
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-terminal");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000001"),
            chunk.Id,
            maxAttempts: maxAttempts);
        await store.EnqueueAsync(job);
        var claimed = Assert.IsType<AnalysisJob>(await store.TryClaimNextAsync(
            "worker-manual-terminal",
            Now,
            TimeSpan.FromMinutes(5)));
        var failed = Assert.IsType<AnalysisJob>(await store.TryFailAsync(
            claimed.Lease!,
            new AnalysisJobFailure(
                AnalysisJobErrorCode.ProviderRejected,
                "provider policy rejected the request"),
            AnalysisFailureDisposition.Terminal,
            Now.AddSeconds(1),
            TimeSpan.Zero));
        var requestedAt = Now.AddSeconds(2);

        var result = await store.TryRetryAsync(job.Id, requestedAt);

        Assert.Equal(AnalysisJobRetryOutcome.Scheduled, result.Outcome);
        Assert.True(result.Accepted);
        var retried = Assert.IsType<AnalysisJob>(result.Job);
        Assert.Equal(AnalysisJobState.FailedRetryable, retried.State);
        Assert.Equal(1, retried.Attempt);
        Assert.Equal(expectedMaxAttempts, retried.MaxAttempts);
        Assert.Equal(requestedAt, retried.NotBeforeUtc);
        Assert.Equal(requestedAt, retried.UpdatedAtUtc);
        Assert.Null(retried.CompletedAtUtc);
        Assert.Equal(failed.Failure, retried.Failure);
        Assert.Null(retried.Lease);
    }

    [Fact]
    public async Task ManualRetryAdvancesRetryableBackoffWithoutChangingAttemptBudget()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-backoff");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000002"),
            chunk.Id,
            maxAttempts: 3);
        await store.EnqueueAsync(job);
        var claimed = Assert.IsType<AnalysisJob>(await store.TryClaimNextAsync(
            "worker-manual-backoff",
            Now,
            TimeSpan.FromMinutes(5)));
        var failed = Assert.IsType<AnalysisJob>(await store.TryFailAsync(
            claimed.Lease!,
            new AnalysisJobFailure(
                AnalysisJobErrorCode.ProviderUnavailable,
                "provider offline"),
            AnalysisFailureDisposition.Retryable,
            Now.AddSeconds(1),
            TimeSpan.FromMinutes(5)));
        var requestedAt = Now.AddSeconds(2);

        var first = await store.TryRetryAsync(job.Id, requestedAt);
        var duplicate = await store.TryRetryAsync(job.Id, requestedAt);
        var alreadyDue = await store.TryRetryAsync(job.Id, requestedAt.AddSeconds(1));

        Assert.Equal(AnalysisJobRetryOutcome.Scheduled, first.Outcome);
        Assert.Equal(AnalysisJobRetryOutcome.AlreadyScheduled, duplicate.Outcome);
        Assert.Equal(AnalysisJobRetryOutcome.AlreadyScheduled, alreadyDue.Outcome);
        Assert.Equal(3, first.Job?.MaxAttempts);
        Assert.Equal(1, first.Job?.Attempt);
        Assert.Equal(requestedAt, first.Job?.NotBeforeUtc);
        Assert.Equal(failed.Failure, first.Job?.Failure);
        Assert.Equal(first.Job, duplicate.Job);
        Assert.Equal(first.Job, alreadyDue.Job);
    }

    [Fact]
    public async Task ManualRetryRejectsNonexistentAndNonfailedJobs()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-state");
        await store.IngestCommittedAsync(chunk);
        var pending = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000003"),
            chunk.Id);
        await store.EnqueueAsync(pending);

        var missing = await store.TryRetryAsync(
            Guid.Parse("41000000-0000-0000-0000-000000000004"),
            Now);
        var wrongState = await store.TryRetryAsync(pending.Id, Now);

        Assert.Equal(AnalysisJobRetryOutcome.NotFound, missing.Outcome);
        Assert.Null(missing.Job);
        Assert.Equal(AnalysisJobRetryOutcome.StateNotRetryable, wrongState.Outcome);
        Assert.Equal(pending, wrongState.Job);
    }

    [Fact]
    public async Task ManualRetryOnlyAcceptsLatestJobUsingStableRetryOrdering()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-latest");
        await store.IngestCommittedAsync(chunk);
        var earlierWithHigherRevisionAndId = CreatePendingJob(
            Guid.Parse("ffffffff-0000-0000-0000-000000000001"),
            chunk.Id,
            providerProfileRevision: 99,
            analysisVersion: "analysis-earlier",
            createdAt: Now);
        var laterCreated = CreatePendingJob(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            chunk.Id,
            providerProfileRevision: 1,
            analysisVersion: "analysis-later-created",
            createdAt: Now.AddSeconds(10));
        var sameTimeHigherRevision = CreatePendingJob(
            Guid.Parse("05000000-0000-0000-0000-000000000001"),
            chunk.Id,
            providerProfileRevision: 2,
            analysisVersion: "analysis-higher-revision",
            createdAt: Now.AddSeconds(10));
        var sameTimeAndRevisionHigherId = CreatePendingJob(
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            chunk.Id,
            providerProfileRevision: 2,
            analysisVersion: "analysis-higher-id",
            createdAt: Now.AddSeconds(10));
        await FailTerminalJobAsync(store, earlierWithHigherRevisionAndId, Now.AddSeconds(1));
        await FailTerminalJobAsync(store, laterCreated, Now.AddSeconds(11));
        await FailTerminalJobAsync(store, sameTimeHigherRevision, Now.AddSeconds(11));
        await FailTerminalJobAsync(store, sameTimeAndRevisionHigherId, Now.AddSeconds(11));
        var requestedAt = Now.AddSeconds(12);

        var earlier = await store.TryRetryAsync(earlierWithHigherRevisionAndId.Id, requestedAt);
        var lowerRevision = await store.TryRetryAsync(laterCreated.Id, requestedAt);
        var lowerId = await store.TryRetryAsync(sameTimeHigherRevision.Id, requestedAt);
        var latest = await store.TryRetryAsync(sameTimeAndRevisionHigherId.Id, requestedAt);

        Assert.Equal(AnalysisJobRetryOutcome.StaleJob, earlier.Outcome);
        Assert.Equal(AnalysisJobRetryOutcome.StaleJob, lowerRevision.Outcome);
        Assert.Equal(AnalysisJobRetryOutcome.StaleJob, lowerId.Outcome);
        Assert.Equal(AnalysisJobRetryOutcome.Scheduled, latest.Outcome);
    }

    [Theory]
    [InlineData(CaptureChunkAvailability.Missing)]
    [InlineData(CaptureChunkAvailability.Deleted)]
    public async Task ManualRetryRejectsUnavailableEvidence(
        CaptureChunkAvailability availability)
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk($"chunk-manual-retry-{availability.ToString().ToLowerInvariant()}");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-00000000000a"),
            chunk.Id,
            maxAttempts: 1);
        var failed = await FailTerminalJobAsync(store, job, Now.AddSeconds(1));
        await SetChunkAvailabilityAsync(factory, chunk.Id, availability);

        var result = await store.TryRetryAsync(job.Id, Now.AddSeconds(2));

        Assert.Equal(AnalysisJobRetryOutcome.EvidenceUnavailable, result.Outcome);
        Assert.Equal(failed, result.Job);
    }

    [Fact]
    public async Task ManualRetryRejectsChunkThatAlreadyHasCompletedAnalysis()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-completed");
        await store.IngestCommittedAsync(chunk);
        await store.EnqueueAsync(CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000005"),
            chunk.Id,
            analysisVersion: "analysis-completed",
            createdAt: Now));
        await CompleteNextJobAsync(store, "worker-before-manual-retry");
        var failedJob = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000006"),
            chunk.Id,
            providerProfileRevision: 2,
            analysisVersion: "analysis-newer-failed",
            maxAttempts: 1,
            createdAt: Now.AddSeconds(10));
        var failed = await FailTerminalJobAsync(store, failedJob, Now.AddSeconds(11));

        var result = await store.TryRetryAsync(failedJob.Id, Now.AddSeconds(12));

        Assert.Equal(AnalysisJobRetryOutcome.AnalysisAlreadyCompleted, result.Outcome);
        Assert.Equal(failed, result.Job);
    }

    [Fact]
    public async Task ManualRetryStopsAtAbsoluteAttemptLimit()
    {
        using var database = new TemporaryDatabase();
        var (factory, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-attempt-limit");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000007"),
            chunk.Id,
            maxAttempts: 100);
        await store.EnqueueAsync(job);
        await SetTerminalJobAsync(
            factory,
            job.Id,
            attempt: 100,
            maxAttempts: 100,
            Now.AddSeconds(1));

        var result = await store.TryRetryAsync(job.Id, Now.AddSeconds(2));

        Assert.Equal(AnalysisJobRetryOutcome.AttemptLimitReached, result.Outcome);
        Assert.Equal(100, result.Job?.Attempt);
        Assert.Equal(100, result.Job?.MaxAttempts);
        Assert.Equal(AnalysisJobState.FailedTerminal, result.Job?.State);
    }

    [Fact]
    public async Task ConcurrentManualRetrySchedulesExactlyOnce()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-concurrent");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000008"),
            chunk.Id,
            maxAttempts: 1);
        await FailTerminalJobAsync(store, job, Now.AddSeconds(1));
        var requestedAt = Now.AddSeconds(2);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            store.TryRetryAsync(job.Id, requestedAt)));

        Assert.Single(results, static result =>
            result.Outcome == AnalysisJobRetryOutcome.Scheduled);
        Assert.Equal(15, results.Count(static result =>
            result.Outcome == AnalysisJobRetryOutcome.AlreadyScheduled));
        var persisted = Assert.IsType<AnalysisJob>(
            await ((IAnalysisJobStore)store).GetAsync(job.Id));
        Assert.Equal(AnalysisJobState.FailedRetryable, persisted.State);
        Assert.Equal(1, persisted.Attempt);
        Assert.Equal(2, persisted.MaxAttempts);
        Assert.Equal(requestedAt, persisted.NotBeforeUtc);
    }

    [Fact]
    public async Task ManualRetryValidatesIdentifierTimestampAndCancellation()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var chunk = CreateChunk("chunk-manual-retry-validation");
        await store.IngestCommittedAsync(chunk);
        var job = CreatePendingJob(
            Guid.Parse("41000000-0000-0000-0000-000000000009"),
            chunk.Id,
            maxAttempts: 1);
        await FailTerminalJobAsync(store, job, Now.AddSeconds(1));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.TryRetryAsync(Guid.Empty, Now.AddSeconds(2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.TryRetryAsync(job.Id, Now));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.TryRetryAsync(job.Id, Now.AddSeconds(2), cancellation.Token));
    }

    [Fact]
    public async Task ExpiredLeaseRecoveryRetriesWhenPossibleAndTerminatesAtLimit()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var retryChunk = CreateChunk("chunk-00000000000000000000000000000007");
        var terminalChunk = CreateChunk("chunk-00000000000000000000000000000008");
        await store.IngestCommittedAsync(retryChunk);
        await store.IngestCommittedAsync(terminalChunk);
        var retryJob = CreatePendingJob(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            retryChunk.Id,
            maxAttempts: 2);
        var terminalJob = CreatePendingJob(
            Guid.Parse("50000000-0000-0000-0000-000000000002"),
            terminalChunk.Id,
            analysisVersion: "analysis-v2",
            maxAttempts: 1);
        await store.EnqueueAsync(retryJob);
        await store.EnqueueAsync(terminalJob);
        var firstClaim = await store.TryClaimNextAsync(
            "worker-a",
            Now,
            TimeSpan.FromMinutes(1));
        Assert.NotNull(firstClaim);
        var secondClaim = await store.TryClaimNextAsync(
            "worker-b",
            Now,
            TimeSpan.FromMinutes(1));
        Assert.NotNull(secondClaim);

        Assert.Equal(2, await store.RecoverExpiredLeasesAsync(
            Now.AddMinutes(1),
            TimeSpan.FromSeconds(15)));

        var first = await ((IAnalysisJobStore)store).GetAsync(firstClaim.Id);
        var second = await ((IAnalysisJobStore)store).GetAsync(secondClaim.Id);
        Assert.NotNull(first);
        Assert.NotNull(second);
        var retryable = first.MaxAttempts == 2 ? first : second;
        var terminal = first.MaxAttempts == 1 ? first : second;
        Assert.Equal(AnalysisJobState.FailedRetryable, retryable.State);
        Assert.Equal(AnalysisJobErrorCode.LeaseExpired, retryable.Failure?.Code);
        Assert.Equal(Now.AddMinutes(1).AddSeconds(15), retryable.NotBeforeUtc);
        Assert.Equal(AnalysisJobState.FailedTerminal, terminal.State);
        Assert.Equal(AnalysisJobErrorCode.LeaseExpired, terminal.Failure?.Code);

        Assert.Null(await store.TryTransitionAsync(
            firstClaim.Lease!,
            AnalysisJobState.Claimed,
            AnalysisJobState.Extracting,
            Now.AddMinutes(1).AddSeconds(1)));
    }

    [Fact]
    public async Task PendingAndRetryableJobsCanBeCancelledButActiveJobsCannot()
    {
        using var database = new TemporaryDatabase();
        var (_, store) = await CreateStoreAsync(database);
        var firstChunk = CreateChunk("chunk-00000000000000000000000000000009");
        var secondChunk = CreateChunk("chunk-00000000000000000000000000000010");
        await store.IngestCommittedAsync(firstChunk);
        await store.IngestCommittedAsync(secondChunk);
        var pending = CreatePendingJob(
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            firstChunk.Id);
        var active = CreatePendingJob(
            Guid.Parse("60000000-0000-0000-0000-000000000002"),
            secondChunk.Id,
            analysisVersion: "analysis-v2");
        await store.EnqueueAsync(pending);
        await store.EnqueueAsync(active);

        var cancelled = await store.TryCancelAsync(
            pending.Id,
            Now.AddSeconds(1));
        Assert.NotNull(cancelled);
        Assert.Equal(AnalysisJobState.Cancelled, cancelled.State);
        Assert.Equal(Now.AddSeconds(1), cancelled.CompletedAtUtc);

        var claimed = await store.TryClaimNextAsync(
            "worker-a",
            Now.AddSeconds(2),
            TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        Assert.Equal(active.Id, claimed.Id);
        Assert.Null(await store.TryCancelAsync(active.Id, Now.AddSeconds(3)));
    }

    [Fact]
    public void StoreRequiresFullyQualifiedEvidenceRoot()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);

        Assert.Throws<ArgumentException>(() => new SqliteCaptureAnalysisStore(
            factory,
            "relative-evidence"));
    }

    private static async Task<(SqliteConnectionFactory Factory, SqliteCaptureAnalysisStore Store)>
        CreateStoreAsync(TemporaryDatabase database)
    {
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        return (factory, new SqliteCaptureAnalysisStore(factory, database.EvidenceRoot));
    }

    private static async Task CompleteNextJobAsync(
        SqliteCaptureAnalysisStore store,
        string leaseOwner)
    {
        var current = Assert.IsType<AnalysisJob>(await store.TryClaimNextAsync(
            leaseOwner,
            Now,
            TimeSpan.FromMinutes(5)));
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
            current = Assert.IsType<AnalysisJob>(await store.TryTransitionAsync(
                current.Lease!,
                expected,
                next,
                Now.AddSeconds(index + 1)));
        }
    }

    private static async Task<AnalysisJob> FailTerminalJobAsync(
        SqliteCaptureAnalysisStore store,
        AnalysisJob pendingJob,
        DateTimeOffset failedAt)
    {
        await store.EnqueueAsync(pendingJob);
        var claimed = Assert.IsType<AnalysisJob>(await store.TryClaimNextAsync(
            $"worker-{pendingJob.Id:N}",
            failedAt.AddTicks(-1),
            TimeSpan.FromMinutes(5)));
        Assert.Equal(pendingJob.Id, claimed.Id);
        return Assert.IsType<AnalysisJob>(await store.TryFailAsync(
            claimed.Lease!,
            new AnalysisJobFailure(
                AnalysisJobErrorCode.ProviderRejected,
                "manual retry regression"),
            AnalysisFailureDisposition.Terminal,
            failedAt,
            TimeSpan.Zero));
    }

    private static CaptureChunk CreateChunk(
        string id,
        ulong persistenceGeneration = 1,
        ulong targetEpoch = 2,
        long frameByteCount = 4096,
        DateTimeOffset? ingestedAt = null,
        CaptureChunkAvailability availability = CaptureChunkAvailability.Available,
        CaptureProcessTelemetry? processTelemetry = null)
    {
        var start = Now.ToOffset(TimeSpan.FromHours(8));
        return new CaptureChunk(
            id,
            new EvidenceRelativePath($"chunks/{id}/manifest.json"),
            new TimeRange(start, start.AddMinutes(1)),
            capturedFrameCount: 10,
            frameCount: 6,
            frameWidth: 1600,
            frameHeight: 900,
            frameByteCount,
            persistenceGeneration,
            targetEpoch,
            committedAtUtc: Now.AddMinutes(1),
            ingestedAtUtc: ingestedAt ?? Now.AddMinutes(2),
            availability: availability,
            processTelemetry: processTelemetry);
    }

    private static async Task SetChunkAvailabilityAsync(
        SqliteConnectionFactory factory,
        string chunkId,
        CaptureChunkAvailability availability)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE capture_chunks
            SET availability = $availability
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$availability", (int)availability);
        command.Parameters.AddWithValue("$id", chunkId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SetTerminalJobAsync(
        SqliteConnectionFactory factory,
        Guid jobId,
        int attempt,
        int maxAttempts,
        DateTimeOffset failedAt)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE analysis_jobs
            SET state = $terminal_state,
                attempt = $attempt,
                max_attempts = $max_attempts,
                not_before_utc_ticks = NULL,
                error_code = $error_code,
                error_detail = $error_detail,
                updated_at_utc_ticks = $failed_at_utc_ticks,
                completed_at_utc_ticks = $failed_at_utc_ticks
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$terminal_state",
            (int)AnalysisJobState.FailedTerminal);
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$max_attempts", maxAttempts);
        command.Parameters.AddWithValue(
            "$error_code",
            (int)AnalysisJobErrorCode.ProviderRejected);
        command.Parameters.AddWithValue("$error_detail", "manual retry attempt limit");
        command.Parameters.AddWithValue(
            "$failed_at_utc_ticks",
            failedAt.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static AnalysisJob CreatePendingJob(
        Guid id,
        string chunkId,
        char fingerprintCharacter = 'A',
        long providerProfileRevision = 1,
        string analysisVersion = "analysis-v1",
        int maxAttempts = 3,
        DateTimeOffset? createdAt = null)
    {
        return AnalysisJob.CreatePending(
            id,
            chunkId,
            ProviderId,
            providerProfileRevision,
            analysisVersion,
            new string(fingerprintCharacter, 64),
            maxAttempts,
            createdAt ?? Now);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.CaptureAnalysis.Tests",
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
