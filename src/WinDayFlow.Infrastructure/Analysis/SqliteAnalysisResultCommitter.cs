using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Analysis;

public sealed class SqliteAnalysisResultCommitter : IAnalysisResultCommitter
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteAnalysisResultCommitter(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<AnalysisResultCommitStatus> TryCommitAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (providerProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "An analysis result requires a provider profile identifier.",
                nameof(providerProfileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(providerProfileRevision);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Any(static entry => entry is null))
        {
            throw new ArgumentException(
                "An analysis result cannot contain null timeline entries.",
                nameof(entries));
        }

        if (entries.Select(static entry => entry.Id).Distinct().Count() != entries.Count)
        {
            throw new ArgumentException(
                "An analysis result cannot contain duplicate timeline entry identifiers.",
                nameof(entries));
        }

        var committedAt = committedAtUtc.ToUniversalTime();
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var profileStatus = await ReadProfileStatusAsync(
                connection,
                transaction,
                providerProfileId,
                providerProfileRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (profileStatus != AnalysisResultCommitStatus.Committed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return profileStatus;
        }

        if (!await IsCloudAnalysisEnabledAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AnalysisResultCommitStatus.CloudAnalysisDisabled;
        }

        var job = await ReadCommittingJobAsync(
                connection,
                transaction,
                lease,
                committedAt,
                cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AnalysisResultCommitStatus.LeaseLost;
        }

        if (job.ProviderProfileId != providerProfileId
            || job.ProviderProfileRevision != providerProfileRevision)
        {
            throw new InvalidDataException(
                "The committing analysis job does not match the expected provider revision.");
        }

        ValidateEntries(entries, job);
        foreach (var entry in entries)
        {
            if (await TryInsertEntryAsync(
                    connection,
                    transaction,
                    entry,
                    cancellationToken)
                    .ConfigureAwait(false))
            {
                await InsertChildrenAsync(
                        connection,
                        transaction,
                        entry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!await HasSameEntryAsync(
                    connection,
                    transaction,
                    entry,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AnalysisResultCommitStatus.EntryConflict;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE analysis_jobs
                SET state = $completed_state,
                    lease_owner = NULL,
                    lease_token = NULL,
                    lease_expires_at_utc_ticks = NULL,
                    error_code = 0,
                    error_detail = NULL,
                    updated_at_utc_ticks = $committed_at_utc_ticks,
                    completed_at_utc_ticks = $committed_at_utc_ticks
                WHERE id = $id
                    AND state = $committing_state
                    AND attempt = $attempt
                    AND lease_owner = $lease_owner
                    AND lease_token = $lease_token
                    AND lease_expires_at_utc_ticks > $committed_at_utc_ticks
                    AND updated_at_utc_ticks <= $committed_at_utc_ticks;
                """;
            command.Parameters.AddWithValue(
                "$completed_state",
                (int)AnalysisJobState.Completed);
            command.Parameters.AddWithValue(
                "$committing_state",
                (int)AnalysisJobState.Committing);
            command.Parameters.AddWithValue("$committed_at_utc_ticks", ToUtcTicks(committedAt));
            AddLeaseParameters(command.Parameters, lease);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AnalysisResultCommitStatus.LeaseLost;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AnalysisResultCommitStatus.Committed;
    }

    private static async Task<AnalysisResultCommitStatus> ReadProfileStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid providerProfileId,
        long providerProfileRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                revision,
                validated_revision,
                base_endpoint,
                api_key_ciphertext IS NOT NULL
            FROM ai_provider_profiles
            WHERE is_active = 1;
            """;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !Guid.TryParse(reader.GetString(0), out var activeProfileId)
            || activeProfileId != providerProfileId
            || reader.GetInt64(1) != providerProfileRevision
            || reader.IsDBNull(2)
            || reader.GetInt64(2) != providerProfileRevision
            || !Uri.TryCreate(reader.GetString(3), UriKind.Absolute, out var endpoint)
            || (!endpoint.IsLoopback && reader.GetInt64(4) != 1))
        {
            return AnalysisResultCommitStatus.ProviderRevisionChanged;
        }

        return AnalysisResultCommitStatus.Committed;
    }

    private static async Task<bool> IsCloudAnalysisEnabledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cloud_analysis_enabled
            FROM app_settings
            WHERE id = 1;
            """;
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar switch
        {
            long value => value == 1,
            int value => value == 1,
            _ => throw new InvalidDataException(
                "The application settings row is missing or invalid."),
        };
    }

    private static async Task<CommittingJob?> ReadCommittingJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalysisJobLease lease,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                capture_chunk_id,
                provider_profile_id,
                provider_profile_revision,
                analysis_version
            FROM analysis_jobs
            WHERE id = $id
                AND state = $committing_state
                AND attempt = $attempt
                AND lease_owner = $lease_owner
                AND lease_token = $lease_token
                AND lease_expires_at_utc_ticks > $committed_at_utc_ticks
                AND updated_at_utc_ticks <= $committed_at_utc_ticks;
            """;
        command.Parameters.AddWithValue(
            "$committing_state",
            (int)AnalysisJobState.Committing);
        command.Parameters.AddWithValue("$committed_at_utc_ticks", ToUtcTicks(committedAt));
        AddLeaseParameters(command.Parameters, lease);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CommittingJob(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetString(3));
    }

    private static void ValidateEntries(
        IReadOnlyList<TimelineEntry> entries,
        CommittingJob job)
    {
        foreach (var entry in entries)
        {
            if (entry.Origin != TimelineEntryOrigin.Analyzed
                || entry.Evidence is null
                || !string.Equals(
                    entry.Evidence.CaptureChunkId,
                    job.CaptureChunkId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    entry.AnalysisVersion,
                    job.AnalysisVersion,
                    StringComparison.Ordinal)
                || entry.Revision != 0
                || entry.HasUserEdits)
            {
                throw new ArgumentException(
                    "Analysis results must contain new, unedited entries for the claimed job.",
                    nameof(entries));
            }
        }
    }

    private static async Task<bool> TryInsertEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimelineEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO timeline_entries(
                id,
                local_date,
                start_utc_ticks,
                start_offset_minutes,
                end_utc_ticks,
                end_offset_minutes,
                title,
                summary,
                category,
                productivity,
                origin,
                revision,
                confidence,
                evidence_capture_chunk_id,
                evidence_artifact_path,
                analysis_version,
                range_edited_at,
                title_edited_at,
                summary_edited_at,
                category_edited_at,
                productivity_edited_at,
                tags_edited_at)
            VALUES (
                $id,
                $local_date,
                $start_utc_ticks,
                $start_offset_minutes,
                $end_utc_ticks,
                $end_offset_minutes,
                $title,
                $summary,
                $category,
                $productivity,
                $origin,
                $revision,
                $confidence,
                $evidence_capture_chunk_id,
                $evidence_artifact_path,
                $analysis_version,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL)
            ON CONFLICT(id) DO NOTHING;
            """;
        AddEntryParameters(command.Parameters, entry);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> HasSameEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimelineEntry entry,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM timeline_entries
                WHERE id = $id
                    AND local_date = $local_date
                    AND start_utc_ticks = $start_utc_ticks
                    AND start_offset_minutes = $start_offset_minutes
                    AND end_utc_ticks = $end_utc_ticks
                    AND end_offset_minutes = $end_offset_minutes
                    AND title = $title
                    AND summary = $summary
                    AND category = $category
                    AND productivity = $productivity
                    AND origin = $origin
                    AND revision = $revision
                    AND confidence = $confidence
                    AND evidence_capture_chunk_id = $evidence_capture_chunk_id
                    AND evidence_artifact_path = $evidence_artifact_path
                    AND analysis_version = $analysis_version
                    AND range_edited_at IS NULL
                    AND title_edited_at IS NULL
                    AND summary_edited_at IS NULL
                    AND category_edited_at IS NULL
                    AND productivity_edited_at IS NULL
                    AND tags_edited_at IS NULL;
                """;
            AddEntryParameters(command.Parameters, entry);
            if (Convert.ToInt32(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) != 1)
            {
                return false;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ordinal, application_id, display_name, duration_ticks
                FROM timeline_entry_apps
                WHERE timeline_entry_id = $id
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$id", FormatId(entry.Id));
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var index = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (index >= entry.Apps.Count
                    || reader.GetInt32(0) != index
                    || !string.Equals(
                        reader.GetString(1),
                        entry.Apps[index].ApplicationId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        reader.GetString(2),
                        entry.Apps[index].DisplayName,
                        StringComparison.Ordinal)
                    || reader.GetInt64(3) != entry.Apps[index].Duration.Ticks)
                {
                    return false;
                }

                index++;
            }

            if (index != entry.Apps.Count)
            {
                return false;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ordinal, value
                FROM timeline_entry_tags
                WHERE timeline_entry_id = $id
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("$id", FormatId(entry.Id));
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var index = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (index >= entry.Tags.Count
                    || reader.GetInt32(0) != index
                    || !string.Equals(
                        reader.GetString(1),
                        entry.Tags[index],
                        StringComparison.Ordinal))
                {
                    return false;
                }

                index++;
            }

            return index == entry.Tags.Count;
        }
    }

    private static void AddEntryParameters(
        SqliteParameterCollection parameters,
        TimelineEntry entry)
    {
        parameters.AddWithValue("$id", FormatId(entry.Id));
        parameters.AddWithValue(
            "$local_date",
            DateOnly.FromDateTime(entry.Range.Start.DateTime)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        parameters.AddWithValue("$start_utc_ticks", ToUtcTicks(entry.Range.Start));
        parameters.AddWithValue(
            "$start_offset_minutes",
            checked((int)entry.Range.Start.Offset.TotalMinutes));
        parameters.AddWithValue("$end_utc_ticks", ToUtcTicks(entry.Range.End));
        parameters.AddWithValue(
            "$end_offset_minutes",
            checked((int)entry.Range.End.Offset.TotalMinutes));
        parameters.AddWithValue("$title", entry.Title);
        parameters.AddWithValue("$summary", entry.Summary);
        parameters.AddWithValue("$category", (int)entry.Category);
        parameters.AddWithValue("$productivity", (int)entry.Productivity);
        parameters.AddWithValue("$origin", (int)entry.Origin);
        parameters.AddWithValue("$revision", entry.Revision);
        parameters.AddWithValue("$confidence", entry.Confidence!.Value);
        parameters.AddWithValue(
            "$evidence_capture_chunk_id",
            entry.Evidence!.CaptureChunkId);
        parameters.AddWithValue(
            "$evidence_artifact_path",
            entry.Evidence.ArtifactPath);
        parameters.AddWithValue("$analysis_version", entry.AnalysisVersion!);
    }

    private static async Task InsertChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimelineEntry entry,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < entry.Apps.Count; index++)
        {
            var app = entry.Apps[index];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO timeline_entry_apps(
                    timeline_entry_id,
                    ordinal,
                    application_id,
                    display_name,
                    duration_ticks)
                VALUES (
                    $timeline_entry_id,
                    $ordinal,
                    $application_id,
                    $display_name,
                    $duration_ticks);
                """;
            command.Parameters.AddWithValue("$timeline_entry_id", FormatId(entry.Id));
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$application_id", app.ApplicationId);
            command.Parameters.AddWithValue("$display_name", app.DisplayName);
            command.Parameters.AddWithValue("$duration_ticks", app.Duration.Ticks);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < entry.Tags.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO timeline_entry_tags(timeline_entry_id, ordinal, value)
                VALUES ($timeline_entry_id, $ordinal, $value);
                """;
            command.Parameters.AddWithValue("$timeline_entry_id", FormatId(entry.Id));
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$value", entry.Tags[index]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddLeaseParameters(
        SqliteParameterCollection parameters,
        AnalysisJobLease lease)
    {
        parameters.AddWithValue("$id", FormatId(lease.JobId));
        parameters.AddWithValue("$attempt", lease.Attempt);
        parameters.AddWithValue("$lease_owner", lease.Owner);
        parameters.AddWithValue("$lease_token", lease.Token);
    }

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static long ToUtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private sealed record CommittingJob(
        string CaptureChunkId,
        Guid ProviderProfileId,
        long ProviderProfileRevision,
        string AnalysisVersion);
}
