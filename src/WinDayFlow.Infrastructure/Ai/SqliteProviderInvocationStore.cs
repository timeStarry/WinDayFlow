using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Privacy;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Ai;

public sealed class SqliteProviderInvocationStore : IProviderInvocationStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteProviderInvocationStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task StartAsync(
        ProviderInvocationStart invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ValidateStart(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_invocations(
                id,
                stage,
                provider_profile_id,
                provider_profile_revision,
                route_revision,
                endpoint_origin,
                evidence_fingerprint,
                item_count,
                byte_count,
                outcome,
                correlation_id,
                started_at_utc_ticks,
                completed_at_utc_ticks,
                input_tokens,
                output_tokens)
            VALUES (
                $id,
                $stage,
                $provider_profile_id,
                $provider_profile_revision,
                $route_revision,
                $endpoint_origin,
                $evidence_fingerprint,
                $item_count,
                $byte_count,
                $outcome,
                $correlation_id,
                $started_at_utc_ticks,
                NULL,
                NULL,
                NULL);
            """;
        command.Parameters.AddWithValue("$id", FormatId(invocation.Id));
        command.Parameters.AddWithValue("$stage", (int)invocation.Stage);
        command.Parameters.AddWithValue("$provider_profile_id", FormatId(invocation.ProviderProfileId));
        command.Parameters.AddWithValue("$provider_profile_revision", invocation.ProviderProfileRevision);
        command.Parameters.AddWithValue("$route_revision", invocation.RouteRevision);
        command.Parameters.AddWithValue("$endpoint_origin", invocation.EndpointOrigin);
        command.Parameters.AddWithValue("$evidence_fingerprint", invocation.EvidenceFingerprint);
        command.Parameters.AddWithValue("$item_count", invocation.ItemCount);
        command.Parameters.AddWithValue("$byte_count", invocation.ByteCount);
        command.Parameters.AddWithValue("$outcome", (int)ProviderInvocationOutcome.Started);
        command.Parameters.AddWithValue("$correlation_id", FormatId(invocation.CorrelationId));
        command.Parameters.AddWithValue("$started_at_utc_ticks", invocation.StartedAtUtc.Ticks);
        try
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("The provider invocation already exists or is invalid.", exception);
        }
    }

    public async Task CompleteAsync(
        Guid invocationId,
        ProviderInvocationOutcome outcome,
        ProviderInvocationUsage? usage,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (invocationId == Guid.Empty)
        {
            throw new ArgumentException("An invocation identifier is required.", nameof(invocationId));
        }

        if (!Enum.IsDefined(outcome) || outcome == ProviderInvocationOutcome.Started)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Completion time must be UTC.", nameof(completedAtUtc));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE provider_invocations
            SET outcome = $outcome,
                completed_at_utc_ticks = $completed_at_utc_ticks,
                input_tokens = $input_tokens,
                output_tokens = $output_tokens
            WHERE id = $id
                AND outcome = 0
                AND started_at_utc_ticks <= $completed_at_utc_ticks;
            """;
        command.Parameters.AddWithValue("$outcome", (int)outcome);
        command.Parameters.AddWithValue("$completed_at_utc_ticks", completedAtUtc.Ticks);
        command.Parameters.AddWithValue("$input_tokens", usage?.InputTokens is { } input ? input : DBNull.Value);
        command.Parameters.AddWithValue("$output_tokens", usage?.OutputTokens is { } output ? output : DBNull.Value);
        command.Parameters.AddWithValue("$id", FormatId(invocationId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The provider invocation is missing or already completed.");
        }
    }

    private static void ValidateStart(ProviderInvocationStart value)
    {
        if (value.Id == Guid.Empty || value.ProviderProfileId == Guid.Empty
            || value.CorrelationId == Guid.Empty)
        {
            throw new ArgumentException("Provider invocation identifiers cannot be empty.", nameof(value));
        }

        if (!Enum.IsDefined(value.Stage)
            || value.ProviderProfileRevision <= 0
            || value.RouteRevision <= 0
            || string.IsNullOrWhiteSpace(value.EndpointOrigin)
            || value.EndpointOrigin.Length > 512
            || value.EvidenceFingerprint.Length != 64
            || value.EvidenceFingerprint.Any(character => !Uri.IsHexDigit(character))
            || value.ItemCount is < 0 or > 256
            || value.ByteCount < 0
            || value.StartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The provider invocation metadata is invalid.", nameof(value));
        }
    }

    private static string FormatId(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture);
}
