using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Timeline;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Settings;

public sealed class SqliteAppSettingsMigrationTests
{
    [Fact]
    public async Task VersionOneDatabaseUpgradesWithoutChangingTimelineData()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var timelineRepository = new SqliteTimelineRepository(factory);
        var start = new DateTimeOffset(2026, 7, 16, 8, 30, 0, TimeSpan.FromHours(8));
        var entry = TimelineEntry.CreateManual(
            Guid.Parse("83806c82-529d-40c3-bc15-49867368f07b"),
            new TimeRange(start, start.AddMinutes(45)),
            "Migration sentinel",
            "This entry must survive the settings migration.",
            ActivityCategory.Planning,
            ProductivityKind.Focused,
            ["migration"],
            start.AddHours(1));
        await timelineRepository.AddAsync(entry);

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP TABLE app_settings;
                DELETE FROM schema_migrations WHERE version = 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();

        var restored = await timelineRepository.GetByIdAsync(entry.Id);
        Assert.NotNull(restored);
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(entry.Range, restored.Range);
        Assert.Equal(entry.Title, restored.Title);
        Assert.Equal(entry.Summary, restored.Summary);
        Assert.Equal(entry.Tags, restored.Tags);
    }

    [Fact]
    public async Task InitializeAsyncRecordsEachMigrationVersionOnlyOnce()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

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
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task VersionTwoCreatesPrivacyPreservingDefaultSettings()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                theme,
                capture_enabled,
                cloud_analysis_enabled,
                capture_consent_version,
                capture_consent_granted_at_utc
            FROM app_settings;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
        Assert.False(await reader.ReadAsync());
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_rootDirectory, "windayflow.db");

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
                // The operating system will clean temporary data if a provider handle lingers.
            }
        }
    }
}
