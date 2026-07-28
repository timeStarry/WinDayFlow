using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Settings;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Settings;

public sealed class SqliteAppSettingsRepository : IAppSettingsRepository
{
    private const string SelectSettingsSql = """
        SELECT
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
            capture_privacy_revision,
            capture_application_privacy_mode
        FROM app_settings
        WHERE id = 1;
        """;

    private const string SelectRulesSql = """
        SELECT
            ordinal,
            rule_id,
            name,
            enabled,
            scope,
            application_identity_kind,
            identity_value,
            window_title_match_kind,
            pattern,
            revision
        FROM capture_exclusion_rules
        WHERE settings_id = 1
        ORDER BY ordinal, rule_id;
        """;

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteAppSettingsRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<AppSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var settings = await ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public async Task SaveAsync(
        AppSettings expected,
        AppSettings proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(proposed);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var persistedBefore = await ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (persistedBefore != expected)
        {
            throw new AppSettingsConcurrencyException();
        }

        ValidateTransition(expected, proposed);
        await UpdateSettingsAsync(connection, transaction, proposed, cancellationToken)
            .ConfigureAwait(false);
        await ReplaceRulesAsync(
                connection,
                transaction,
                proposed.CapturePrivacy.ExclusionRules,
                cancellationToken)
            .ConfigureAwait(false);

        var persistedAfter = await ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (persistedAfter != proposed)
        {
            throw new InvalidDataException(
                "The persisted application settings did not match the requested snapshot.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateTransition(AppSettings expected, AppSettings proposed)
    {
        ValidateRuleTransition(
            expected.CapturePrivacy.ExclusionRules,
            proposed.CapturePrivacy.ExclusionRules);

        var effectivePrivacyChanged = !expected.CapturePrivacy
            .HasSameEffectivePolicy(proposed.CapturePrivacy);
        if (!effectivePrivacyChanged)
        {
            if (proposed.CapturePrivacy.Revision != expected.CapturePrivacy.Revision)
            {
                throw new InvalidOperationException(
                    "The capture privacy revision cannot change without an effective privacy-policy change.");
            }

            return;
        }

        if (expected.CapturePrivacy.Revision == long.MaxValue
            || proposed.CapturePrivacy.Revision != expected.CapturePrivacy.Revision + 1)
        {
            throw new InvalidOperationException(
                "An effective privacy-policy change must advance the privacy revision exactly once.");
        }

        if (proposed.CaptureEnabled)
        {
            throw new InvalidOperationException(
                "An effective privacy-policy change must disable capture in the same transaction.");
        }
    }

    private static void ValidateRuleTransition(
        CaptureExclusionRuleSet expected,
        CaptureExclusionRuleSet proposed)
    {
        var expectedById = new Dictionary<Guid, (CaptureExclusionRule Rule, int Index)>();
        for (var index = 0; index < expected.Count; index++)
        {
            var rule = expected[index];
            expectedById.Add(rule.Id, (rule, index));
        }

        var orderChanged = HasCommonRuleOrderChanged(expected, proposed);
        var movedRuleAdvanced = false;
        for (var index = 0; index < proposed.Count; index++)
        {
            var rule = proposed[index];
            if (!expectedById.TryGetValue(rule.Id, out var previous))
            {
                if (rule.Revision != 1)
                {
                    throw new InvalidOperationException(
                        "A new capture exclusion rule must start at revision one.");
                }

                continue;
            }

            var contentChanged = !HasSameRuleContent(previous.Rule, rule);
            var positionChanged = previous.Index != index;
            var advancedExactlyOnce = HasAdvancedExactlyOnce(
                previous.Rule.Revision,
                rule.Revision);

            if (contentChanged)
            {
                if (!advancedExactlyOnce)
                {
                    throw new InvalidOperationException(
                        "A changed capture exclusion rule must advance its revision exactly once.");
                }
            }
            else if (rule.Revision != previous.Rule.Revision
                     && !(orderChanged && positionChanged && advancedExactlyOnce))
            {
                throw new InvalidOperationException(
                    "An unchanged capture exclusion rule cannot change its revision.");
            }

            if (orderChanged && positionChanged && advancedExactlyOnce)
            {
                movedRuleAdvanced = true;
            }
        }

        if (orderChanged && !movedRuleAdvanced)
        {
            throw new InvalidOperationException(
                "Reordering capture exclusion rules must advance at least one moved rule revision exactly once.");
        }
    }

    private static bool HasCommonRuleOrderChanged(
        CaptureExclusionRuleSet expected,
        CaptureExclusionRuleSet proposed)
    {
        var expectedIds = expected.Rules
            .Select(static rule => rule.Id)
            .ToHashSet();
        var proposedIds = proposed.Rules
            .Select(static rule => rule.Id)
            .ToHashSet();
        var expectedCommonOrder = expected.Rules
            .Where(rule => proposedIds.Contains(rule.Id))
            .Select(static rule => rule.Id);
        var proposedCommonOrder = proposed.Rules
            .Where(rule => expectedIds.Contains(rule.Id))
            .Select(static rule => rule.Id);
        return !expectedCommonOrder.SequenceEqual(proposedCommonOrder);
    }

    private static bool HasSameRuleContent(
        CaptureExclusionRule expected,
        CaptureExclusionRule proposed)
    {
        return string.Equals(expected.Name, proposed.Name, StringComparison.Ordinal)
            && expected.Enabled == proposed.Enabled
            && expected.Scope == proposed.Scope
            && expected.ApplicationIdentityKind == proposed.ApplicationIdentityKind
            && string.Equals(
                expected.IdentityValue,
                proposed.IdentityValue,
                StringComparison.Ordinal)
            && expected.WindowTitleMatchKind == proposed.WindowTitleMatchKind
            && string.Equals(
                expected.Pattern,
                proposed.Pattern,
                StringComparison.Ordinal);
    }

    private static bool HasAdvancedExactlyOnce(long expected, long proposed)
    {
        return expected < long.MaxValue && proposed == expected + 1;
    }

    private static async Task UpdateSettingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE app_settings
            SET theme = $theme,
                capture_enabled = $capture_enabled,
                cloud_analysis_enabled = $cloud_analysis_enabled,
                capture_consent_version = $capture_consent_version,
                capture_consent_granted_at_utc = $capture_consent_granted_at_utc,
                capture_consent_privacy_revision = $capture_consent_privacy_revision,
                evidence_retention_days = $evidence_retention_days,
                exclude_sensitive_applications = $exclude_sensitive_applications,
                pause_in_remote_sessions = $pause_in_remote_sessions,
                pause_during_screen_sharing = $pause_during_screen_sharing,
                capture_privacy_revision = $capture_privacy_revision,
                capture_application_privacy_mode = $capture_application_privacy_mode
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$theme", (int)settings.Theme);
        command.Parameters.AddWithValue("$capture_enabled", settings.CaptureEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$cloud_analysis_enabled",
            settings.CloudAnalysisEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$capture_consent_version",
            settings.RecordingConsent?.PolicyVersion is int policyVersion
                ? policyVersion
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$capture_consent_granted_at_utc",
            settings.RecordingConsent?.AcceptedAtUtc.ToString(
                "O",
                CultureInfo.InvariantCulture)
            ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$capture_consent_privacy_revision",
            settings.RecordingConsent?.PrivacyRevision is long privacyRevision
                ? privacyRevision
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$evidence_retention_days",
            settings.CapturePrivacy.EvidenceRetentionDays);
        command.Parameters.AddWithValue(
            "$exclude_sensitive_applications",
            settings.CapturePrivacy.ExcludeSensitiveApplications ? 1 : 0);
        command.Parameters.AddWithValue(
            "$pause_in_remote_sessions",
            settings.CapturePrivacy.PauseInRemoteSessions ? 1 : 0);
        command.Parameters.AddWithValue(
            "$pause_during_screen_sharing",
            settings.CapturePrivacy.PauseDuringScreenSharing ? 1 : 0);
        command.Parameters.AddWithValue(
            "$capture_privacy_revision",
            settings.CapturePrivacy.Revision);
        command.Parameters.AddWithValue(
            "$capture_application_privacy_mode",
            (int)settings.CapturePrivacy.ApplicationPrivacyMode);

        var affectedRows = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                "The application settings row has not been initialized.");
        }
    }

