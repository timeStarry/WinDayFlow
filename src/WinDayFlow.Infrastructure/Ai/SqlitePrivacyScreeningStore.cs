using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class SqlitePrivacyScreeningStore : IPrivacyScreeningStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqlitePrivacyScreeningStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<PrivacyScreeningSnapshot?> GetAsync(
        string captureChunkId,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string inputFingerprint,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(
            captureChunkId,
            providerProfileId,
            providerProfileRevision,
            routeRevision,
            inputFingerprint);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + """
            WHERE capture_chunk_id = $capture_chunk_id
                AND provider_profile_id = $provider_profile_id
                AND provider_profile_revision = $provider_profile_revision
                AND route_revision = $route_revision
                AND input_fingerprint = $input_fingerprint;
            """;
        AddKeyParameters(
            command.Parameters,
            captureChunkId,
            providerProfileId,
            providerProfileRevision,
            routeRevision,
            inputFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Materialize(reader)
            : null;
    }

    public async Task<PrivacyScreeningSnapshot> SaveAsync(
        PrivacyScreeningSnapshot screening,
        CancellationToken cancellationToken = default)
    {
        Validate(screening);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO privacy_screenings(
                id, capture_chunk_id, provider_profile_id,
                provider_profile_revision, route_revision, input_fingerprint,
                state, verdict, result_json, derivative_manifest_relative_path,
                output_fingerprint, attempt, error_code, revision,
                created_at_utc_ticks, updated_at_utc_ticks)
            VALUES (
                $id, $capture_chunk_id, $provider_profile_id,
                $provider_profile_revision, $route_revision, $input_fingerprint,
                $state, $verdict, $result_json, $derivative_manifest_relative_path,
                $output_fingerprint, $attempt, $error_code, $revision,
                $created_at_utc_ticks, $updated_at_utc_ticks)
            ON CONFLICT(
                capture_chunk_id, provider_profile_id, provider_profile_revision,
                route_revision, input_fingerprint)
            DO UPDATE SET
                state = excluded.state,
                verdict = excluded.verdict,
                result_json = excluded.result_json,
                derivative_manifest_relative_path = excluded.derivative_manifest_relative_path,
                output_fingerprint = excluded.output_fingerprint,
                attempt = excluded.attempt,
                error_code = excluded.error_code,
                revision = excluded.revision,
                updated_at_utc_ticks = excluded.updated_at_utc_ticks
            WHERE privacy_screenings.id = excluded.id
                AND privacy_screenings.created_at_utc_ticks = excluded.created_at_utc_ticks
                AND privacy_screenings.revision = excluded.revision - 1
                AND privacy_screenings.updated_at_utc_ticks <= excluded.updated_at_utc_ticks;
            """;
        command.Parameters.AddWithValue("$id", FormatId(screening.Id));
        AddKeyParameters(
            command.Parameters,
            screening.CaptureChunkId,
            screening.ProviderProfileId,
            screening.ProviderProfileRevision,
            screening.RouteRevision,
            screening.InputFingerprint);
        command.Parameters.AddWithValue("$state", (int)screening.State);
        command.Parameters.AddWithValue(
            "$verdict",
            screening.Verdict is { } verdict ? (int)verdict : DBNull.Value);
        command.Parameters.AddWithValue(
            "$result_json",
            screening.Result is { } result
                ? JsonSerializer.Serialize(result, JsonOptions)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$derivative_manifest_relative_path",
            screening.DerivativeManifestPath?.Value is { } path ? path : DBNull.Value);
        command.Parameters.AddWithValue(
            "$output_fingerprint",
            screening.OutputFingerprint is { } fingerprint ? fingerprint : DBNull.Value);
        command.Parameters.AddWithValue("$attempt", screening.Attempt);
        command.Parameters.AddWithValue("$error_code", screening.ErrorCode is { } code ? code : DBNull.Value);
        command.Parameters.AddWithValue("$revision", screening.Revision);
        command.Parameters.AddWithValue("$created_at_utc_ticks", screening.CreatedAtUtc.Ticks);
        command.Parameters.AddWithValue("$updated_at_utc_ticks", screening.UpdatedAtUtc.Ticks);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The privacy screening changed concurrently.");
        }
        return screening;
    }

    public async Task<PrivacyScreeningSnapshot?> FindByOutputAsync(
        string captureChunkId,
        string outputFingerprint,
        CancellationToken cancellationToken = default)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        ValidateFingerprint(outputFingerprint, nameof(outputFingerprint));
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + """
            WHERE capture_chunk_id = $capture_chunk_id
                AND output_fingerprint = $output_fingerprint
            ORDER BY updated_at_utc_ticks DESC, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$capture_chunk_id", captureChunkId);
        command.Parameters.AddWithValue("$output_fingerprint", outputFingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Materialize(reader)
            : null;
    }

    private static PrivacyScreeningSnapshot Materialize(SqliteDataReader reader)
    {
        var result = reader.IsDBNull(8)
            ? null
            : JsonSerializer.Deserialize<PrivacyScreeningResult>(reader.GetString(8), JsonOptions)
                ?? throw new InvalidDataException("The privacy screening result is empty.");
        return new PrivacyScreeningSnapshot(
            Guid.ParseExact(reader.GetString(0), "D"),
            reader.GetString(1),
            Guid.ParseExact(reader.GetString(2), "D"),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            (PrivacyScreeningState)reader.GetInt32(6),
            reader.IsDBNull(7) ? null : (PrivacyScreeningVerdict)reader.GetInt32(7),
            result,
            reader.IsDBNull(9) ? null : new EvidenceRelativePath(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.GetInt64(13),
            new DateTimeOffset(reader.GetInt64(14), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(15), TimeSpan.Zero));
    }

    private static void Validate(PrivacyScreeningSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateKey(
            value.CaptureChunkId,
            value.ProviderProfileId,
            value.ProviderProfileRevision,
            value.RouteRevision,
            value.InputFingerprint);
        if (value.Id == Guid.Empty || !Enum.IsDefined(value.State)
            || value.Verdict is { } verdict && !Enum.IsDefined(verdict)
            || value.Attempt is < 0 or > 100
            || value.Revision <= 0
            || value.CreatedAtUtc.Offset != TimeSpan.Zero
            || value.UpdatedAtUtc.Offset != TimeSpan.Zero
            || value.UpdatedAtUtc < value.CreatedAtUtc
            || value.Result is not null && value.Verdict != value.Result.Verdict
            || value.OutputFingerprint is { } output && !IsFingerprint(output)
            || value.State is PrivacyScreeningState.Clear or PrivacyScreeningState.Redacted
                && value.OutputFingerprint is null
            || value.State == PrivacyScreeningState.Redacted
                && value.DerivativeManifestPath is null)
        {
            throw new ArgumentException("The privacy screening snapshot is invalid.", nameof(value));
        }
    }

    private static void ValidateKey(
        string captureChunkId,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string inputFingerprint)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        if (providerProfileId == Guid.Empty || providerProfileRevision <= 0 || routeRevision <= 0
            || !IsFingerprint(inputFingerprint))
        {
            throw new ArgumentException("The privacy screening cache key is invalid.");
        }
    }

    private static void AddKeyParameters(
        SqliteParameterCollection parameters,
        string captureChunkId,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string inputFingerprint)
    {
        parameters.AddWithValue("$capture_chunk_id", captureChunkId);
        parameters.AddWithValue("$provider_profile_id", FormatId(providerProfileId));
        parameters.AddWithValue("$provider_profile_revision", providerProfileRevision);
        parameters.AddWithValue("$route_revision", routeRevision);
        parameters.AddWithValue("$input_fingerprint", inputFingerprint);
    }

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static bool IsFingerprint(string value) => value.Length == 64
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'A' and <= 'F');

    private static void ValidateFingerprint(string value, string parameterName)
    {
        if (!IsFingerprint(value))
        {
            throw new ArgumentException("A canonical SHA-256 fingerprint is required.", parameterName);
        }
    }

    private const string SelectSql = """
        SELECT
            id, capture_chunk_id, provider_profile_id, provider_profile_revision,
            route_revision, input_fingerprint, state, verdict, result_json,
            derivative_manifest_relative_path, output_fingerprint, attempt,
            error_code, revision, created_at_utc_ticks, updated_at_utc_ticks
        FROM privacy_screenings
        """;
}
