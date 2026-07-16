using System.Globalization;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
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
                DELETE FROM schema_migrations WHERE version >= 2;
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
        Assert.Equal(
            CapturePrivacySettings.Default,
            (await new SqliteAppSettingsRepository(factory).GetAsync()).CapturePrivacy);
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
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task VersionThreeCreatesPrivacyPreservingDefaultSettings()
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
                capture_consent_granted_at_utc,
                capture_consent_privacy_revision,
                evidence_retention_days,
                exclude_sensitive_applications,
                pause_in_remote_sessions,
                pause_during_screen_sharing,
                capture_privacy_revision
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
        Assert.True(reader.IsDBNull(6));
        Assert.Equal(30, reader.GetInt32(7));
        Assert.Equal(1, reader.GetInt32(8));
        Assert.Equal(1, reader.GetInt32(9));
        Assert.Equal(1, reader.GetInt32(10));
        Assert.Equal(1, reader.GetInt64(11));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task VersionTwoConsentIsPreservedButCannotKeepCaptureEnabled()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();
        const string acceptedAt = "2026-07-16T03:04:05.0000000+00:00";

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE app_settings RENAME TO app_settings_v3;
                CREATE TABLE app_settings (
                    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                    theme INTEGER NOT NULL CHECK (theme BETWEEN 0 AND 2),
                    capture_enabled INTEGER NOT NULL CHECK (capture_enabled IN (0, 1)),
                    cloud_analysis_enabled INTEGER NOT NULL CHECK (cloud_analysis_enabled IN (0, 1)),
                    capture_consent_version INTEGER NULL CHECK (
                        capture_consent_version IS NULL OR capture_consent_version > 0
                    ),
                    capture_consent_granted_at_utc TEXT NULL,
                    CHECK (
                        (capture_consent_version IS NULL AND capture_consent_granted_at_utc IS NULL)
                        OR
                        (capture_consent_version IS NOT NULL AND capture_consent_granted_at_utc IS NOT NULL)
                    ),
                    CHECK (
                        capture_enabled = 0
                        OR capture_consent_version IS NOT NULL
                    )
                );
                INSERT INTO app_settings(
                    id,
                    theme,
                    capture_enabled,
                    cloud_analysis_enabled,
                    capture_consent_version,
                    capture_consent_granted_at_utc)
                VALUES (1, 2, 1, 1, 1, '2026-07-16T03:04:05.0000000+00:00');
                DROP TABLE app_settings_v3;
                DELETE FROM schema_migrations WHERE version = 3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();

        var settings = await new SqliteAppSettingsRepository(factory).GetAsync();
        Assert.Equal(AppThemePreference.Dark, settings.Theme);
        Assert.False(settings.CaptureEnabled);
        Assert.True(settings.CloudAnalysisEnabled);
        Assert.Equal(1, settings.RecordingConsent?.PolicyVersion);
        Assert.Equal(
            DateTimeOffset.ParseExact(
                acceptedAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None),
            settings.RecordingConsent?.AcceptedAtUtc);
        Assert.Null(settings.RecordingConsent?.PrivacyRevision);
        Assert.Equal(CapturePrivacySettings.Default, settings.CapturePrivacy);
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
