using Microsoft.Data.Sqlite;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Timeline;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Timeline;

public sealed class SqliteTimelineRepositoryTests
{
    [Fact]
    public async Task InitializeAsyncCreatesDirectoryAndAppliesEachMigrationOnce()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);

        Assert.False(Directory.Exists(database.DatabaseDirectory));

        await Task.WhenAll(
            initializer.InitializeAsync(),
            new SqliteDatabaseInitializer(factory).InitializeAsync());
        await initializer.InitializeAsync();

        Assert.True(File.Exists(database.DatabasePath));
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, COUNT(*)
            FROM schema_migrations
            GROUP BY version
            ORDER BY version;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ManualEntryPersistsAcrossRepositoryReinstantiationAndCanBeDeleted()
    {
        using var database = new TemporaryDatabase();
        var firstFactory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(firstFactory).InitializeAsync();
        var firstRepository = new SqliteTimelineRepository(firstFactory);
        var entry = CreateManualEntry();

        await firstRepository.AddAsync(entry);

        var secondFactory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(secondFactory).InitializeAsync();
        var secondRepository = new SqliteTimelineRepository(secondFactory);
        var byId = await secondRepository.GetByIdAsync(entry.Id);
        var forDay = await secondRepository.GetForDayAsync(
            DateOnly.FromDateTime(entry.Range.Start.DateTime));

        Assert.NotNull(byId);
        AssertEntryEquivalent(entry, byId);
        AssertEntryEquivalent(entry, Assert.Single(forDay));
        Assert.Equal(TimelineEntryOrigin.Manual, byId.Origin);
        Assert.Null(byId.Confidence);
        Assert.Null(byId.Evidence);
        Assert.Null(byId.AnalysisVersion);
        Assert.Equal(entry.Tags, byId.Tags);
        Assert.True(byId.UserEdits.HasEdits);

        Assert.True(await secondRepository.DeleteAsync(entry.Id));
        Assert.Null(await secondRepository.GetByIdAsync(entry.Id));
        Assert.Empty(await secondRepository.GetForDayAsync(
            DateOnly.FromDateTime(entry.Range.Start.DateTime)));
        Assert.False(await secondRepository.DeleteAsync(entry.Id));
    }

    [Fact]
    public async Task AnalyzedEntryRoundTripPreservesOffsetsOrderedChildrenAndProvenance()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var entry = CreateAnalyzedEntry();

        await repository.AddAsync(entry);

        var restored = await repository.GetByIdAsync(entry.Id);

        Assert.NotNull(restored);
        AssertEntryEquivalent(entry, restored);
        Assert.Equal(
            entry.Apps.Select(static app => app.ApplicationId),
            restored.Apps.Select(static app => app.ApplicationId));
        Assert.Equal(entry.Tags, restored.Tags);
        Assert.Equal(entry.Range.Start.Offset, restored.Range.Start.Offset);
        Assert.Equal(entry.Range.End.Offset, restored.Range.End.Offset);
        Assert.Equal(entry.UserEdits, restored.UserEdits);
        Assert.Equal(TimelineEntryOrigin.Analyzed, restored.Origin);
        Assert.NotNull(restored.Evidence);
    }

    [Fact]
    public async Task UpdateReplacesParentAndOrderedChildren()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var original = CreateAnalyzedEntry();
        await repository.AddAsync(original);
        var replacement = CreateReplacement(original.Id);

        var updated = await repository.UpdateAsync(replacement);
        var restored = await repository.GetByIdAsync(original.Id);

        Assert.True(updated);
        Assert.NotNull(restored);
        AssertEntryEquivalent(replacement, restored);
        Assert.DoesNotContain(
            restored.Apps,
            app => app.ApplicationId == original.Apps[0].ApplicationId);
        Assert.DoesNotContain(original.Tags[0], restored.Tags);
    }

    [Fact]
    public async Task ChildWriteFailureRollsBackEntireAddAndUpdate()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var repository = new SqliteTimelineRepository(factory);
        await CreateRejectingTagTriggerAsync(factory);

        var rejectedAdd = CreateManualEntry(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ["reject"]);

        await Assert.ThrowsAsync<SqliteException>(() => repository.AddAsync(rejectedAdd));
        Assert.Null(await repository.GetByIdAsync(rejectedAdd.Id));

        var original = CreateAnalyzedEntry();
        await repository.AddAsync(original);
        var rejectedUpdate = new TimelineEntry(
            original.Id,
            original.Range,
            "This parent update must roll back",
            original.Summary,
            original.Category,
            original.Productivity,
            [new AppUsage("replacement.app", "Replacement", TimeSpan.FromMinutes(2))],
            ["reject"],
            original.Confidence,
            original.Evidence,
            original.AnalysisVersion,
            original.UserEdits,
            original.Origin);

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpdateAsync(rejectedUpdate));

        var restored = await repository.GetByIdAsync(original.Id);
        Assert.NotNull(restored);
        AssertEntryEquivalent(original, restored);
    }

    [Fact]
    public async Task MissingUpdateAndDeleteReturnFalse()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var missing = CreateManualEntry();

        Assert.False(await repository.UpdateAsync(missing));
        Assert.False(await repository.DeleteAsync(missing.Id));
        Assert.Null(await repository.GetByIdAsync(missing.Id));
    }

    [Fact]
    public async Task StaleRevisionCannotOverwriteANewerUpdate()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var original = CreateAnalyzedEntry();
        await repository.AddAsync(original);
        var firstSnapshot = await repository.GetByIdAsync(original.Id);
        var staleSnapshot = await repository.GetByIdAsync(original.Id);
        Assert.NotNull(firstSnapshot);
        Assert.NotNull(staleSnapshot);

        var firstUpdate = firstSnapshot.ApplyUserEdit(new TimelineEntryEdit(
            firstSnapshot.Range.End,
            title: "First update wins"));
        var staleUpdate = staleSnapshot.ApplyUserEdit(new TimelineEntryEdit(
            staleSnapshot.Range.End.AddMinutes(1),
            title: "Stale update must fail"));

        Assert.True(await repository.UpdateAsync(firstUpdate));
        Assert.False(await repository.UpdateAsync(staleUpdate));

        var restored = await repository.GetByIdAsync(original.Id);
        Assert.NotNull(restored);
        Assert.Equal("First update wins", restored.Title);
        Assert.Equal(1, restored.Revision);
    }

    [Fact]
    public async Task InitializationAndRepositoryOperationsHonorCancellation()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializer.InitializeAsync(cancellation.Token));

        await initializer.InitializeAsync();
        var repository = new SqliteTimelineRepository(factory);
        var entry = CreateManualEntry();
        var operations = new Func<Task>[]
        {
            () => repository.GetForDayAsync(new DateOnly(2026, 7, 16), cancellation.Token),
            () => repository.GetByIdAsync(entry.Id, cancellation.Token),
            () => repository.AddAsync(entry, cancellation.Token),
            () => repository.UpdateAsync(entry, cancellation.Token),
            () => repository.DeleteAsync(entry.Id, cancellation.Token),
        };

        foreach (var operation in operations)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
        }

        Assert.Null(await repository.GetByIdAsync(entry.Id));
    }

    private static async Task<SqliteTimelineRepository> CreateRepositoryAsync(string databasePath)
    {
        var factory = new SqliteConnectionFactory(databasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        return new SqliteTimelineRepository(factory);
    }

    private static async Task CreateRejectingTagTriggerAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_test_tag
            BEFORE INSERT ON timeline_entry_tags
            WHEN NEW.value = 'reject'
            BEGIN
                SELECT RAISE(ABORT, 'forced child write failure');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static TimelineEntry CreateManualEntry(
        Guid? id = null,
        IReadOnlyList<string>? tags = null)
    {
        var start = new DateTimeOffset(2026, 7, 16, 9, 12, 13, TimeSpan.FromHours(8))
            .AddTicks(4567);
        return TimelineEntry.CreateManual(
            id ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new TimeRange(start, start.AddMinutes(47).AddTicks(89)),
            "Manual planning",
            "Written without capture or AI.",
            ActivityCategory.Planning,
            ProductivityKind.Focused,
            tags ?? ["third", "first", "second"],
            start.AddHours(2).ToOffset(TimeSpan.FromHours(5.5)));
    }

    private static TimelineEntry CreateAnalyzedEntry()
    {
        var start = new DateTimeOffset(2026, 7, 16, 10, 1, 2, TimeSpan.FromHours(5.5))
            .AddTicks(1234);
        var end = start.AddMinutes(61).AddTicks(77).ToOffset(TimeSpan.FromHours(-4));
        var provenance = new UserEditProvenance(
            rangeEditedAt: start.AddHours(3).ToOffset(TimeSpan.FromHours(9)),
            titleEditedAt: start.AddHours(4).ToOffset(TimeSpan.FromHours(-7)),
            summaryEditedAt: start.AddHours(5).ToOffset(TimeSpan.FromHours(8)),
            categoryEditedAt: start.AddHours(6).ToOffset(TimeSpan.Zero),
            productivityEditedAt: start.AddHours(7).ToOffset(TimeSpan.FromHours(1)),
            tagsEditedAt: start.AddHours(8).ToOffset(TimeSpan.FromHours(-3.5)));

        return new TimelineEntry(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            new TimeRange(start, end),
            "Analyzed work",
            "Structured provider result.",
            ActivityCategory.FocusedWork,
            ProductivityKind.Focused,
            [
                new AppUsage("app.third", "Third", TimeSpan.FromMinutes(3)),
                new AppUsage("app.first", "First", TimeSpan.FromMinutes(31)),
                new AppUsage("app.second", "Second", TimeSpan.FromMinutes(12)),
            ],
            ["zeta", "alpha", "middle"],
            0.875,
            new EvidenceReference("chunk-ordered", "evidence/chunk-ordered.mp4"),
            "analysis-v7",
            provenance,
            TimelineEntryOrigin.Analyzed);
    }

    private static TimelineEntry CreateReplacement(Guid id)
    {
        var start = new DateTimeOffset(2026, 7, 17, 14, 30, 0, TimeSpan.FromHours(9));
        return new TimelineEntry(
            id,
            new TimeRange(start, start.AddMinutes(25)),
            "Updated title",
            "Updated summary",
            ActivityCategory.Communication,
            ProductivityKind.Neutral,
            [
                new AppUsage("new.second", "New second", TimeSpan.FromMinutes(5)),
                new AppUsage("new.first", "New first", TimeSpan.FromMinutes(20)),
            ],
            ["replacement-2", "replacement-1"],
            0.625,
            new EvidenceReference("chunk-replacement", "evidence/replacement.mp4"),
            "analysis-v8",
            new UserEditProvenance(titleEditedAt: start.AddHours(1)),
            TimelineEntryOrigin.Analyzed);
    }

    private static void AssertEntryEquivalent(TimelineEntry expected, TimelineEntry actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Range.Start, actual.Range.Start);
        Assert.Equal(expected.Range.End, actual.Range.End);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Productivity, actual.Productivity);
        Assert.Equal(expected.Apps, actual.Apps);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.Evidence, actual.Evidence);
        Assert.Equal(expected.AnalysisVersion, actual.AnalysisVersion);
        Assert.Equal(expected.UserEdits, actual.UserEdits);
        Assert.Equal(expected.Origin, actual.Origin);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabaseDirectory => Path.Combine(_rootDirectory, "nested", "data");

        public string DatabasePath => Path.Combine(DatabaseDirectory, "windayflow.db");

        public void Dispose()
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return;
            }

            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The operating system will clean temporary test data if a provider handle lingers.
            }
        }
    }
}
