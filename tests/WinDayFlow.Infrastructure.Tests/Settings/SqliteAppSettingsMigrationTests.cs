using System.Reflection;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Settings;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Settings;

public sealed class SqliteAppSettingsMigrationTests
{
    private const string ProfileId = "72c1d90a-361e-42fa-bf7b-27e319d9c532";

    [Fact]
    public async Task FreshDatabaseAppliesSchemaV15ExactlyOnce()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        var initializer = new SqliteDatabaseInitializer(factory);

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(15, reader.GetInt32(0));
        Assert.Equal(15, reader.GetInt32(1));

        var settings = await new SqliteAppSettingsRepository(factory).GetAsync();
        Assert.Equal(AppSettings.Default, settings);
    }

    [Fact]
    public async Task V13ResetsDevelopmentEvidenceAndMigratesOnlyTimelineRoute()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await CreateVersion12DatabaseAsync(factory);
        await SeedVersion12DevelopmentDataAsync(factory);
        database.CreateInternalArtifacts();
        var externalExport = database.CreateExternalExport();

        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        await using var connection = await factory.OpenConnectionAsync();
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM capture_chunks;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM analysis_jobs;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM timeline_entries;"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT is_active FROM ai_provider_profiles WHERE id = '" + ProfileId + "';"));
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT length(api_key_ciphertext) FROM ai_provider_profiles WHERE id = '" + ProfileId + "';"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT maximum_concurrency FROM ai_provider_profiles WHERE id = '" + ProfileId + "';"));

        await using (var route = connection.CreateCommand())
        {
            route.CommandText = "SELECT stage, provider_profile_id, enabled FROM analysis_stage_bindings ORDER BY stage;";
            await using var reader = await route.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((int)AnalysisStage.PrivacyInspection, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal((int)AnalysisStage.TimelineAnalysis, reader.GetInt32(0));
            Assert.Equal(ProfileId, reader.GetString(1));
            Assert.Equal(1, reader.GetInt32(2));
        }

        var settings = await new SqliteAppSettingsRepository(factory).GetAsync();
        Assert.Equal(AppThemePreference.Dark, settings.Theme);
        Assert.Equal(CaptureIntent.Recording, settings.CaptureIntent);
        Assert.Equal(90, settings.Evidence.RetentionDays);
        Assert.Equal(15, settings.CaptureIntervalSeconds);
        Assert.NotNull(settings.RecordingConsent);
        Assert.Contains(
            settings.Evidence.SendRules.Rules,
            rule => rule.Id == EvidenceSettings.WinDayFlowSendRuleId && rule.Enabled);
        Assert.False(Directory.Exists(database.StagingDirectory));
        Assert.False(Directory.Exists(database.ChunksDirectory));
        Assert.False(Directory.Exists(database.ScreeningsDirectory));
        Assert.False(Directory.Exists(database.CacheDirectory));
        Assert.False(Directory.Exists(database.ExportsDirectory));
        Assert.True(File.Exists(externalExport));
    }

    [Fact]
    public async Task V13CreatesPrivacyAuditContextAndStatisticsTables()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var expected = new[]
        {
            "analysis_stage_bindings",
            "provider_profile_validations",
            "privacy_screenings",
            "provider_invocations",
            "evidence_send_overrides",
            "application_catalog",
            "capture_context_samples",
            "capture_context_rule_matches",
            "app_installation",
        };

        await using var connection = await factory.OpenConnectionAsync();
        foreach (var table in expected)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    private static async Task CreateVersion12DatabaseAsync(
        SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE schema_migrations (version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        for (var version = 1; version <= 12; version++)
        {
            var field = typeof(SqliteDatabaseInitializer).GetField(
                $"MigrationVersion{version}Sql",
                BindingFlags.NonPublic | BindingFlags.Static);
            var sql = Assert.IsType<string>(field?.GetRawConstantValue());
            await using var migration = connection.CreateCommand();
            migration.CommandText = sql;
            await migration.ExecuteNonQueryAsync();
            await using var record = connection.CreateCommand();
            record.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES ($version, '2026-07-16T00:00:00.0000000+00:00');";
            record.Parameters.AddWithValue("$version", version);
            await record.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedVersion12DevelopmentDataAsync(
        SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_settings
            SET theme = 2,
                capture_enabled = 1,
                cloud_analysis_enabled = 1,
                capture_consent_version = 2,
                capture_consent_granted_at_utc = '2026-07-16T03:04:05.0000000+00:00',
                evidence_retention_days = 90,
                capture_interval_seconds = 15
            WHERE id = 1;

            INSERT INTO ai_provider_profiles(
                id, display_name, kind, base_endpoint, model,
                request_timeout_ticks, revision, is_active,
                api_key_ciphertext, api_key_salt, api_key_protection_version,
                validated_revision, validated_at_utc_ticks,
                created_at_utc_ticks, updated_at_utc_ticks)
            VALUES (
                '72c1d90a-361e-42fa-bf7b-27e319d9c532',
                'Legacy provider', 0, 'https://example.test/v1', 'vision-model',
                300000000, 3, 1,
                X'01020304', zeroblob(32), 1,
                3, 1000, 900, 1000);

            INSERT INTO capture_chunks(
                id, manifest_relative_path, start_utc_ticks, start_offset_minutes,
                end_utc_ticks, end_offset_minutes, captured_frame_count, frame_count,
                frame_width, frame_height, frame_byte_count,
                persistence_generation_hex, target_epoch_hex,
                committed_at_utc_ticks, ingested_at_utc_ticks, availability)
            VALUES (
                'legacy-chunk', 'chunks/legacy-chunk/manifest.json',
                100, 0, 200, 0, 1, 1, 2, 2, 4,
                '0000000000000001', '0000000000000001', 200, 201, 0);

            INSERT INTO analysis_jobs(
                id, capture_chunk_id, provider_profile_id, provider_profile_revision,
                analysis_version, input_fingerprint, state, attempt, max_attempts,
                not_before_utc_ticks, error_code, created_at_utc_ticks, updated_at_utc_ticks)
            VALUES (
                '11111111-1111-1111-1111-111111111111', 'legacy-chunk',
                '72c1d90a-361e-42fa-bf7b-27e319d9c532', 3,
                'legacy', printf('%064d', 0), 0, 0, 3, 300, 0, 300, 300);

            INSERT INTO timeline_entries(
                id, local_date, start_utc_ticks, start_offset_minutes,
                end_utc_ticks, end_offset_minutes, title, summary,
                category, productivity, origin, revision, confidence)
            VALUES (
                '22222222-2222-2222-2222-222222222222', '2026-07-16',
                100, 0, 200, 0, 'Legacy entry', '', 0, 0, 1, 0, NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly string _externalRoot = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.ExternalExports.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_root, "windayflow.db");
        public string StagingDirectory => Path.Combine(_root, ".staging");
        public string ChunksDirectory => Path.Combine(_root, "chunks");
        public string ScreeningsDirectory => Path.Combine(_root, "screenings");
        public string CacheDirectory => Path.Combine(_root, "cache");
        public string ExportsDirectory => Path.Combine(_root, "exports");

        public void CreateInternalArtifacts()
        {
            foreach (var directory in new[]
                     {
                         StagingDirectory,
                         ChunksDirectory,
                         ScreeningsDirectory,
                         CacheDirectory,
                         ExportsDirectory,
                     })
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "development-data.bin"), "old");
            }
        }

        public string CreateExternalExport()
        {
            Directory.CreateDirectory(_externalRoot);
            var path = Path.Combine(_externalRoot, "user-export.mp4");
            File.WriteAllText(path, "keep");
            return path;
        }

        public void Dispose()
        {
            foreach (var path in new[] { _root, _externalRoot })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
