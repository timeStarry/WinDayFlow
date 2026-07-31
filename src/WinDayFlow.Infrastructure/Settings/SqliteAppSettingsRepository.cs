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
            capture_consent_version,
            capture_consent_granted_at_utc,
            evidence_retention_days,
            capture_privacy_revision,
            capture_interval_seconds,
            capture_intent,
            evidence_retention_unlimited
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
                proposed.Evidence.SendRules,
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
        ValidateRuleTransition(expected.Evidence.SendRules, proposed.Evidence.SendRules);

        var effectiveRulesChanged = !expected.Evidence.SendRules
            .HasSameEffectivePolicy(proposed.Evidence.SendRules);
        if (!effectiveRulesChanged)
        {
            if (proposed.Evidence.RulesRevision != expected.Evidence.RulesRevision)
            {
                throw new InvalidOperationException(
                    "The evidence send-rule revision cannot change without an effective rule change.");
            }
            return;
        }

        if (expected.Evidence.RulesRevision == long.MaxValue
            || proposed.Evidence.RulesRevision != expected.Evidence.RulesRevision + 1)
        {
            throw new InvalidOperationException(
                "An effective evidence send-rule change must advance the revision exactly once.");
        }
    }

    private static void ValidateRuleTransition(
        CaptureExclusionRuleSet expected,
        CaptureExclusionRuleSet proposed)
    {
        var expectedById = new Dictionary<Guid, (CaptureExclusionRule Rule, int Index)>();
        for (var index = 0; index < expected.Count; index++)
        {
            expectedById.Add(expected[index].Id, (expected[index], index));
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
                        "A new evidence send rule must start at revision one.");
                }
                continue;
            }

            var contentChanged = !HasSameRuleContent(previous.Rule, rule);
            var positionChanged = previous.Index != index;
            var advancedExactlyOnce = HasAdvancedExactlyOnce(
                previous.Rule.Revision,
                rule.Revision);
            if (contentChanged && !advancedExactlyOnce)
            {
                throw new InvalidOperationException(
                    "A changed evidence send rule must advance its revision exactly once.");
            }
            if (!contentChanged
                && rule.Revision != previous.Rule.Revision
                && !(orderChanged && positionChanged && advancedExactlyOnce))
            {
                throw new InvalidOperationException(
                    "An unchanged evidence send rule cannot change its revision.");
            }
            if (orderChanged && positionChanged && advancedExactlyOnce)
            {
                movedRuleAdvanced = true;
            }
        }

        if (orderChanged && !movedRuleAdvanced)
        {
            throw new InvalidOperationException(
                "Reordering evidence send rules must advance a moved rule revision.");
        }
    }

    private static bool HasCommonRuleOrderChanged(
        CaptureExclusionRuleSet expected,
        CaptureExclusionRuleSet proposed)
    {
        var expectedIds = expected.Rules.Select(static rule => rule.Id).ToHashSet();
        var proposedIds = proposed.Rules.Select(static rule => rule.Id).ToHashSet();
        return !expected.Rules.Where(rule => proposedIds.Contains(rule.Id))
            .Select(static rule => rule.Id)
            .SequenceEqual(proposed.Rules.Where(rule => expectedIds.Contains(rule.Id))
                .Select(static rule => rule.Id));
    }

    private static bool HasSameRuleContent(
        CaptureExclusionRule expected,
        CaptureExclusionRule proposed) =>
        string.Equals(expected.Name, proposed.Name, StringComparison.Ordinal)
        && expected.Enabled == proposed.Enabled
        && expected.Scope == proposed.Scope
        && expected.ApplicationIdentityKind == proposed.ApplicationIdentityKind
        && string.Equals(expected.IdentityValue, proposed.IdentityValue, StringComparison.Ordinal)
        && expected.WindowTitleMatchKind == proposed.WindowTitleMatchKind
        && string.Equals(expected.Pattern, proposed.Pattern, StringComparison.Ordinal);

    private static bool HasAdvancedExactlyOnce(long expected, long proposed) =>
        expected < long.MaxValue && proposed == expected + 1;

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
                capture_enabled = 0,
                cloud_analysis_enabled = 0,
                capture_consent_version = $capture_consent_version,
                capture_consent_granted_at_utc = $capture_consent_granted_at_utc,
                capture_consent_privacy_revision = NULL,
                evidence_retention_days = CASE
                    WHEN $evidence_retention_unlimited = 1
                    THEN evidence_retention_days
                    ELSE $evidence_retention_days
                END,
                evidence_retention_unlimited = $evidence_retention_unlimited,
                exclude_sensitive_applications = 0,
                pause_in_remote_sessions = 0,
                pause_during_screen_sharing = 0,
                capture_privacy_revision = $rules_revision,
                capture_application_privacy_mode = 1,
                capture_interval_seconds = $capture_interval_seconds,
                capture_intent = $capture_intent
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$theme", (int)settings.Theme);
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
            "$evidence_retention_days",
            settings.Evidence.RetentionDays
                == EvidenceSettings.UnlimitedRetentionDays
                    ? EvidenceSettings.DefaultRetentionDays
                    : settings.Evidence.RetentionDays);
        command.Parameters.AddWithValue(
            "$evidence_retention_unlimited",
            settings.Evidence.RetentionDays
                == EvidenceSettings.UnlimitedRetentionDays ? 1 : 0);
        command.Parameters.AddWithValue(
            "$rules_revision",
            settings.Evidence.RulesRevision);
        command.Parameters.AddWithValue(
            "$capture_interval_seconds",
            settings.CaptureIntervalSeconds);
        command.Parameters.AddWithValue("$capture_intent", (int)settings.CaptureIntent);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
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
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < rules.Count; ordinal++)
        {
            var rule = rules[ordinal];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO capture_exclusion_rules(
                    settings_id, rule_id, ordinal, name, enabled, scope,
                    application_identity_kind, identity_value,
                    window_title_match_kind, pattern, revision)
                VALUES (1, $rule_id, $ordinal, $name, $enabled, $scope,
                    $identity_kind, $identity_value, $match_kind, $pattern, $revision);
                """;
            insert.Parameters.AddWithValue("$rule_id", rule.Id.ToString("D"));
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$name", rule.Name);
            insert.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$scope", (int)rule.Scope);
            insert.Parameters.AddWithValue("$identity_kind", (int)rule.ApplicationIdentityKind);
            insert.Parameters.AddWithValue("$identity_value", rule.IdentityValue);
            insert.Parameters.AddWithValue(
                "$match_kind",
                rule.WindowTitleMatchKind is { } matchKind ? (int)matchKind : DBNull.Value);
            insert.Parameters.AddWithValue("$pattern", rule.Pattern ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$revision", rule.Revision);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        return new AppSettings(
            settings.Theme,
            settings.RecordingConsent,
            new EvidenceSettings(
                settings.Evidence.RetentionDays,
                settings.Evidence.RulesRevision,
                rules),
            settings.CaptureIntervalSeconds,
            settings.CaptureIntent);
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
                        "Stored evidence send-rule ordinals must be contiguous and zero-based.");
                }

                var serializedId = reader.GetString(1);
                if (!Guid.TryParseExact(serializedId, "D", out var id)
                    || !string.Equals(serializedId, id.ToString("D"), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A stored evidence send-rule identifier is invalid.");
                }

                rules.Add(new CaptureExclusionRule(
                    id,
                    reader.GetString(2),
                    ReadBoolean(reader, 3, "enabled"),
                    (CaptureExclusionRuleScope)checked((int)reader.GetInt64(4)),
                    (ApplicationIdentityKind)checked((int)reader.GetInt64(5)),
                    reader.GetString(6),
                    reader.IsDBNull(7)
                        ? null
                        : (WindowTitleMatchKind)checked((int)reader.GetInt64(7)),
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
            throw new InvalidDataException("Stored evidence send rules are invalid.", exception);
        }
    }

    private static AppSettings MaterializeSettings(SqliteDataReader reader)
    {
        try
        {
            var theme = (AppThemePreference)checked((int)reader.GetInt64(0));
            if (!Enum.IsDefined(theme))
            {
                throw new InvalidDataException("The stored application theme is invalid.");
            }

            var consentVersionIsNull = reader.IsDBNull(1);
            var consentTimestampIsNull = reader.IsDBNull(2);
            if (consentVersionIsNull != consentTimestampIsNull)
            {
                throw new InvalidDataException(
                    "Stored recording consent version and timestamp are inconsistent.");
            }

            RecordingConsent? consent = null;
            if (!consentVersionIsNull)
            {
                consent = new RecordingConsent(
                    checked((int)reader.GetInt64(1)),
                    DateTimeOffset.ParseExact(
                        reader.GetString(2),
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None));
            }

            var captureIntent = (CaptureIntent)checked((int)reader.GetInt64(6));
            if (captureIntent is not (CaptureIntent.Stopped
                or CaptureIntent.Paused
                or CaptureIntent.Recording))
            {
                throw new InvalidDataException("The stored capture intent is invalid.");
            }

            return new AppSettings(
                theme,
                consent,
                new EvidenceSettings(
                    ReadBoolean(
                        reader,
                        7,
                        "evidence_retention_unlimited")
                            ? EvidenceSettings.UnlimitedRetentionDays
                            : checked((int)reader.GetInt64(3)),
                    reader.GetInt64(4),
                    CaptureExclusionRuleSet.Empty),
                checked((int)reader.GetInt64(5)),
                captureIntent);
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
            throw new InvalidDataException("Stored application settings are invalid.", exception);
        }
    }

    private static bool ReadBoolean(
        SqliteDataReader reader,
        int ordinal,
        string columnName) => reader.GetInt64(ordinal) switch
        {
            0 => false,
            1 => true,
            var value => throw new InvalidDataException(
                $"Stored value '{value}' for {columnName} is not a boolean."),
        };
}
