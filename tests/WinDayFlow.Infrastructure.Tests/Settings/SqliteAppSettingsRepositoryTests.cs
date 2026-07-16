using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Settings;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Settings;

public sealed class SqliteAppSettingsRepositoryTests
{
    [Fact]
    public async Task GetAsyncReturnsTheMigratedDefaultRow()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);

        var settings = await repository.GetAsync();

        Assert.Equal(AppSettings.Default, settings);
    }

    [Fact]
    public async Task SaveAsyncRoundTripsEverySettingAcrossRepositoryInstances()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var privacy = new CapturePrivacySettings(
            EvidenceRetentionDays: 90,
            ExcludeSensitiveApplications: false,
            PauseInRemoteSessions: true,
            PauseDuringScreenSharing: false,
            Revision: 7);
        var consent = new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            new DateTimeOffset(2026, 7, 16, 3, 4, 5, TimeSpan.Zero).AddTicks(6789),
            privacy.Revision);
        var expected = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: true,
            CloudAnalysisEnabled: true,
            consent,
            privacy);

        await new SqliteAppSettingsRepository(factory).SaveAsync(expected);

        var restored = await new SqliteAppSettingsRepository(
                new SqliteConnectionFactory(database.DatabasePath))
            .GetAsync();
        Assert.Equal(expected, restored);
        Assert.Equal(consent.PolicyVersion, restored.RecordingConsent?.PolicyVersion);
        Assert.Equal(consent.AcceptedAtUtc, restored.RecordingConsent?.AcceptedAtUtc);
        Assert.Equal(consent.PrivacyRevision, restored.RecordingConsent?.PrivacyRevision);
        Assert.Equal(privacy, restored.CapturePrivacy);
    }

    [Fact]
    public async Task DatabaseRejectsCaptureWithoutRecordedConsent()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_settings
            SET capture_enabled = 1
            WHERE id = 1;
            """;

        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(
            AppSettings.Default,
            await new SqliteAppSettingsRepository(factory).GetAsync());
    }

    [Fact]
    public async Task DatabaseRejectsCaptureWhenConsentCoversAnotherPrivacyRevision()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_settings
            SET capture_consent_version = 2,
                capture_consent_granted_at_utc = '2026-07-16T03:04:05.0000000+00:00',
                capture_consent_privacy_revision = 2,
                capture_enabled = 1
            WHERE id = 1;
            """;

        await Assert.ThrowsAsync<SqliteException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(
            AppSettings.Default,
            await new SqliteAppSettingsRepository(factory).GetAsync());
    }

    [Fact]
    public async Task GetAsyncRejectsCorruptValuesEvenIfChecksWereBypassed()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE app_settings SET theme = 99 WHERE id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAppSettingsRepository(factory);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetAsync());
    }

    [Fact]
    public async Task FailedSaveRollsBackTheSettingsUpdate()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER delete_settings_after_update
                AFTER UPDATE ON app_settings
                BEGIN
                    DELETE FROM app_settings WHERE id = 1;
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAppSettingsRepository(factory);
        var changed = new AppSettings(
            AppThemePreference.Light,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(changed));

        Assert.Equal(AppSettings.Default, await repository.GetAsync());
    }

    [Fact]
    public async Task OperationsHonorPreCanceledTokensWithoutChangingSettings()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var changed = new AppSettings(
            AppThemePreference.Light,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.SaveAsync(changed, cancellation.Token));

        Assert.Equal(AppSettings.Default, await repository.GetAsync());
    }

    private static async Task<SqliteAppSettingsRepository> CreateRepositoryAsync(
        string databasePath)
    {
        var factory = new SqliteConnectionFactory(databasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        return new SqliteAppSettingsRepository(factory);
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
