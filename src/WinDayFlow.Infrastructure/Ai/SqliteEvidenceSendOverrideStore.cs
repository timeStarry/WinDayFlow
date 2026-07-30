using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class SqliteEvidenceSendOverrideStore : IEvidenceSendOverrideStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteEvidenceSendOverrideStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<EvidenceSendOverride> CreateAsync(
        EvidenceSendOverride value,
        CancellationToken cancellationToken = default)
    {
        Validate(value);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence_send_overrides(
                id, capture_chunk_id, stage, provider_profile_id,
                provider_profile_revision, route_revision, evidence_fingerprint,
                logical_operation_id, remaining_uses, created_at_utc_ticks,
                expires_at_utc_ticks, last_consumed_at_utc_ticks)
            VALUES ($id, $chunk_id, $stage, $profile_id, $profile_revision,
                $route_revision, $fingerprint, $operation_id, $remaining_uses,
                $created_at, $expires_at, NULL);
            """;
        AddParameters(command.Parameters, value);
        try
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("The evidence-send override already exists or is invalid.", exception);
        }
        return value;
    }

    public async Task<bool> TryConsumeAsync(
        string captureChunkId,
        AnalysisStage stage,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string evidenceFingerprint,
        Guid logicalOperationId,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(
            captureChunkId,
            stage,
            providerProfileId,
            providerProfileRevision,
            routeRevision,
            evidenceFingerprint,
            logicalOperationId,
            consumedAtUtc);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE evidence_send_overrides
            SET remaining_uses = remaining_uses - 1,
                last_consumed_at_utc_ticks = $consumed_at
            WHERE id = (
                SELECT id
                FROM evidence_send_overrides
                WHERE capture_chunk_id = $chunk_id
                    AND stage = $stage
                    AND provider_profile_id = $profile_id
                    AND provider_profile_revision = $profile_revision
                    AND route_revision = $route_revision
                    AND evidence_fingerprint = $fingerprint
                    AND logical_operation_id = $operation_id
                    AND remaining_uses > 0
                    AND expires_at_utc_ticks >= $consumed_at
                ORDER BY created_at_utc_ticks DESC, id
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$chunk_id", captureChunkId);
        command.Parameters.AddWithValue("$stage", (int)stage);
        command.Parameters.AddWithValue("$profile_id", FormatId(providerProfileId));
        command.Parameters.AddWithValue("$profile_revision", providerProfileRevision);
        command.Parameters.AddWithValue("$route_revision", routeRevision);
        command.Parameters.AddWithValue("$fingerprint", evidenceFingerprint);
        command.Parameters.AddWithValue("$operation_id", FormatId(logicalOperationId));
        command.Parameters.AddWithValue("$consumed_at", consumedAtUtc.Ticks);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddParameters(
        SqliteParameterCollection parameters,
        EvidenceSendOverride value)
    {
        parameters.AddWithValue("$id", FormatId(value.Id));
        parameters.AddWithValue("$chunk_id", value.CaptureChunkId);
        parameters.AddWithValue("$stage", (int)value.Stage);
        parameters.AddWithValue("$profile_id", FormatId(value.ProviderProfileId));
        parameters.AddWithValue("$profile_revision", value.ProviderProfileRevision);
        parameters.AddWithValue("$route_revision", value.RouteRevision);
        parameters.AddWithValue("$fingerprint", value.EvidenceFingerprint);
        parameters.AddWithValue("$operation_id", FormatId(value.LogicalOperationId));
        parameters.AddWithValue("$remaining_uses", value.RemainingUses);
        parameters.AddWithValue("$created_at", value.CreatedAtUtc.Ticks);
        parameters.AddWithValue("$expires_at", value.ExpiresAtUtc.Ticks);
    }

    private static void Validate(EvidenceSendOverride value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Id == Guid.Empty || value.RemainingUses is <= 0 or > 20
            || value.ExpiresAtUtc <= value.CreatedAtUtc)
        {
            throw new ArgumentException("The evidence-send override is invalid.", nameof(value));
        }
        ValidateKey(
            value.CaptureChunkId,
            value.Stage,
            value.ProviderProfileId,
            value.ProviderProfileRevision,
            value.RouteRevision,
            value.EvidenceFingerprint,
            value.LogicalOperationId,
            value.CreatedAtUtc);
        if (value.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Override expiry must use UTC.", nameof(value));
        }
    }

    private static void ValidateKey(
        string captureChunkId,
        AnalysisStage stage,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        string fingerprint,
        Guid logicalOperationId,
        DateTimeOffset timestamp)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        if (!Enum.IsDefined(stage) || providerProfileId == Guid.Empty
            || providerProfileRevision <= 0 || routeRevision <= 0
            || fingerprint.Length != 64
            || fingerprint.Any(static character => character is not (>= '0' and <= '9'
                or >= 'A' and <= 'F'))
            || logicalOperationId == Guid.Empty || timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The evidence-send override key is invalid.");
        }
    }

    private static string FormatId(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture);
}
