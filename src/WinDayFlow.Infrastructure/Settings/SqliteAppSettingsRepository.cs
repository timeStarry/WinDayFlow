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
            capture_privacy_revision
        FROM app_settings
        WHERE id = 1;
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
        return await ReadAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
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
                    capture_privacy_revision = $capture_privacy_revision
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

            var affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    "The application settings row has not been initialized.");
            }
        }

        var persisted = await ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (persisted != settings)
        {
            throw new InvalidDataException(
                "The persisted application settings did not match the requested values.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AppSettings> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

        return Materialize(reader);
    }

    private static AppSettings Materialize(SqliteDataReader reader)
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

            var privacy = new CapturePrivacySettings(
                checked((int)reader.GetInt64(6)),
                ReadBoolean(reader, 7, "exclude_sensitive_applications"),
                ReadBoolean(reader, 8, "pause_in_remote_sessions"),
                ReadBoolean(reader, 9, "pause_during_screen_sharing"),
                reader.GetInt64(10));

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
