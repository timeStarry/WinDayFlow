using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Ai;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Analysis;

public sealed class SqliteAnalysisResultCommitter : IAnalysisStageAwareWindowResultCommitter
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteAnalysisResultCommitter(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public Task<AnalysisResultCommitStatus> TryCommitAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        TryCommitCoreAsync(
            lease,
            providerProfileId,
            providerProfileRevision,
            expectedRouteRevision: null,
            window: null,
            entries,
            committedAtUtc,
            cancellationToken);

    public Task<AnalysisResultCommitStatus> TryCommitAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        TryCommitCoreAsync(
            lease,
            providerProfileId,
            providerProfileRevision,
            routeRevision,
            window: null,
            entries,
            committedAtUtc,
            cancellationToken);

    public Task<AnalysisResultCommitStatus> TryCommitWindowAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        AnalysisWindowSnapshot window,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return TryCommitCoreAsync(
            lease,
            providerProfileId,
            providerProfileRevision,
            expectedRouteRevision: null,
            window,
            entries,
            committedAtUtc,
            cancellationToken);
    }

    public Task<AnalysisResultCommitStatus> TryCommitWindowAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        long routeRevision,
        AnalysisWindowSnapshot window,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        return TryCommitCoreAsync(
            lease,
            providerProfileId,
            providerProfileRevision,
            routeRevision,
            window,
            entries,
            committedAtUtc,
            cancellationToken);
    }

    private async Task<AnalysisResultCommitStatus> TryCommitCoreAsync(
        AnalysisJobLease lease,
        Guid providerProfileId,
        long providerProfileRevision,
        long? expectedRouteRevision,
        AnalysisWindowSnapshot? window,
        IReadOnlyList<TimelineEntry> entries,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (providerProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "An analysis result requires a provider profile identifier.",
                nameof(providerProfileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(providerProfileRevision);
        if (expectedRouteRevision.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRouteRevision.Value);
        }
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

        var profileStatus = await ReadRouteStatusAsync(
                connection,
                transaction,
                providerProfileId,
                providerProfileRevision,
                expectedRouteRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (profileStatus != AnalysisResultCommitStatus.Committed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return profileStatus;
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

        ValidateEntries(entries, job, window);
        if (window is not null)
        {
            if (!await WindowBaselineMatchesAsync(
                    connection,
                    transaction,
                    window,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AnalysisResultCommitStatus.WindowChanged;
            }

            await RewriteEligibleWindowEntriesAsync(
                    connection,
                    transaction,
                    window.Range,
                    cancellationToken)
                .ConfigureAwait(false);
        }

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

    private static async Task<AnalysisResultCommitStatus> ReadRouteStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid providerProfileId,
        long providerProfileRevision,
        long? expectedRouteRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                profiles.id,
                profiles.revision,
                profiles.base_endpoint,
                profiles.api_key_ciphertext IS NOT NULL,
                bindings.enabled,
                bindings.route_revision,
                validations.provider_profile_id IS NOT NULL
            FROM analysis_stage_bindings AS bindings
            INNER JOIN ai_provider_profiles AS profiles
                ON profiles.id = bindings.provider_profile_id
            LEFT JOIN provider_profile_validations AS validations
                ON validations.provider_profile_id = profiles.id
                AND validations.provider_profile_revision = profiles.revision
                AND validations.stage = bindings.stage
            WHERE bindings.stage = $stage;
            """;
        command.Parameters.AddWithValue("$stage", (int)AnalysisStage.TimelineAnalysis);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !Guid.TryParse(reader.GetString(0), out var activeProfileId)
            || activeProfileId != providerProfileId
            || reader.GetInt64(1) != providerProfileRevision
            || !Uri.TryCreate(reader.GetString(2), UriKind.Absolute, out var endpoint)
            || (!endpoint.IsLoopback && reader.GetInt64(3) != 1)
            || reader.GetInt64(4) != 1
            || (expectedRouteRevision.HasValue
                && reader.GetInt64(5) != expectedRouteRevision.Value)
            || reader.GetInt64(6) != 1)
        {
            return AnalysisResultCommitStatus.ProviderRevisionChanged;
        }

        return AnalysisResultCommitStatus.Committed;
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
        CommittingJob job,
        AnalysisWindowSnapshot? window)
    {
        var orderedEntries = entries
            .OrderBy(static entry => entry.Range.Start)
            .ThenBy(static entry => entry.Range.End)
            .ToArray();
        for (var index = 1; index < orderedEntries.Length; index++)
        {
            if (orderedEntries[index - 1].Range.End > orderedEntries[index].Range.Start)
            {
                throw new ArgumentException(
                    "Analysis results cannot contain overlapping timeline entries.",
                    nameof(entries));
            }
        }

        var allowedChunks = window?.Members
            .Select(static member => member.Chunk.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Origin != TimelineEntryOrigin.Analyzed
                || entry.EvidenceReferences.Count == 0
                || entry.EvidenceReferences.Any(evidence => allowedChunks is null
                    ? !string.Equals(
                        evidence.CaptureChunkId,
                        job.CaptureChunkId,
                        StringComparison.Ordinal)
                    : !allowedChunks.Contains(evidence.CaptureChunkId))
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

            if (window is not null
                && (entry.Range.Start < window.Range.Start
                    || entry.Range.End > window.Range.End
                    || window.ExistingEntries.Any(existing =>
                        existing.IsRewriteProtectedBy(window.Range)
                        && existing.Range.Start < entry.Range.End
                        && existing.Range.End > entry.Range.Start)))
            {
                throw new ArgumentException(
                    "Window analysis results must stay inside the window and avoid preserved entries.",
                    nameof(entries));
            }
        }
    }

    private static async Task<bool> WindowBaselineMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalysisWindowSnapshot window,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, revision
            FROM timeline_entries
            WHERE start_utc_ticks < $window_end_utc_ticks
                AND end_utc_ticks > $window_start_utc_ticks
            ORDER BY start_utc_ticks, end_utc_ticks, id;
            """;
        AddWindowParameters(command.Parameters, window.Range);

        var actual = new List<(Guid Id, long Revision)>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actual.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        }

        var expected = window.ExistingEntries
            .OrderBy(static entry => entry.Range.Start)
            .ThenBy(static entry => entry.Range.End)
            .ThenBy(static entry => entry.Id)
            .Select(static entry => (entry.Id, entry.Revision))
            .ToArray();
        return actual.SequenceEqual(expected);
    }

    private static async Task RewriteEligibleWindowEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeRange window,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE timeline_entries
            SET end_utc_ticks = $window_start_utc_ticks,
                end_offset_minutes = $window_start_offset_minutes,
                revision = revision + 1
            WHERE origin = 0
                AND range_edited_at IS NULL
                AND title_edited_at IS NULL
                AND summary_edited_at IS NULL
                AND category_edited_at IS NULL
                AND productivity_edited_at IS NULL
                AND tags_edited_at IS NULL
                AND start_utc_ticks < $window_start_utc_ticks
                AND end_utc_ticks > $window_start_utc_ticks
                AND end_utc_ticks <= $window_end_utc_ticks;

            DELETE FROM timeline_entries
            WHERE origin = 0
                AND range_edited_at IS NULL
                AND title_edited_at IS NULL
                AND summary_edited_at IS NULL
                AND category_edited_at IS NULL
                AND productivity_edited_at IS NULL
                AND tags_edited_at IS NULL
                AND start_utc_ticks >= $window_start_utc_ticks
                AND end_utc_ticks <= $window_end_utc_ticks;
            """;
        AddWindowParameters(command.Parameters, window);
        command.Parameters.AddWithValue(
            "$window_start_offset_minutes",
            checked((int)window.Start.Offset.TotalMinutes));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddWindowParameters(
        SqliteParameterCollection parameters,
        TimeRange window)
    {
        parameters.AddWithValue(
            "$window_start_utc_ticks",
            window.Start.UtcDateTime.Ticks);
        parameters.AddWithValue(
            "$window_end_utc_ticks",
            window.End.UtcDateTime.Ticks);
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
                SELECT
                    ordinal,
                    capture_chunk_id,
                    artifact_path,
                    contribution_start_utc_ticks,
                    contribution_start_offset_minutes,
                    contribution_end_utc_ticks,
                    contribution_end_offset_minutes
                FROM timeline_entry_evidence
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
                if (index >= entry.EvidenceReferences.Count)
                {
                    return false;
                }

                var expected = entry.EvidenceReferences[index];
                var contribution = expected.ContributionRange ?? entry.Range;
                if (reader.GetInt32(0) != index
                    || !string.Equals(
                        reader.GetString(1),
                        expected.CaptureChunkId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        reader.GetString(2),
                        expected.ArtifactPath,
                        StringComparison.Ordinal)
                    || reader.GetInt64(3) != contribution.Start.UtcDateTime.Ticks
                    || reader.GetInt32(4)
                        != checked((int)contribution.Start.Offset.TotalMinutes)
                    || reader.GetInt64(5) != contribution.End.UtcDateTime.Ticks
                    || reader.GetInt32(6)
                        != checked((int)contribution.End.Offset.TotalMinutes))
                {
                    return false;
                }

                index++;
            }

            if (index != entry.EvidenceReferences.Count)
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
        for (var index = 0; index < entry.EvidenceReferences.Count; index++)
        {
            var evidence = entry.EvidenceReferences[index];
            var contribution = evidence.ContributionRange ?? entry.Range;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO timeline_entry_evidence(
                    timeline_entry_id,
                    ordinal,
                    capture_chunk_id,
                    artifact_path,
                    contribution_start_utc_ticks,
                    contribution_start_offset_minutes,
                    contribution_end_utc_ticks,
                    contribution_end_offset_minutes)
                VALUES (
                    $timeline_entry_id,
                    $ordinal,
                    $capture_chunk_id,
                    $artifact_path,
                    $contribution_start_utc_ticks,
                    $contribution_start_offset_minutes,
                    $contribution_end_utc_ticks,
                    $contribution_end_offset_minutes);
                """;
            command.Parameters.AddWithValue("$timeline_entry_id", FormatId(entry.Id));
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$capture_chunk_id", evidence.CaptureChunkId);
            command.Parameters.AddWithValue("$artifact_path", evidence.ArtifactPath);
            command.Parameters.AddWithValue(
                "$contribution_start_utc_ticks",
                contribution.Start.UtcDateTime.Ticks);
            command.Parameters.AddWithValue(
                "$contribution_start_offset_minutes",
                checked((int)contribution.Start.Offset.TotalMinutes));
            command.Parameters.AddWithValue(
                "$contribution_end_utc_ticks",
                contribution.End.UtcDateTime.Ticks);
            command.Parameters.AddWithValue(
                "$contribution_end_offset_minutes",
                checked((int)contribution.End.Offset.TotalMinutes));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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
