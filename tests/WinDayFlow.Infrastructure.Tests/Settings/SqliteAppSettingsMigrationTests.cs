using System.Globalization;
using Microsoft.Data.Sqlite;
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
                DROP INDEX ix_analysis_jobs_provider_revision_state;
                DROP TABLE ai_provider_profiles;
                DROP TABLE analysis_jobs;
                DROP TABLE capture_chunks;
                DROP TABLE capture_exclusion_rules;
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
    public async Task VersionFourPreservesPatternWhitespaceButRejectsPaddedMetadata()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        await using var connection = await factory.OpenConnectionAsync();

        await using (var insertPattern = connection.CreateCommand())
        {
            insertPattern.CommandText = """
                INSERT INTO capture_exclusion_rules(
                    settings_id,
                    rule_id,
                    ordinal,
                    name,
                    enabled,
                    scope,
                    application_identity_kind,
                    identity_value,
                    window_title_match_kind,
                    pattern,
                    revision)
                VALUES (
                    1,
                    'b330ea53-4180-4855-892a-f373b00b6bad',
                    0,
                    'Exact window',
                    0,
                    1,
                    0,
                    'browser.exe',
                    0,
                    ' Secret ',
                    1);
                """;
            await insertPattern.ExecuteNonQueryAsync();
        }

        await using (var readPattern = connection.CreateCommand())
        {
            readPattern.CommandText = """
                SELECT pattern
                FROM capture_exclusion_rules
                WHERE rule_id = 'b330ea53-4180-4855-892a-f373b00b6bad';
                """;
            Assert.Equal(" Secret ", await readPattern.ExecuteScalarAsync());
        }

        await using (var insertPaddedName = connection.CreateCommand())
        {
            insertPaddedName.CommandText = """
                INSERT INTO capture_exclusion_rules(
                    settings_id, rule_id, ordinal, name, enabled, scope,
                    application_identity_kind, identity_value,
                    window_title_match_kind, pattern, revision)
                VALUES (
                    1, 'b482058a-348e-426d-9935-b460a606af41', 1,
                    ' Padded name ', 0, 0, 0, 'other.exe', NULL, NULL, 1);
                """;
            await Assert.ThrowsAsync<SqliteException>(
                () => insertPaddedName.ExecuteNonQueryAsync());
        }

        await using var insertPaddedIdentity = connection.CreateCommand();
        insertPaddedIdentity.CommandText = """
            INSERT INTO capture_exclusion_rules(
                settings_id, rule_id, ordinal, name, enabled, scope,
                application_identity_kind, identity_value,
                window_title_match_kind, pattern, revision)
            VALUES (
                1, '860a2f58-97c3-42e8-8a27-d89a174a4046', 1,
                'Padded identity', 0, 0, 0, ' other.exe ', NULL, NULL, 1);
            """;
        await Assert.ThrowsAsync<SqliteException>(
            () => insertPaddedIdentity.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task VersionThreeDatabasePreservesDataAndForcesCloudAnalysisOff()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();
        var timelineRepository = new SqliteTimelineRepository(factory);
        var start = new DateTimeOffset(2026, 7, 16, 9, 15, 0, TimeSpan.FromHours(8));
        var entry = TimelineEntry.CreateManual(
            Guid.Parse("2f7bc346-9023-4e09-8671-e00ab43811d3"),
            new TimeRange(start, start.AddMinutes(30)),
            "Version three sentinel",
            "Schema v4 must not alter existing timeline data.",
            ActivityCategory.Communication,
            ProductivityKind.Neutral,
            ["schema-v3"],
            start.AddHours(1));
        await timelineRepository.AddAsync(entry);
        const string acceptedAt = "2026-07-16T01:02:03.0000000+00:00";

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX ix_analysis_jobs_provider_revision_state;
                DROP TABLE ai_provider_profiles;
                DROP TABLE analysis_jobs;
                DROP TABLE capture_chunks;
                DROP TABLE capture_exclusion_rules;
                DELETE FROM schema_migrations WHERE version >= 4;
                UPDATE app_settings
                SET theme = 2,
                    capture_enabled = 1,
                    cloud_analysis_enabled = 1,
                    capture_consent_version = 2,
                    capture_consent_granted_at_utc = '2026-07-16T01:02:03.0000000+00:00',
                    capture_consent_privacy_revision = 7,
                    evidence_retention_days = 90,
                    exclude_sensitive_applications = 0,
                    pause_in_remote_sessions = 0,
                    pause_during_screen_sharing = 1,
                    capture_privacy_revision = 7
                WHERE id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();

        var expectedPrivacy = new CapturePrivacySettings(
            EvidenceRetentionDays: 90,
            ExcludeSensitiveApplications: false,
            PauseInRemoteSessions: false,
            PauseDuringScreenSharing: true,
            Revision: 7);
        var expected = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: true,
            CloudAnalysisEnabled: false,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion,
                DateTimeOffset.ParseExact(
                    acceptedAt,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None),
                PrivacyRevision: 7),
            expectedPrivacy);
        var settings = await new SqliteAppSettingsRepository(factory).GetAsync();
        Assert.Equal(expected, settings);
        Assert.Empty(settings.CapturePrivacy.ExclusionRules.Rules);

        var restored = await timelineRepository.GetByIdAsync(entry.Id);
        Assert.NotNull(restored);
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(entry.Range, restored.Range);
        Assert.Equal(entry.Title, restored.Title);
        Assert.Equal(entry.Summary, restored.Summary);
        Assert.Equal(entry.Tags, restored.Tags);

        await using var migratedConnection = await factory.OpenConnectionAsync();
        await using var countRules = migratedConnection.CreateCommand();
        countRules.CommandText = "SELECT COUNT(*) FROM capture_exclusion_rules;";
        Assert.Equal(0L, await countRules.ExecuteScalarAsync());
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
                DROP INDEX ix_analysis_jobs_provider_revision_state;
                DROP TABLE ai_provider_profiles;
                DROP TABLE analysis_jobs;
                DROP TABLE capture_chunks;
                DROP TABLE capture_exclusion_rules;
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
                DELETE FROM schema_migrations WHERE version >= 3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();

        var settings = await new SqliteAppSettingsRepository(factory).GetAsync();
        Assert.Equal(AppThemePreference.Dark, settings.Theme);
        Assert.False(settings.CaptureEnabled);
        Assert.False(settings.CloudAnalysisEnabled);
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
