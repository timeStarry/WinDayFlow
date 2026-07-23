using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Analysis;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Timeline;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Analysis;

public sealed class SqliteAnalysisResultCommitterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 3, 0, 0, TimeSpan.Zero);

    private static readonly Guid ProfileId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task CommitAtomicallyWritesTimelineChildrenAndCompletesJob()
    {
        using var context = await TestContext.CreateAsync();
        var entry = context.CreateEntry(
            Guid.Parse("10000000-0000-0000-0000-000000000001"));

        var status = await context.Committer.TryCommitAsync(
            context.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [entry],
            context.CommitAt);

        Assert.Equal(AnalysisResultCommitStatus.Committed, status);
        var completed = await ((IAnalysisJobStore)context.Store).GetAsync(context.JobId);
        Assert.Equal(AnalysisJobState.Completed, completed?.State);
        Assert.Equal(context.CommitAt, completed?.CompletedAtUtc);
        var restored = await context.Timeline.GetByIdAsync(entry.Id);
        Assert.NotNull(restored);
        AssertEntryEquivalent(entry, restored);
        Assert.Equal((1, 1, 1), await context.ReadCountsAsync());
    }

    [Fact]
    public async Task CloudDisableAndProviderRevisionDriftDoNotWriteResults()
    {
        using var disabled = await TestContext.CreateAsync(cloudEnabled: false);
        var disabledStatus = await disabled.Committer.TryCommitAsync(
            disabled.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [disabled.CreateEntry(Guid.NewGuid())],
            disabled.CommitAt);
        Assert.Equal(AnalysisResultCommitStatus.CloudAnalysisDisabled, disabledStatus);
        Assert.Equal((0, 0, 0), await disabled.ReadCountsAsync());
        Assert.Equal(
            AnalysisJobState.Committing,
            (await ((IAnalysisJobStore)disabled.Store).GetAsync(disabled.JobId))?.State);

        using var changed = await TestContext.CreateAsync();
        await changed.ChangeProfileRevisionAsync(2);
        var changedStatus = await changed.Committer.TryCommitAsync(
            changed.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [changed.CreateEntry(Guid.NewGuid())],
            changed.CommitAt);
        Assert.Equal(AnalysisResultCommitStatus.ProviderRevisionChanged, changedStatus);
        Assert.Equal((0, 0, 0), await changed.ReadCountsAsync());
        Assert.Equal(
            AnalysisJobState.Committing,
            (await ((IAnalysisJobStore)changed.Store).GetAsync(changed.JobId))?.State);
    }

    [Fact]
    public async Task ExpiredOrWrongAttemptLeaseCannotWriteOrComplete()
    {
        using var context = await TestContext.CreateAsync();

        var expired = await context.Committer.TryCommitAsync(
            context.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [context.CreateEntry(Guid.NewGuid())],
            context.Lease.ExpiresAtUtc);
        var wrongAttemptLease = new AnalysisJobLease(
            context.JobId,
            context.Lease.Owner,
            context.Lease.Token,
            context.Lease.Attempt + 1,
            context.Lease.ExpiresAtUtc.AddMinutes(1));
        var wrongAttempt = await context.Committer.TryCommitAsync(
            wrongAttemptLease,
            ProfileId,
            providerProfileRevision: 1,
            [context.CreateEntry(Guid.NewGuid())],
            context.CommitAt);

        Assert.Equal(AnalysisResultCommitStatus.LeaseLost, expired);
        Assert.Equal(AnalysisResultCommitStatus.LeaseLost, wrongAttempt);
        Assert.Equal((0, 0, 0), await context.ReadCountsAsync());
        Assert.Equal(
            AnalysisJobState.Committing,
            (await ((IAnalysisJobStore)context.Store).GetAsync(context.JobId))?.State);
    }

    [Fact]
    public async Task ChildWriteFailureRollsBackAllEntriesAndJobCompletion()
    {
        using var context = await TestContext.CreateAsync();
        await context.CreateFailingTagTriggerAsync();
        var first = context.CreateEntry(
            Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var second = context.CreateEntry(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            tags: ["force-rollback"]);

        await Assert.ThrowsAsync<SqliteException>(() => context.Committer.TryCommitAsync(
            context.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [first, second],
            context.CommitAt));

        Assert.Equal((0, 0, 0), await context.ReadCountsAsync());
        Assert.Equal(
            AnalysisJobState.Committing,
            (await ((IAnalysisJobStore)context.Store).GetAsync(context.JobId))?.State);
    }

    [Fact]
    public async Task DifferentExistingDeterministicEntryRollsBackAndIsNotOverwritten()
    {
        using var context = await TestContext.CreateAsync();
        var id = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var manual = TimelineEntry.CreateManual(
            id,
            new TimeRange(Now.AddSeconds(5), Now.AddSeconds(25)),
            "User-owned title",
            "User-owned summary",
            ActivityCategory.Personal,
            ProductivityKind.Neutral,
            ["manual"],
            Now.AddSeconds(4));
        await context.Timeline.AddAsync(manual);

        var status = await context.Committer.TryCommitAsync(
            context.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [context.CreateEntry(id)],
            context.CommitAt);

        Assert.Equal(AnalysisResultCommitStatus.EntryConflict, status);
        var preserved = await context.Timeline.GetByIdAsync(id);
        Assert.NotNull(preserved);
        Assert.Equal(manual.Title, preserved.Title);
        Assert.Equal(manual.Summary, preserved.Summary);
        Assert.Equal(manual.Origin, preserved.Origin);
        Assert.Equal(manual.Revision, preserved.Revision);
        Assert.Equal(manual.Tags, preserved.Tags);
        Assert.Equal(
            AnalysisJobState.Committing,
            (await ((IAnalysisJobStore)context.Store).GetAsync(context.JobId))?.State);
    }

    [Fact]
    public async Task ExactExistingResultIsIdempotentButSecondCompletedCommitLosesCas()
    {
        using var context = await TestContext.CreateAsync();
        var entry = context.CreateEntry(
            Guid.Parse("40000000-0000-0000-0000-000000000001"));
        await context.Timeline.AddAsync(entry);

        var first = await context.Committer.TryCommitAsync(
            context.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [entry],
            context.CommitAt);
        var duplicate = await context.Committer.TryCommitAsync(
            context.Lease,
            ProfileId,
            providerProfileRevision: 1,
            [entry],
            context.CommitAt.AddSeconds(1));

        Assert.Equal(AnalysisResultCommitStatus.Committed, first);
        Assert.Equal(AnalysisResultCommitStatus.LeaseLost, duplicate);
        Assert.Equal((1, 1, 1), await context.ReadCountsAsync());
        var restored = await context.Timeline.GetByIdAsync(entry.Id);
        Assert.NotNull(restored);
        AssertEntryEquivalent(entry, restored);
    }

    private static void AssertEntryEquivalent(TimelineEntry expected, TimelineEntry actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Range, actual.Range);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Productivity, actual.Productivity);
        Assert.Equal(expected.Origin, actual.Origin);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.Evidence, actual.Evidence);
        Assert.Equal(expected.AnalysisVersion, actual.AnalysisVersion);
        Assert.Equal(expected.Apps, actual.Apps);
        Assert.Equal(expected.Tags, actual.Tags);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TemporaryDatabase _database;

        private TestContext(
            TemporaryDatabase database,
            SqliteConnectionFactory factory,
            SqliteCaptureAnalysisStore store,
            SqliteAnalysisResultCommitter committer,
            SqliteTimelineRepository timeline,
            Guid jobId,
            AnalysisJobLease lease,
            CaptureChunk chunk)
        {
            _database = database;
            Factory = factory;
            Store = store;
            Committer = committer;
            Timeline = timeline;
            JobId = jobId;
            Lease = lease;
            Chunk = chunk;
        }

        public SqliteConnectionFactory Factory { get; }

        public SqliteCaptureAnalysisStore Store { get; }

        public SqliteAnalysisResultCommitter Committer { get; }

        public SqliteTimelineRepository Timeline { get; }

        public Guid JobId { get; }

        public AnalysisJobLease Lease { get; }

        public CaptureChunk Chunk { get; }

        public DateTimeOffset CommitAt { get; } = Now.AddSeconds(5);

        public static async Task<TestContext> CreateAsync(bool cloudEnabled = true)
        {
            var database = new TemporaryDatabase();
            try
            {
                var factory = new SqliteConnectionFactory(database.DatabasePath);
                await new SqliteDatabaseInitializer(factory).InitializeAsync();
                await InsertConfigurationAsync(factory, cloudEnabled);
                var store = new SqliteCaptureAnalysisStore(factory, database.EvidenceRoot);
                var chunk = CreateChunk();
                await store.IngestCommittedAsync(chunk);
                var jobId = Guid.NewGuid();
                await store.EnqueueAsync(AnalysisJob.CreatePending(
                    jobId,
                    chunk.Id,
                    ProfileId,
                    providerProfileRevision: 1,
                    "timeline-v1",
                    new string('A', 64),
                    maxAttempts: 3,
                    Now));
                var current = await store.TryClaimNextAsync(
                    "committer-tests",
                    Now,
                    TimeSpan.FromMinutes(10))
                    ?? throw new InvalidOperationException("The test job was not claimed.");
                var transitions = new[]
                {
                    (AnalysisJobState.Claimed, AnalysisJobState.Extracting),
                    (AnalysisJobState.Extracting, AnalysisJobState.Observing),
                    (AnalysisJobState.Observing, AnalysisJobState.Summarizing),
                    (AnalysisJobState.Summarizing, AnalysisJobState.Committing),
                };
                for (var index = 0; index < transitions.Length; index++)
                {
                    var (expected, next) = transitions[index];
                    current = await store.TryTransitionAsync(
                        current.Lease!,
                        expected,
                        next,
                        Now.AddSeconds(index + 1))
                        ?? throw new InvalidOperationException("The test job did not advance.");
                }

                return new TestContext(
                    database,
                    factory,
                    store,
                    new SqliteAnalysisResultCommitter(factory),
                    new SqliteTimelineRepository(factory),
                    jobId,
                    current.Lease!,
                    chunk);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }

        public TimelineEntry CreateEntry(
            Guid id,
            IReadOnlyList<string>? tags = null)
        {
            var activity = new Activity(
                new TimeRange(Chunk.Range.Start, Chunk.Range.Start.AddSeconds(30)),
                "Generated title",
                "Generated summary",
                ActivityCategory.FocusedWork,
                ProductivityKind.Focused,
                [new AppUsage("editor.exe", "Editor", TimeSpan.FromSeconds(30))],
                tags ?? ["generated"],
                confidence: 0.9,
                new EvidenceReference(Chunk.Id, Chunk.VideoPath.Value));
            return TimelineEntry.FromActivity(id, activity, "timeline-v1");
        }

        public async Task<(int Entries, int Apps, int Tags)> ReadCountsAsync()
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM timeline_entries),
                    (SELECT COUNT(*) FROM timeline_entry_apps),
                    (SELECT COUNT(*) FROM timeline_entry_tags);
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }

        public async Task ChangeProfileRevisionAsync(long revision)
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ai_provider_profiles
                SET revision = $revision,
                    validated_revision = $revision,
                    updated_at_utc_ticks = $updated_at_utc_ticks
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue(
                "$updated_at_utc_ticks",
                Now.AddMinutes(1).UtcDateTime.Ticks);
            command.Parameters.AddWithValue("$id", ProfileId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async Task CreateFailingTagTriggerAsync()
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_analysis_tag
                BEFORE INSERT ON timeline_entry_tags
                WHEN NEW.value = 'force-rollback'
                BEGIN
                    SELECT RAISE(ABORT, 'forced child failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose() => _database.Dispose();

        private static async Task InsertConfigurationAsync(
            SqliteConnectionFactory factory,
            bool cloudEnabled)
        {
            await using var connection = await factory.OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var profile = connection.CreateCommand())
            {
                profile.Transaction = transaction;
                profile.CommandText = """
                    INSERT INTO ai_provider_profiles(
                        id,
                        display_name,
                        kind,
                        base_endpoint,
                        model,
                        request_timeout_ticks,
                        revision,
                        is_active,
                        api_key_ciphertext,
                        api_key_salt,
                        api_key_protection_version,
                        validated_revision,
                        validated_at_utc_ticks,
                        created_at_utc_ticks,
                        updated_at_utc_ticks)
                    VALUES (
                        $id,
                        'Local provider',
                        0,
                        'http://localhost:11434/v1/',
                        'vision-v1',
                        $timeout_ticks,
                        1,
                        1,
                        NULL,
                        NULL,
                        NULL,
                        1,
                        $now_ticks,
                        $now_ticks,
                        $now_ticks);
                    """;
                profile.Parameters.AddWithValue("$id", ProfileId.ToString("D"));
                profile.Parameters.AddWithValue(
                    "$timeout_ticks",
                    TimeSpan.FromSeconds(30).Ticks);
                profile.Parameters.AddWithValue("$now_ticks", Now.UtcDateTime.Ticks);
                await profile.ExecuteNonQueryAsync();
            }

            await using (var settings = connection.CreateCommand())
            {
                settings.Transaction = transaction;
                settings.CommandText = """
                    UPDATE app_settings
                    SET cloud_analysis_enabled = $enabled
                    WHERE id = 1;
                    """;
                settings.Parameters.AddWithValue("$enabled", cloudEnabled ? 1 : 0);
                Assert.Equal(1, await settings.ExecuteNonQueryAsync());
            }

            await transaction.CommitAsync();
        }

        private static CaptureChunk CreateChunk()
        {
            const string id = "chunk-committer-0001";
            return new CaptureChunk(
                id,
                new EvidenceRelativePath($"chunks/{id}/capture.mp4"),
                new EvidenceRelativePath($"chunks/{id}/manifest.json"),
                new TimeRange(Now, Now.AddMinutes(1)),
                frameCount: 6,
                videoWidth: 1920,
                videoHeight: 1080,
                frameRateNumerator: 1,
                frameRateDenominator: 10,
                videoByteCount: 4_096,
                persistenceGeneration: 1,
                targetEpoch: 2,
                committedAtUtc: Now.AddMinutes(1),
                ingestedAtUtc: Now.AddMinutes(2));
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.AnalysisCommit.Tests",
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
