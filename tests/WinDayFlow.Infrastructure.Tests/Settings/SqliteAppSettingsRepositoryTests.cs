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
            Revision: 2,
            ApplicationPrivacyMode:
                CaptureApplicationPrivacyMode.AllowAllApplications);
        var consent = new RecordingConsent(
            AppSettingsService.CurrentRecordingConsentVersion,
            new DateTimeOffset(2026, 7, 16, 3, 4, 5, TimeSpan.Zero).AddTicks(6789),
            privacy.Revision);
        var expected = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            consent,
            privacy,
            CaptureIntervalSeconds: 30);

        await new SqliteAppSettingsRepository(factory).SaveAsync(
            AppSettings.Default,
            expected);

        var restored = await new SqliteAppSettingsRepository(
                new SqliteConnectionFactory(database.DatabasePath))
            .GetAsync();
        Assert.Equal(expected, restored);
        Assert.Equal(consent.PolicyVersion, restored.RecordingConsent?.PolicyVersion);
        Assert.Equal(consent.AcceptedAtUtc, restored.RecordingConsent?.AcceptedAtUtc);
        Assert.Equal(consent.PrivacyRevision, restored.RecordingConsent?.PrivacyRevision);
        Assert.Equal(privacy, restored.CapturePrivacy);
        Assert.Equal(30, restored.CaptureIntervalSeconds);
        Assert.Equal(
            CaptureApplicationPrivacyMode.AllowAllApplications,
            restored.CapturePrivacy.ApplicationPrivacyMode);
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
    public async Task GetAsyncRejectsCorruptApplicationPrivacyModeEvenIfChecksWereBypassed()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();

        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE app_settings
                SET capture_application_privacy_mode = 99
                WHERE id = 1;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAppSettingsRepository(factory);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncRequiresPrivacyRevisionAdvanceForApplicationModeChange()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var current = AppSettings.Default.CapturePrivacy;
        var invalidPrivacy = new CapturePrivacySettings(
            current.EvidenceRetentionDays,
            current.ExcludeSensitiveApplications,
            current.PauseInRemoteSessions,
            current.PauseDuringScreenSharing,
            current.Revision,
            current.ExclusionRules,
            CaptureApplicationPrivacyMode.AllowAllApplications);
        var invalid = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            RecordingConsent: null,
            invalidPrivacy);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(AppSettings.Default, invalid));

        var changedPrivacy = current.ChangeApplicationPrivacyMode(
            CaptureApplicationPrivacyMode.AllowAllApplications);
        var changed = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            RecordingConsent: null,
            changedPrivacy);
        await repository.SaveAsync(AppSettings.Default, changed);

        Assert.Equal(changed, await repository.GetAsync());
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

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.SaveAsync(AppSettings.Default, changed));

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
            () => repository.SaveAsync(
                AppSettings.Default,
                changed,
                cancellation.Token));

        Assert.Equal(AppSettings.Default, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncRoundTripsEveryRuleTypeInStableOrder()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var rules = new CaptureExclusionRuleSet(
        [
            new CaptureExclusionRule(
                Guid.Parse("ec27e2d7-c8a7-4278-b5e3-4f3a077b8e16"),
                "Executable",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "private.exe",
                null,
                null,
                revision: 1),
            CaptureExclusionRule.Create(
                Guid.Parse("513f40a8-05d7-41e0-bff5-e1407574cd50"),
                "Package window",
                enabled: false,
                CaptureExclusionRuleScope.Window,
                ApplicationIdentityKind.PackageFamilyName,
                "Contoso.Browser_123456789abcd",
                WindowTitleMatchKind.Exact,
                " Secret "),
            CaptureExclusionRule.Create(
                Guid.Parse("bf5ef208-2b41-48cc-89d9-06655038e21e"),
                "Publisher",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.PublisherCertificateSha256,
                new string('A', CaptureExclusionRule.PublisherCertificateSha256Length)),
        ]);
        var privacy = AppSettings.Default.CapturePrivacy.ChangeRules(rules);
        var proposed = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null,
            privacy);

        await repository.SaveAsync(AppSettings.Default, proposed);

        var restored = await new SqliteAppSettingsRepository(
                new SqliteConnectionFactory(database.DatabasePath))
            .GetAsync();
        Assert.Equal(proposed, restored);
        Assert.Equal(
            rules.Rules.Select(static rule => rule.Id),
            restored.CapturePrivacy.ExclusionRules.Rules.Select(static rule => rule.Id));
        Assert.Equal(WindowTitleMatchKind.Exact, restored.CapturePrivacy.ExclusionRules[1].WindowTitleMatchKind);
        Assert.Equal(" Secret ", restored.CapturePrivacy.ExclusionRules[1].Pattern);
    }

    [Fact]
    public async Task SaveAsyncRejectsEnabledRuleIdentifierReplacementWithoutPrivacyTransition()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var original = CreateApplicationRule(
            Guid.NewGuid(),
            "private.exe",
            enabled: true);
        var expected = WithRules(AppSettings.Default, [original]);
        await repository.SaveAsync(AppSettings.Default, expected);
        var replacement = CopyRule(original, id: Guid.NewGuid(), revision: 1);
        var privacy = expected.CapturePrivacy;
        var invalidPrivacy = new CapturePrivacySettings(
            privacy.EvidenceRetentionDays,
            privacy.ExcludeSensitiveApplications,
            privacy.PauseInRemoteSessions,
            privacy.PauseDuringScreenSharing,
            privacy.Revision,
            new CaptureExclusionRuleSet([replacement]));
        var invalid = new AppSettings(
            expected.Theme,
            CaptureEnabled: false,
            expected.CloudAnalysisEnabled,
            expected.RecordingConsent,
            invalidPrivacy);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(expected, invalid));

        Assert.Equal(expected, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncRejectsRuleRevisionRegressionJumpAndUnchangedAdvance()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var original = CreateApplicationRule(
            Guid.NewGuid(),
            "draft.exe",
            enabled: false);
        var expected = WithRules(AppSettings.Default, [original]);
        await repository.SaveAsync(AppSettings.Default, expected);

        var unchangedAdvance = WithRules(
            expected,
            [CopyRule(original, revision: 2)]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(expected, unchangedAdvance));
        Assert.Equal(expected, await repository.GetAsync());

        var jumped = WithRules(
            expected,
            [CopyRule(original, name: "Changed draft", revision: 3)]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(expected, jumped));
        Assert.Equal(expected, await repository.GetAsync());

        var advancedRule = CopyRule(original, name: "Changed draft", revision: 2);
        var advanced = WithRules(expected, [advancedRule]);
        await repository.SaveAsync(expected, advanced);

        var regressed = WithRules(
            advanced,
            [CopyRule(advancedRule, revision: 1)]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(advanced, regressed));
        Assert.Equal(advanced, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncAllowsCrossScopeReorderWithoutPrivacyRevisionChange()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var application = CreateApplicationRule(
            Guid.NewGuid(),
            "private.exe",
            enabled: true);
        var window = CreateWindowRule(Guid.NewGuid(), "Private");
        var expected = WithRules(AppSettings.Default, [application, window]);
        await repository.SaveAsync(AppSettings.Default, expected);

        var movedWindow = CopyRule(window, revision: 2);
        var proposed = WithRules(expected, [movedWindow, application]);
        await repository.SaveAsync(expected, proposed);

        Assert.Equal(
            expected.CapturePrivacy.Revision,
            proposed.CapturePrivacy.Revision);
        Assert.Equal(proposed, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncRequiresMovedRuleRevisionAndPrivacyChangeForSameScopeReorder()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var first = CreateApplicationRule(
            Guid.NewGuid(),
            "first.exe",
            enabled: true);
        var second = CreateApplicationRule(
            Guid.NewGuid(),
            "second.exe",
            enabled: true);
        var expected = WithRules(AppSettings.Default, [first, second]);
        await repository.SaveAsync(AppSettings.Default, expected);

        var missingRuleRevision = WithRules(expected, [second, first]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(expected, missingRuleRevision));
        Assert.Equal(expected, await repository.GetAsync());

        var movedSecond = CopyRule(second, revision: 2);
        var proposed = WithRules(expected, [movedSecond, first]);
        await repository.SaveAsync(expected, proposed);

        Assert.Equal(
            expected.CapturePrivacy.Revision + 1,
            proposed.CapturePrivacy.Revision);
        Assert.Equal(proposed, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncRejectsInvalidPrivacyTransitionWithoutChangingDatabase()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var rules = new CaptureExclusionRuleSet(
        [
            CaptureExclusionRule.Create(
                Guid.NewGuid(),
                "Private app",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "private.exe"),
        ]);
        var invalidPrivacy = new CapturePrivacySettings(
            CapturePrivacySettings.DefaultRetentionDays,
            ExcludeSensitiveApplications: true,
            PauseInRemoteSessions: true,
            PauseDuringScreenSharing: true,
            Revision: AppSettings.Default.CapturePrivacy.Revision,
            rules);
        var invalid = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            RecordingConsent: null,
            invalidPrivacy);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.SaveAsync(AppSettings.Default, invalid));
        Assert.Equal(AppSettings.Default, await repository.GetAsync());
    }

    [Fact]
    public async Task SaveAsyncRejectsAStaleExpectedSnapshot()
    {
        using var database = new TemporaryDatabase();
        var repository = await CreateRepositoryAsync(database.DatabasePath);
        var first = new AppSettings(
            AppThemePreference.Dark,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            RecordingConsent: null);
        var stale = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null);
        await repository.SaveAsync(AppSettings.Default, first);

        await Assert.ThrowsAsync<AppSettingsConcurrencyException>(
            () => repository.SaveAsync(AppSettings.Default, stale));

        Assert.Equal(first, await repository.GetAsync());
    }

    [Fact]
    public async Task RuleInsertFailureRollsBackSettingsAndTheEntireOrderedRuleSet()
    {
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.DatabasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var repository = new SqliteAppSettingsRepository(factory);
        var firstRule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "First",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "first.exe");
        var firstPrivacy = AppSettings.Default.CapturePrivacy.ChangeRules(
            new CaptureExclusionRuleSet([firstRule]));
        var expected = new AppSettings(
            AppThemePreference.System,
            CaptureEnabled: false,
            CloudAnalysisEnabled: false,
            RecordingConsent: null,
            firstPrivacy);
        await repository.SaveAsync(AppSettings.Default, expected);
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_capture_exclusion_rule
                BEFORE INSERT ON capture_exclusion_rules
                WHEN NEW.name = 'Rejected'
                BEGIN
                    SELECT RAISE(ABORT, 'forced capture exclusion rule failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var rejectedRule = CaptureExclusionRule.Create(
            Guid.NewGuid(),
            "Rejected",
            enabled: true,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            "rejected.exe");
        var changedPrivacy = expected.CapturePrivacy.ChangeRules(
            new CaptureExclusionRuleSet([firstRule, rejectedRule]));
        var proposed = new AppSettings(
            AppThemePreference.Light,
            CaptureEnabled: false,
            CloudAnalysisEnabled: true,
            RecordingConsent: null,
            changedPrivacy);

        await Assert.ThrowsAsync<SqliteException>(
            () => repository.SaveAsync(expected, proposed));

        Assert.Equal(expected, await repository.GetAsync());
    }

    private static async Task<SqliteAppSettingsRepository> CreateRepositoryAsync(
        string databasePath)
    {
        var factory = new SqliteConnectionFactory(databasePath);
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        return new SqliteAppSettingsRepository(factory);
    }

    private static CaptureExclusionRule CreateApplicationRule(
        Guid id,
        string executableName,
        bool enabled)
    {
        return CaptureExclusionRule.Create(
            id,
            executableName,
            enabled,
            CaptureExclusionRuleScope.Application,
            ApplicationIdentityKind.ExecutableName,
            executableName);
    }

    private static CaptureExclusionRule CreateWindowRule(Guid id, string pattern)
    {
        return CaptureExclusionRule.Create(
            id,
            "Private window",
            enabled: true,
            CaptureExclusionRuleScope.Window,
            ApplicationIdentityKind.ExecutableName,
            "browser.exe",
            WindowTitleMatchKind.Contains,
            pattern);
    }

    private static CaptureExclusionRule CopyRule(
        CaptureExclusionRule rule,
        Guid? id = null,
        string? name = null,
        long? revision = null)
    {
        return new CaptureExclusionRule(
            id ?? rule.Id,
            name ?? rule.Name,
            rule.Enabled,
            rule.Scope,
            rule.ApplicationIdentityKind,
            rule.IdentityValue,
            rule.WindowTitleMatchKind,
            rule.Pattern,
            revision ?? rule.Revision);
    }

    private static AppSettings WithRules(
        AppSettings settings,
        IReadOnlyList<CaptureExclusionRule> rules)
    {
        var privacy = settings.CapturePrivacy.ChangeRules(
            new CaptureExclusionRuleSet(rules));
        return new AppSettings(
            settings.Theme,
            CaptureEnabled: privacy.Revision == settings.CapturePrivacy.Revision
                && settings.CaptureEnabled,
            settings.CloudAnalysisEnabled,
            settings.RecordingConsent,
            privacy);
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
