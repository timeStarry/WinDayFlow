using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Capture;
using WinDayFlow.Application.Settings;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Capture;

public sealed class SqliteCaptureContextStore : ICaptureContextStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteCaptureContextStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task ReplaceAsync(
        CaptureChunk chunk,
        IReadOnlyList<CaptureContextSample> samples,
        CaptureExclusionRuleSet rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateSamples(chunk, samples);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM capture_context_samples WHERE capture_chunk_id = $id;";
            delete.Parameters.AddWithValue("$id", chunk.Id);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var sample in samples)
        {
            if (sample.Application is { } application)
            {
                await UpsertApplicationAsync(
                        connection,
                        transaction,
                        application,
                        sample.SampledAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await InsertSampleAsync(
                    connection,
                    transaction,
                    sample,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var match in MergeRuleMatches(sample, rules))
            {
                await InsertRuleMatchAsync(
                        connection,
                        transaction,
                        chunk.Id,
                        sample.Ordinal,
                        match,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CaptureContextSample>> ListAsync(
        string captureChunkId,
        CancellationToken cancellationToken = default)
    {
        CaptureChunk.ValidateIdentifier(captureChunkId);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                samples.ordinal,
                samples.sampled_at_utc_ticks,
                chunks.start_offset_minutes,
                catalog.application_id,
                catalog.display_name,
                catalog.identity_kind,
                catalog.identity_value,
                samples.process_id,
                samples.cpu_usage_basis_points,
                samples.working_set_bytes,
                samples.private_memory_bytes,
                samples.evaluated_rule_set_revision,
                samples.application_context_available,
                samples.window_context_available,
                matches.rule_id,
                matches.rule_revision
            FROM capture_context_samples AS samples
            INNER JOIN capture_chunks AS chunks
                ON chunks.id = samples.capture_chunk_id
            LEFT JOIN application_catalog AS catalog
                ON catalog.application_id = samples.application_id
            LEFT JOIN capture_context_rule_matches AS matches
                ON matches.capture_chunk_id = samples.capture_chunk_id
                AND matches.sample_ordinal = samples.ordinal
            WHERE samples.capture_chunk_id = $id
            ORDER BY samples.ordinal, matches.rule_id;
            """;
        command.Parameters.AddWithValue("$id", captureChunkId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var builders = new Dictionary<int, SampleBuilder>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var ordinal = checked((int)reader.GetInt64(0));
            if (!builders.TryGetValue(ordinal, out var builder))
            {
                CaptureContextApplication? application = null;
                if (!reader.IsDBNull(3))
                {
                    application = new CaptureContextApplication(
                        reader.GetString(3),
                        reader.GetString(4),
                        (ApplicationIdentityKind)checked((int)reader.GetInt64(5)),
                        reader.GetString(6),
                        checked((uint)reader.GetInt64(7)),
                        checked((uint)reader.GetInt64(8)),
                        reader.GetInt64(9),
                        reader.GetInt64(10));
                }

                var offset = TimeSpan.FromMinutes(reader.GetInt32(2));
                builder = new SampleBuilder(
                    ordinal,
                    new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero).ToOffset(offset),
                    application,
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.GetBoolean(12),
                    reader.GetBoolean(13));
                builders.Add(ordinal, builder);
            }

            if (!reader.IsDBNull(14))
            {
                builder.RuleMatches.Add(new CaptureContextRuleMatch(
                    Guid.ParseExact(reader.GetString(14), "D"),
                    reader.GetInt64(15)));
            }
        }

        return builders.Values
            .OrderBy(static value => value.Ordinal)
            .Select(value => new CaptureContextSample(
                captureChunkId,
                value.Ordinal,
                value.SampledAt,
                value.Application,
                value.RuleMatches,
                value.EvaluatedRuleSetRevision,
                value.ApplicationContextAvailable,
                value.WindowContextAvailable))
            .ToArray();
    }

    private static IEnumerable<CaptureContextRuleMatch> MergeRuleMatches(
        CaptureContextSample sample,
        CaptureExclusionRuleSet rules)
    {
        var observedRuleIds = new HashSet<Guid>();
        foreach (var match in sample.RuleMatches)
        {
            if (observedRuleIds.Add(match.RuleId))
            {
                yield return match;
            }
        }

        foreach (var match in FindApplicationRuleMatches(sample.Application, rules))
        {
            if (observedRuleIds.Add(match.RuleId))
            {
                yield return match;
            }
        }
    }

    private static IEnumerable<CaptureContextRuleMatch> FindApplicationRuleMatches(
        CaptureContextApplication? application,
        CaptureExclusionRuleSet rules)
    {
        if (application is null)
        {
            yield break;
        }

        foreach (var rule in rules.Rules)
        {
            if (!rule.Enabled
                || rule.Scope != CaptureExclusionRuleScope.Application
                || rule.ApplicationIdentityKind != application.IdentityKind
                || !IdentityEquals(
                    rule.ApplicationIdentityKind,
                    rule.IdentityValue,
                    application.IdentityValue))
            {
                continue;
            }

            yield return new CaptureContextRuleMatch(rule.Id, rule.Revision);
        }
    }

    private static bool IdentityEquals(
        ApplicationIdentityKind kind,
        string left,
        string right) => kind switch
        {
            ApplicationIdentityKind.ExecutableName
                or ApplicationIdentityKind.PackageFamilyName =>
                string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
            ApplicationIdentityKind.PublisherCertificateSha256 =>
                string.Equals(left, right, StringComparison.Ordinal),
            _ => false,
        };

    private static async Task UpsertApplicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureContextApplication application,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO application_catalog(
                application_id,
                identity_kind,
                identity_value,
                display_name,
                icon_cache_key,
                first_seen_utc_ticks,
                last_seen_utc_ticks)
            VALUES ($id, $kind, $value, $name, NULL, $ticks, $ticks)
            ON CONFLICT(application_id) DO UPDATE SET
                identity_kind = excluded.identity_kind,
                identity_value = excluded.identity_value,
                display_name = excluded.display_name,
                last_seen_utc_ticks = MAX(last_seen_utc_ticks, excluded.last_seen_utc_ticks);
            """;
        command.Parameters.AddWithValue("$id", application.ApplicationId);
        command.Parameters.AddWithValue("$kind", (int)application.IdentityKind);
        command.Parameters.AddWithValue("$value", application.IdentityValue);
        command.Parameters.AddWithValue("$name", application.DisplayName);
        command.Parameters.AddWithValue("$ticks", observedAt.ToUniversalTime().Ticks);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CaptureContextSample sample,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO capture_context_samples(
                capture_chunk_id,
                ordinal,
                sampled_at_utc_ticks,
                application_id,
                process_id,
                cpu_usage_basis_points,
                working_set_bytes,
                private_memory_bytes,
                evaluated_rule_set_revision,
                application_context_available,
                window_context_available)
            VALUES ($chunk_id, $ordinal, $sampled_at, $application_id,
                $process_id, $cpu, $working_set, $private_memory,
                $rules_revision, $application_context, $window_context);
            """;
        command.Parameters.AddWithValue("$chunk_id", sample.CaptureChunkId);
        command.Parameters.AddWithValue("$ordinal", sample.Ordinal);
        command.Parameters.AddWithValue("$sampled_at", sample.SampledAt.ToUniversalTime().Ticks);
        AddNullable(command.Parameters, "$application_id", sample.Application?.ApplicationId);
        AddNullable(command.Parameters, "$process_id", sample.Application is { } app
            ? checked((long)app.ProcessId) : null);
        AddNullable(command.Parameters, "$cpu", sample.Application is { } cpu
            ? checked((long)cpu.CpuUsageBasisPoints) : null);
        AddNullable(command.Parameters, "$working_set", sample.Application?.WorkingSetBytes);
        AddNullable(command.Parameters, "$private_memory", sample.Application?.PrivateMemoryBytes);
        AddNullable(command.Parameters, "$rules_revision", sample.EvaluatedRuleSetRevision);
        command.Parameters.AddWithValue(
            "$application_context",
            sample.ApplicationContextAvailable ? 1 : 0);
        command.Parameters.AddWithValue(
            "$window_context",
            sample.WindowContextAvailable ? 1 : 0);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRuleMatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string chunkId,
        int sampleOrdinal,
        CaptureContextRuleMatch match,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO capture_context_rule_matches(
                capture_chunk_id, sample_ordinal, rule_id, rule_revision)
            VALUES ($chunk_id, $ordinal, $rule_id, $rule_revision);
            """;
        command.Parameters.AddWithValue("$chunk_id", chunkId);
        command.Parameters.AddWithValue("$ordinal", sampleOrdinal);
        command.Parameters.AddWithValue(
            "$rule_id",
            match.RuleId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rule_revision", match.RuleRevision);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSamples(
        CaptureChunk chunk,
        IReadOnlyList<CaptureContextSample> samples)
    {
        var previousOrdinal = -1;
        foreach (var sample in samples)
        {
            if (sample is null
                || !string.Equals(sample.CaptureChunkId, chunk.Id, StringComparison.Ordinal)
                || sample.Ordinal <= previousOrdinal
                || sample.SampledAt < chunk.Range.Start
                || sample.SampledAt >= chunk.Range.End)
            {
                throw new ArgumentException("Capture context samples do not match their chunk.", nameof(samples));
            }
            previousOrdinal = sample.Ordinal;
        }
    }

    private static void AddNullable(
        SqliteParameterCollection parameters,
        string name,
        object? value) => parameters.AddWithValue(name, value ?? DBNull.Value);

    private sealed record SampleBuilder(
        int Ordinal,
        DateTimeOffset SampledAt,
        CaptureContextApplication? Application,
        long? EvaluatedRuleSetRevision,
        bool ApplicationContextAvailable,
        bool WindowContextAvailable)
    {
        public List<CaptureContextRuleMatch> RuleMatches { get; } = [];
    }
}