    private static async Task ReplaceRulesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureExclusionRuleSet rules,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM capture_exclusion_rules WHERE settings_id = 1;";
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < rules.Count; ordinal++)
        {
            var rule = rules[ordinal];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
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
                    $rule_id,
                    $ordinal,
                    $name,
                    $enabled,
                    $scope,
                    $application_identity_kind,
                    $identity_value,
                    $window_title_match_kind,
                    $pattern,
                    $revision);
                """;
            insert.Parameters.AddWithValue("$rule_id", rule.Id.ToString("D"));
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$name", rule.Name);
            insert.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$scope", (int)rule.Scope);
            insert.Parameters.AddWithValue(
                "$application_identity_kind",
                (int)rule.ApplicationIdentityKind);
            insert.Parameters.AddWithValue("$identity_value", rule.IdentityValue);
            insert.Parameters.AddWithValue(
                "$window_title_match_kind",
                rule.WindowTitleMatchKind is { } matchKind
                    ? (int)matchKind
                    : DBNull.Value);
            insert.Parameters.AddWithValue(
                "$pattern",
                rule.Pattern ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$revision", rule.Revision);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AppSettings> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        AppSettings settings;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SelectSettingsSql;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The application settings row has not been initialized.");
            }

            settings = MaterializeSettings(reader);
        }

        var rules = await ReadRulesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var privacy = settings.CapturePrivacy;
        return new AppSettings(
            settings.Theme,
            settings.CaptureEnabled,
            settings.CloudAnalysisEnabled,
            settings.RecordingConsent,
            new CapturePrivacySettings(
                privacy.EvidenceRetentionDays,
                privacy.ExcludeSensitiveApplications,
                privacy.PauseInRemoteSessions,
                privacy.PauseDuringScreenSharing,
                privacy.Revision,
                rules,
                privacy.ApplicationPrivacyMode));
    }

    private static async Task<CaptureExclusionRuleSet> ReadRulesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SelectRulesSql;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var rules = new List<CaptureExclusionRule>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = checked((int)reader.GetInt64(0));
                if (ordinal != rules.Count)
                {
                    throw new InvalidDataException(
                        "Stored capture exclusion rule ordinals must be contiguous and zero-based.");
                }

                var serializedId = reader.GetString(1);
                if (!Guid.TryParseExact(serializedId, "D", out var id)
                    || !string.Equals(
                        serializedId,
                        id.ToString("D"),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A stored capture exclusion rule identifier is invalid.");
                }

                var windowTitleMatchKind = reader.IsDBNull(7)
                    ? (WindowTitleMatchKind?)null
                    : (WindowTitleMatchKind)checked((int)reader.GetInt64(7));
                rules.Add(new CaptureExclusionRule(
                    id,
                    reader.GetString(2),
                    ReadBoolean(reader, 3, "enabled"),
                    (CaptureExclusionRuleScope)checked((int)reader.GetInt64(4)),
                    (ApplicationIdentityKind)checked((int)reader.GetInt64(5)),
                    reader.GetString(6),
                    windowTitleMatchKind,
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt64(9)));
            }

            return new CaptureExclusionRuleSet(rules);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or FormatException
                                          or InvalidCastException
                                          or OverflowException)
        {
            throw new InvalidDataException(
                "Stored capture exclusion rules are invalid.",
                exception);
        }
    }

    private static AppSettings MaterializeSettings(SqliteDataReader reader)
    {
        try
        {
            var themeValue = checked((int)reader.GetInt64(0));
            var theme = (AppThemePreference)themeValue;
            if (!Enum.IsDefined(theme))
            {
                throw new InvalidDataException(
                    $"Stored application theme value '{themeValue}' is not supported.");
            }

            var captureEnabled = ReadBoolean(reader, 1, "capture_enabled");
            var cloudAnalysisEnabled = ReadBoolean(reader, 2, "cloud_analysis_enabled");
            var consentVersionIsNull = reader.IsDBNull(3);
            var consentTimestampIsNull = reader.IsDBNull(4);
            if (consentVersionIsNull != consentTimestampIsNull)
            {
                throw new InvalidDataException(
                    "Stored recording consent version and timestamp must either both be present or both be absent.");
            }

            RecordingConsent? consent = null;
            if (!consentVersionIsNull)
            {
                var policyVersion = checked((int)reader.GetInt64(3));
                var acceptedAtUtc = DateTimeOffset.ParseExact(
                    reader.GetString(4),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None);
                var consentPrivacyRevision = reader.IsDBNull(5)
                    ? (long?)null
                    : reader.GetInt64(5);
                consent = new RecordingConsent(
                    policyVersion,
                    acceptedAtUtc,
                    consentPrivacyRevision);
            }

            var applicationPrivacyModeValue = checked((int)reader.GetInt64(11));
            var applicationPrivacyMode =
                (CaptureApplicationPrivacyMode)applicationPrivacyModeValue;
            if (!Enum.IsDefined(applicationPrivacyMode))
            {
                throw new InvalidDataException(
                    $"Stored capture application privacy mode value '{applicationPrivacyModeValue}' is not supported.");
            }

            var privacy = new CapturePrivacySettings(
                checked((int)reader.GetInt64(6)),
                ReadBoolean(reader, 7, "exclude_sensitive_applications"),
                ReadBoolean(reader, 8, "pause_in_remote_sessions"),
                ReadBoolean(reader, 9, "pause_during_screen_sharing"),
                reader.GetInt64(10),
                applicationPrivacyMode);

            return new AppSettings(
                theme,
                captureEnabled,
                cloudAnalysisEnabled,
                consent,
                privacy);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or FormatException
                                          or InvalidCastException
                                          or OverflowException)
        {
            throw new InvalidDataException(
                "Stored application settings are invalid.",
                exception);
        }
    }

    private static bool ReadBoolean(
        SqliteDataReader reader,
        int ordinal,
        string columnName)
    {
        return reader.GetInt64(ordinal) switch
        {
            0 => false,
            1 => true,
            var value => throw new InvalidDataException(
                $"Stored value '{value}' for {columnName} is not a boolean."),
        };
    }
}
