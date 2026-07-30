using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Ai;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class SqliteAnalysisStageBindingStore : IAnalysisStageBindingStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteAnalysisStageBindingStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<AnalysisStageBinding>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = BindingSelectSql + " ORDER BY stage;";
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var bindings = new List<AnalysisStageBinding>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bindings.Add(MaterializeBinding(reader));
        }

        return bindings;
    }

    public async Task<AnalysisStageBinding> GetAsync(
        AnalysisStage stage,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(stage);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadBindingAsync(connection, null, stage, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The persisted analysis-stage binding is missing.");
    }

    public async Task<AnalysisStageBinding> SaveAsync(
        AnalysisStage stage,
        bool enabled,
        Guid? providerProfileId,
        long expectedRouteRevision,
        PrivacyStageOptions? privacyOptions,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateStage(stage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRouteRevision);
        var proposed = new AnalysisStageBinding(
            stage,
            enabled,
            providerProfileId,
            expectedRouteRevision,
            privacyOptions);
        var changedAtTicks = ToUtcTicks(changedAtUtc);

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadBindingAsync(
                connection,
                transaction,
                stage,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The persisted analysis-stage binding is missing.");
        if (current.RouteRevision != expectedRouteRevision)
        {
            throw new AnalysisStageBindingConflictException();
        }

        if (current.Enabled == proposed.Enabled
            && current.ProviderProfileId == proposed.ProviderProfileId
            && current.PrivacyOptions == proposed.PrivacyOptions)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }

        if (enabled)
        {
            await EnsureProviderValidatedAsync(
                    connection,
                    transaction,
                    providerProfileId!.Value,
                    stage,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var nextRevision = checked(expectedRouteRevision + 1);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE analysis_stage_bindings
            SET provider_profile_id = $provider_profile_id,
                enabled = $enabled,
                route_revision = $next_revision,
                options_json = $options_json,
                updated_at_utc_ticks = MAX(updated_at_utc_ticks, $updated_at_utc_ticks)
            WHERE stage = $stage AND route_revision = $expected_revision;
            """;
        command.Parameters.AddWithValue("$stage", (int)stage);
        command.Parameters.AddWithValue(
            "$provider_profile_id",
            providerProfileId.HasValue
                ? providerProfileId.Value.ToString("D", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$next_revision", nextRevision);
        command.Parameters.AddWithValue("$expected_revision", expectedRouteRevision);
        command.Parameters.AddWithValue("$options_json", SerializeOptions(stage, privacyOptions));
        command.Parameters.AddWithValue("$updated_at_utc_ticks", changedAtTicks);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new AnalysisStageBindingConflictException();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AnalysisStageBinding(
            stage,
            enabled,
            providerProfileId,
            nextRevision,
            privacyOptions);
    }

    public async Task<ProviderStageValidation?> GetValidationAsync(
        Guid profileId,
        long profileRevision,
        AnalysisStage stage,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId, profileRevision, stage);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadValidationAsync(
                connection,
                null,
                profileId,
                profileRevision,
                stage,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProviderStageValidation> MarkValidatedAsync(
        Guid profileId,
        long profileRevision,
        AnalysisStage stage,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profileId, profileRevision, stage);
        var validatedAtTicks = ToUtcTicks(validatedAtUtc);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = """
                SELECT COUNT(*)
                FROM ai_provider_profiles
                WHERE id = $id AND revision = $revision;
                """;
            verify.Parameters.AddWithValue("$id", FormatId(profileId));
            verify.Parameters.AddWithValue("$revision", profileRevision);
            if (Convert.ToInt32(
                    await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) != 1)
            {
                throw new AiProviderConfigurationConflictException();
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO provider_profile_validations(
                    provider_profile_id,
                    provider_profile_revision,
                    stage,
                    validated_at_utc_ticks)
                VALUES ($id, $revision, $stage, $validated_at_utc_ticks)
                ON CONFLICT(provider_profile_id, provider_profile_revision, stage)
                DO UPDATE SET validated_at_utc_ticks = excluded.validated_at_utc_ticks;
                """;
            command.Parameters.AddWithValue("$id", FormatId(profileId));
            command.Parameters.AddWithValue("$revision", profileRevision);
            command.Parameters.AddWithValue("$stage", (int)stage);
            command.Parameters.AddWithValue("$validated_at_utc_ticks", validatedAtTicks);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderStageValidation(profileId, profileRevision, stage, validatedAtUtc);
    }

    private static async Task EnsureProviderValidatedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        AnalysisStage stage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM ai_provider_profiles AS profiles
            INNER JOIN provider_profile_validations AS validations
                ON validations.provider_profile_id = profiles.id
                AND validations.provider_profile_revision = profiles.revision
                AND validations.stage = $stage
            WHERE profiles.id = $id;
            """;
        command.Parameters.AddWithValue("$stage", (int)stage);
        command.Parameters.AddWithValue("$id", FormatId(profileId));
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException(
                "The selected provider must pass validation for this processing stage before it can be enabled.");
        }
    }

    private static async Task<AnalysisStageBinding?> ReadBindingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AnalysisStage stage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BindingSelectSql + " WHERE stage = $stage;";
        command.Parameters.AddWithValue("$stage", (int)stage);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MaterializeBinding(reader)
            : null;
    }

    private static AnalysisStageBinding MaterializeBinding(SqliteDataReader reader)
    {
        var stage = (AnalysisStage)reader.GetInt32(0);
        var providerId = reader.IsDBNull(1)
            ? (Guid?)null
            : Guid.ParseExact(reader.GetString(1), "D");
        return new AnalysisStageBinding(
            stage,
            reader.GetInt32(2) != 0,
            providerId,
            reader.GetInt64(3),
            DeserializeOptions(stage, reader.GetString(4)));
    }

    private static async Task<ProviderStageValidation?> ReadValidationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid profileId,
        long profileRevision,
        AnalysisStage stage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT validated_at_utc_ticks
            FROM provider_profile_validations
            WHERE provider_profile_id = $id
                AND provider_profile_revision = $revision
                AND stage = $stage;
            """;
        command.Parameters.AddWithValue("$id", FormatId(profileId));
        command.Parameters.AddWithValue("$revision", profileRevision);
        command.Parameters.AddWithValue("$stage", (int)stage);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : new ProviderStageValidation(
                profileId,
                profileRevision,
                stage,
                new DateTimeOffset(Convert.ToInt64(value, CultureInfo.InvariantCulture), TimeSpan.Zero));
    }

    private static PrivacyStageOptions? DeserializeOptions(
        AnalysisStage stage,
        string json)
    {
        if (stage != AnalysisStage.PrivacyInspection)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new PrivacyStageOptions(
            (PrivacyMatchAction)root.GetProperty("onMatch").GetInt32(),
            (PrivacyFailureAction)root.GetProperty("onError").GetInt32());
    }

    private static string SerializeOptions(
        AnalysisStage stage,
        PrivacyStageOptions? options)
    {
        if (stage != AnalysisStage.PrivacyInspection)
        {
            return "{}";
        }

        var value = options ?? PrivacyStageOptions.Default;
        return JsonSerializer.Serialize(new
        {
            onMatch = (int)value.OnMatch,
            onError = (int)value.OnError,
        });
    }

    private static long ToUtcTicks(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must use UTC.", nameof(value));
        }

        return value.Ticks;
    }

    private static void ValidateProfile(Guid profileId, long revision, AnalysisStage stage)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A provider profile identifier is required.", nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ValidateStage(stage);
    }

    private static void ValidateStage(AnalysisStage stage)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private const string BindingSelectSql = """
        SELECT stage, provider_profile_id, enabled, route_revision, options_json
        FROM analysis_stage_bindings
        """;
}
