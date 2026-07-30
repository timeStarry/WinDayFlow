using WinDayFlow.Application.Settings;
using WinDayFlow.Infrastructure.Persistence;
using WinDayFlow.Infrastructure.Settings;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Settings;

public sealed class SqliteAppSettingsRepositoryTests
{
    [Fact]
    public async Task V13SettingsRoundTripPreservesIntentConsentAndSendRules()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(factory);
        var current = await repository.GetAsync();
        var customRule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Password manager",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "password-manager.exe");
        var evidence = current.Evidence
            .ChangeRetentionDays(90)
            .ChangeSendRules(new CaptureExclusionRuleSet(
                [.. current.Evidence.SendRules.Rules, customRule]));
        var proposed = new AppSettings(
            AppThemePreference.Dark,
            new RecordingConsent(
                AppSettingsService.CurrentRecordingConsentVersion,
                new DateTimeOffset(2026, 7, 16, 3, 4, 5, TimeSpan.Zero)),
            evidence,
            CaptureIntervalSeconds: 30,
            CaptureIntent.Paused);

        await repository.SaveAsync(current, proposed);

        Assert.Equal(proposed, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveUsesWholeSnapshotCompareAndSwap()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var first = new SqliteAppSettingsRepository(factory);
        var second = new SqliteAppSettingsRepository(factory);
        var stale = await first.GetAsync();
        var winner = new AppSettings(
            AppThemePreference.Dark,
            stale.RecordingConsent,
            stale.Evidence,
            stale.CaptureIntervalSeconds,
            stale.CaptureIntent);
        await first.SaveAsync(stale, winner);

        var loser = new AppSettings(
            AppThemePreference.Light,
            stale.RecordingConsent,
            stale.Evidence,
            stale.CaptureIntervalSeconds,
            stale.CaptureIntent);

        await Assert.ThrowsAsync<AppSettingsConcurrencyException>(
            () => second.SaveAsync(stale, loser));
        Assert.Equal(winner, await first.GetAsync());
    }

    [Fact]
    public async Task EffectiveSendRuleChangesMustAdvanceRulesRevisionExactlyOnce()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(factory);
        var current = await repository.GetAsync();
        var rule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Editor",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "editor.exe");
        var invalidEvidence = new EvidenceSettings(
            current.Evidence.RetentionDays,
            current.Evidence.RulesRevision + 2,
            new CaptureExclusionRuleSet(
                [.. current.Evidence.SendRules.Rules, rule]));
        var invalid = new AppSettings(
            current.Theme,
            current.RecordingConsent,
            invalidEvidence,
            current.CaptureIntervalSeconds,
            current.CaptureIntent);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(current, invalid));
        Assert.Equal(current, await repository.GetAsync());
    }

    [Fact]
    public async Task CorruptCaptureIntentIsRejectedWhenMaterialized()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE app_settings SET capture_intent = 99 WHERE id = 1;";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SqliteAppSettingsRepository(factory).GetAsync());
    }

    [Fact]
    public async Task LegacyCloudAndPrivacyColumnsRemainDisabledOnWrite()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(factory);
        var current = await repository.GetAsync();
        var proposed = new AppSettings(
            AppThemePreference.Dark,
            current.RecordingConsent,
            current.Evidence,
            current.CaptureIntervalSeconds,
            current.CaptureIntent);
        await repository.SaveAsync(current, proposed);

        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capture_enabled, cloud_analysis_enabled,
                   exclude_sensitive_applications, pause_in_remote_sessions,
                   pause_during_screen_sharing
            FROM app_settings WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(0, reader.GetInt32(index));
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinDayFlow.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(_root, "windayflow.db");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
