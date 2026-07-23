using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Application.Capture;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Analysis;

public sealed class SqliteCaptureAnalysisStore : ICaptureChunkStore, IAnalysisJobStore
{
    private const string CaptureChunkColumns = """
        id,
        video_relative_path,
        manifest_relative_path,
        start_utc_ticks,
        start_offset_minutes,
        end_utc_ticks,
        end_offset_minutes,
        frame_count,
        video_width,
        video_height,
        frame_rate_numerator,
        frame_rate_denominator,
        video_byte_count,
        persistence_generation_hex,
        target_epoch_hex,
        committed_at_utc_ticks,
        ingested_at_utc_ticks,
        availability
        """;

    private const string AnalysisJobColumns = """
        id,
        capture_chunk_id,
        provider_profile_id,
        provider_profile_revision,
        analysis_version,
        input_fingerprint,
        state,
        attempt,
        max_attempts,
        not_before_utc_ticks,
        lease_owner,
        lease_token,
        lease_expires_at_utc_ticks,
        error_code,
        error_detail,
        created_at_utc_ticks,
        updated_at_utc_ticks,
        completed_at_utc_ticks
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly string _evidenceRootPath;
    private readonly string _evidenceRootPrefix;

    public SqliteCaptureAnalysisStore(
        SqliteConnectionFactory connectionFactory,
        string evidenceRootPath)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRootPath);
        if (!Path.IsPathFullyQualified(evidenceRootPath))
        {
            throw new ArgumentException(
                "The evidence root must be a fully qualified path.",
                nameof(evidenceRootPath));
        }

        _evidenceRootPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(evidenceRootPath));
        _evidenceRootPrefix = Path.EndsInDirectorySeparator(_evidenceRootPath)
            ? _evidenceRootPath
            : _evidenceRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<CaptureChunkIngestResult> IngestCommittedAsync(
        CaptureChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Availability != CaptureChunkAvailability.Available)
        {
            throw new ArgumentException(
                "Only available committed evidence can be ingested.",
                nameof(chunk));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureContained(chunk.VideoPath);
        EnsureContained(chunk.ManifestPath);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await ReadCaptureChunkByIdAsync(
                connection,
                transaction,
                chunk.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!HasSameCommittedEvidence(existing, chunk))
            {
                throw new CaptureChunkConflictException(chunk.Id);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new CaptureChunkIngestResult(existing, Created: false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO capture_chunks(
                    id,
                    video_relative_path,
                    manifest_relative_path,
                    start_utc_ticks,
                    start_offset_minutes,
                    end_utc_ticks,
                    end_offset_minutes,
                    frame_count,
                    video_width,
                    video_height,
                    frame_rate_numerator,
                    frame_rate_denominator,
                    video_byte_count,
                    persistence_generation_hex,
                    target_epoch_hex,
                    committed_at_utc_ticks,
                    ingested_at_utc_ticks,
                    availability)
                VALUES (
                    $id,
                    $video_relative_path,
                    $manifest_relative_path,
                    $start_utc_ticks,
                    $start_offset_minutes,
                    $end_utc_ticks,
                    $end_offset_minutes,
                    $frame_count,
                    $video_width,
                    $video_height,
                    $frame_rate_numerator,
                    $frame_rate_denominator,
                    $video_byte_count,
                    $persistence_generation_hex,
                    $target_epoch_hex,
                    $committed_at_utc_ticks,
                    $ingested_at_utc_ticks,
                    $availability);
                """;
            AddCaptureChunkParameters(command.Parameters, chunk);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new CaptureChunkConflictException(chunk.Id);
            }
        }

        var persisted = await ReadCaptureChunkByIdAsync(
                connection,
                transaction,
                chunk.Id,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The ingested capture chunk could not be read back.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CaptureChunkIngestResult(persisted, Created: true);
    }

    async Task<CaptureChunk?> ICaptureChunkStore.GetAsync(
        string chunkId,
        CancellationToken cancellationToken)
    {
        CaptureChunk.ValidateIdentifier(chunkId);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var chunk = await ReadCaptureChunkByIdAsync(
                connection,
                transaction,
                chunkId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return chunk;
    }

    public async Task<AnalysisJobEnqueueResult> EnqueueAsync(
        AnalysisJob pendingJob,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pendingJob);
        if (pendingJob.State != AnalysisJobState.Pending)
        {
            throw new ArgumentException(
                "Only a pending analysis job can be enqueued.",
                nameof(pendingJob));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var sourceChunk = await ReadCaptureChunkByIdAsync(
                connection,
                transaction,
                pendingJob.CaptureChunkId,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceChunk is null)
        {
            throw new KeyNotFoundException(
                $"Capture chunk '{pendingJob.CaptureChunkId}' was not found.");
        }

        if (sourceChunk.Availability != CaptureChunkAvailability.Available)
        {
            throw new InvalidOperationException(
                $"Capture chunk '{pendingJob.CaptureChunkId}' is not available for analysis.");
        }

        var existingById = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                pendingJob.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingById is not null)
        {
            EnsureSameEnqueueDefinition(existingById, pendingJob);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AnalysisJobEnqueueResult(existingById, Created: false);
        }

        var existingByKey = await ReadAnalysisJobByKeyAsync(
                connection,
                transaction,
                pendingJob,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingByKey is not null)
        {
            EnsureSameEnqueueDefinition(existingByKey, pendingJob);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AnalysisJobEnqueueResult(existingByKey, Created: false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO analysis_jobs(
                    id,
                    capture_chunk_id,
                    provider_profile_id,
                    provider_profile_revision,
                    analysis_version,
                    input_fingerprint,
                    state,
                    attempt,
                    max_attempts,
                    not_before_utc_ticks,
                    lease_owner,
                    lease_token,
                    lease_expires_at_utc_ticks,
                    error_code,
                    error_detail,
                    created_at_utc_ticks,
                    updated_at_utc_ticks,
                    completed_at_utc_ticks)
                VALUES (
                    $id,
                    $capture_chunk_id,
                    $provider_profile_id,
                    $provider_profile_revision,
                    $analysis_version,
                    $input_fingerprint,
                    $state,
                    $attempt,
                    $max_attempts,
                    $not_before_utc_ticks,
                    NULL,
                    NULL,
                    NULL,
                    0,
                    NULL,
                    $created_at_utc_ticks,
                    $updated_at_utc_ticks,
                    NULL);
                """;
            AddPendingJobParameters(command.Parameters, pendingJob);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new AnalysisJobConflictException(pendingJob.Id);
            }
        }

        var persisted = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                pendingJob.Id,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The enqueued analysis job could not be read back.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AnalysisJobEnqueueResult(persisted, Created: true);
    }

    async Task<AnalysisJob?> IAnalysisJobStore.GetAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        ValidateJobId(jobId);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var job = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                jobId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    private void EnsureContained(EvidenceRelativePath relativePath)
    {
        var platformRelativePath = relativePath.Value.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(_evidenceRootPath, platformRelativePath));
        if (!resolved.StartsWith(_evidenceRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The evidence-relative path resolves outside the configured evidence root.",
                nameof(relativePath));
        }
    }

    private static bool HasSameCommittedEvidence(CaptureChunk left, CaptureChunk right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && left.VideoPath == right.VideoPath
            && left.ManifestPath == right.ManifestPath
            && left.Range == right.Range
            && left.FrameCount == right.FrameCount
            && left.VideoWidth == right.VideoWidth
            && left.VideoHeight == right.VideoHeight
            && left.FrameRateNumerator == right.FrameRateNumerator
            && left.FrameRateDenominator == right.FrameRateDenominator
            && left.VideoByteCount == right.VideoByteCount
            && left.PersistenceGeneration == right.PersistenceGeneration
            && left.TargetEpoch == right.TargetEpoch;
    }

    private static void EnsureSameEnqueueDefinition(
        AnalysisJob existing,
        AnalysisJob requested)
    {
        if (!string.Equals(
                existing.CaptureChunkId,
                requested.CaptureChunkId,
                StringComparison.Ordinal)
            || existing.ProviderProfileId != requested.ProviderProfileId
            || existing.ProviderProfileRevision != requested.ProviderProfileRevision
            || !string.Equals(
                existing.AnalysisVersion,
                requested.AnalysisVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                existing.InputFingerprint,
                requested.InputFingerprint,
                StringComparison.Ordinal)
            || existing.MaxAttempts != requested.MaxAttempts)
        {
            throw new AnalysisJobConflictException(requested.Id);
        }
    }

    private static void AddCaptureChunkParameters(
        SqliteParameterCollection parameters,
        CaptureChunk chunk)
    {
        parameters.AddWithValue("$id", chunk.Id);
        parameters.AddWithValue("$video_relative_path", chunk.VideoPath.Value);
        parameters.AddWithValue("$manifest_relative_path", chunk.ManifestPath.Value);
        parameters.AddWithValue("$start_utc_ticks", ToUtcTicks(chunk.Range.Start));
        parameters.AddWithValue(
            "$start_offset_minutes",
            checked((int)chunk.Range.Start.Offset.TotalMinutes));
        parameters.AddWithValue("$end_utc_ticks", ToUtcTicks(chunk.Range.End));
        parameters.AddWithValue(
            "$end_offset_minutes",
            checked((int)chunk.Range.End.Offset.TotalMinutes));
        parameters.AddWithValue("$frame_count", checked((long)chunk.FrameCount));
        parameters.AddWithValue("$video_width", checked((long)chunk.VideoWidth));
        parameters.AddWithValue("$video_height", checked((long)chunk.VideoHeight));
        parameters.AddWithValue(
            "$frame_rate_numerator",
            checked((long)chunk.FrameRateNumerator));
        parameters.AddWithValue(
            "$frame_rate_denominator",
            checked((long)chunk.FrameRateDenominator));
        parameters.AddWithValue("$video_byte_count", chunk.VideoByteCount);
        parameters.AddWithValue(
            "$persistence_generation_hex",
            FormatUInt64(chunk.PersistenceGeneration));
        parameters.AddWithValue("$target_epoch_hex", FormatUInt64(chunk.TargetEpoch));
        parameters.AddWithValue("$committed_at_utc_ticks", ToUtcTicks(chunk.CommittedAtUtc));
        parameters.AddWithValue("$ingested_at_utc_ticks", ToUtcTicks(chunk.IngestedAtUtc));
        parameters.AddWithValue("$availability", (int)chunk.Availability);
    }

    private static void AddPendingJobParameters(
        SqliteParameterCollection parameters,
        AnalysisJob job)
    {
        parameters.AddWithValue("$id", FormatId(job.Id));
        parameters.AddWithValue("$capture_chunk_id", job.CaptureChunkId);
        parameters.AddWithValue("$provider_profile_id", FormatId(job.ProviderProfileId));
        parameters.AddWithValue("$provider_profile_revision", job.ProviderProfileRevision);
        parameters.AddWithValue("$analysis_version", job.AnalysisVersion);
        parameters.AddWithValue("$input_fingerprint", job.InputFingerprint);
        parameters.AddWithValue("$state", (int)job.State);
        parameters.AddWithValue("$attempt", job.Attempt);
        parameters.AddWithValue("$max_attempts", job.MaxAttempts);
        parameters.AddWithValue("$not_before_utc_ticks", ToUtcTicks(job.NotBeforeUtc!.Value));
        parameters.AddWithValue("$created_at_utc_ticks", ToUtcTicks(job.CreatedAtUtc));
        parameters.AddWithValue("$updated_at_utc_ticks", ToUtcTicks(job.UpdatedAtUtc));
    }

    public async Task<AnalysisJob?> TryClaimNextAsync(
        string leaseOwner,
        DateTimeOffset claimedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseOwner(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            leaseDuration,
            TimeSpan.Zero);

        var claimedAt = claimedAtUtc.ToUniversalTime();
        var leaseExpiresAt = claimedAt.Add(leaseDuration);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        Guid? candidateId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT jobs.id
                FROM analysis_jobs AS jobs
                INNER JOIN capture_chunks AS chunks
                    ON chunks.id = jobs.capture_chunk_id
                WHERE jobs.state IN (0, 7)
                    AND jobs.not_before_utc_ticks <= $claimed_at_utc_ticks
                    AND jobs.attempt < jobs.max_attempts
                    AND chunks.availability = 0
                ORDER BY
                    jobs.not_before_utc_ticks,
                    jobs.created_at_utc_ticks,
                    jobs.id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$claimed_at_utc_ticks", ToUtcTicks(claimedAt));
            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            candidateId = scalar is string value ? Guid.Parse(value) : null;
        }

        if (!candidateId.HasValue)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        int affectedRows;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE analysis_jobs
                SET state = $claimed_state,
                    attempt = attempt + 1,
                    not_before_utc_ticks = NULL,
                    lease_owner = $lease_owner,
                    lease_token = $lease_token,
                    lease_expires_at_utc_ticks = $lease_expires_at_utc_ticks,
                    error_code = 0,
                    error_detail = NULL,
                    updated_at_utc_ticks = $claimed_at_utc_ticks
                WHERE id = $id
                    AND state IN (0, 7)
                    AND not_before_utc_ticks <= $claimed_at_utc_ticks
                    AND attempt < max_attempts;
                """;
            command.Parameters.AddWithValue("$claimed_state", (int)AnalysisJobState.Claimed);
            command.Parameters.AddWithValue("$lease_owner", leaseOwner);
            command.Parameters.AddWithValue("$lease_token", token);
            command.Parameters.AddWithValue(
                "$lease_expires_at_utc_ticks",
                ToUtcTicks(leaseExpiresAt));
            command.Parameters.AddWithValue("$claimed_at_utc_ticks", ToUtcTicks(claimedAt));
            command.Parameters.AddWithValue("$id", FormatId(candidateId.Value));
            affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var claimed = affectedRows == 1
            ? await ReadAnalysisJobByIdAsync(
                    connection,
                    transaction,
                    candidateId.Value,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task<AnalysisJob?> TryTransitionAsync(
        AnalysisJobLease lease,
        AnalysisJobState expectedState,
        AnalysisJobState nextState,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!AnalysisJobStateMachine.IsActive(expectedState)
            || !AnalysisJobStateMachine.CanTransition(expectedState, nextState)
            || nextState is AnalysisJobState.FailedRetryable or AnalysisJobState.FailedTerminal)
        {
            throw new InvalidOperationException(
                $"Analysis state transition {expectedState} -> {nextState} is not a normal leased transition.");
        }

        var changedAt = changedAtUtc.ToUniversalTime();
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var completed = nextState == AnalysisJobState.Completed;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = completed
                ? """
                    UPDATE analysis_jobs
                    SET state = $next_state,
                        lease_owner = NULL,
                        lease_token = NULL,
                        lease_expires_at_utc_ticks = NULL,
                        updated_at_utc_ticks = $changed_at_utc_ticks,
                        completed_at_utc_ticks = $changed_at_utc_ticks
                    WHERE id = $id
                        AND state = $expected_state
                        AND attempt = $attempt
                        AND lease_owner = $lease_owner
                        AND lease_token = $lease_token
                        AND lease_expires_at_utc_ticks > $changed_at_utc_ticks
                        AND updated_at_utc_ticks <= $changed_at_utc_ticks;
                    """
                : """
                    UPDATE analysis_jobs
                    SET state = $next_state,
                        updated_at_utc_ticks = $changed_at_utc_ticks
                    WHERE id = $id
                        AND state = $expected_state
                        AND attempt = $attempt
                        AND lease_owner = $lease_owner
                        AND lease_token = $lease_token
                        AND lease_expires_at_utc_ticks > $changed_at_utc_ticks
                        AND updated_at_utc_ticks <= $changed_at_utc_ticks;
                    """;
            AddLeaseIdentityParameters(command.Parameters, lease);
            command.Parameters.AddWithValue("$expected_state", (int)expectedState);
            command.Parameters.AddWithValue("$next_state", (int)nextState);
            command.Parameters.AddWithValue("$changed_at_utc_ticks", ToUtcTicks(changedAt));

            var affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affectedRows == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var updated = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                lease.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<AnalysisJob?> TryRenewLeaseAsync(
        AnalysisJobLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset newExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var renewedAt = renewedAtUtc.ToUniversalTime();
        var newExpiresAt = newExpiresAtUtc.ToUniversalTime();
        if (newExpiresAt <= renewedAt || newExpiresAt <= lease.ExpiresAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newExpiresAtUtc),
                "A renewed analysis lease must extend the current lease beyond the renewal time.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE analysis_jobs
                SET lease_expires_at_utc_ticks = $new_expires_at_utc_ticks,
                    updated_at_utc_ticks = $renewed_at_utc_ticks
                WHERE id = $id
                    AND state BETWEEN 1 AND 5
                    AND attempt = $attempt
                    AND lease_owner = $lease_owner
                    AND lease_token = $lease_token
                    AND lease_expires_at_utc_ticks > $renewed_at_utc_ticks
                    AND lease_expires_at_utc_ticks < $new_expires_at_utc_ticks
                    AND updated_at_utc_ticks <= $renewed_at_utc_ticks;
                """;
            AddLeaseIdentityParameters(command.Parameters, lease);
            command.Parameters.AddWithValue("$renewed_at_utc_ticks", ToUtcTicks(renewedAt));
            command.Parameters.AddWithValue(
                "$new_expires_at_utc_ticks",
                ToUtcTicks(newExpiresAt));
            var affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affectedRows == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var updated = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                lease.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<AnalysisJob?> TryFailAsync(
        AnalysisJobLease lease,
        AnalysisJobFailure failure,
        AnalysisFailureDisposition disposition,
        DateTimeOffset failedAtUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        var failedAt = failedAtUtc.ToUniversalTime();
        var retryAt = failedAt.Add(retryDelay);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                lease.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!HasCurrentLease(current, lease, failedAt))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var retryable = disposition == AnalysisFailureDisposition.Retryable
            && current!.Attempt < current.MaxAttempts;
        var state = retryable
            ? AnalysisJobState.FailedRetryable
            : AnalysisJobState.FailedTerminal;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE analysis_jobs
                SET state = $state,
                    not_before_utc_ticks = $not_before_utc_ticks,
                    lease_owner = NULL,
                    lease_token = NULL,
                    lease_expires_at_utc_ticks = NULL,
                    error_code = $error_code,
                    error_detail = $error_detail,
                    updated_at_utc_ticks = $failed_at_utc_ticks,
                    completed_at_utc_ticks = $completed_at_utc_ticks
                WHERE id = $id
                    AND state BETWEEN 1 AND 5
                    AND attempt = $attempt
                    AND lease_owner = $lease_owner
                    AND lease_token = $lease_token
                    AND lease_expires_at_utc_ticks > $failed_at_utc_ticks
                    AND updated_at_utc_ticks <= $failed_at_utc_ticks;
                """;
            AddLeaseIdentityParameters(command.Parameters, lease);
            command.Parameters.AddWithValue("$state", (int)state);
            AddNullableParameter(
                command.Parameters,
                "$not_before_utc_ticks",
                retryable ? ToUtcTicks(retryAt) : null);
            command.Parameters.AddWithValue("$error_code", (int)failure.Code);
            AddNullableParameter(command.Parameters, "$error_detail", failure.Detail);
            command.Parameters.AddWithValue("$failed_at_utc_ticks", ToUtcTicks(failedAt));
            AddNullableParameter(
                command.Parameters,
                "$completed_at_utc_ticks",
                retryable ? null : ToUtcTicks(failedAt));
            var affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affectedRows == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var updated = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                lease.JobId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<AnalysisJob?> TryCancelAsync(
        Guid jobId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        var cancelledAt = cancelledAtUtc.ToUniversalTime();
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE analysis_jobs
                SET state = $cancelled_state,
                    not_before_utc_ticks = NULL,
                    error_code = 0,
                    error_detail = NULL,
                    updated_at_utc_ticks = $cancelled_at_utc_ticks,
                    completed_at_utc_ticks = $cancelled_at_utc_ticks
                WHERE id = $id
                    AND state IN (0, 7)
                    AND updated_at_utc_ticks <= $cancelled_at_utc_ticks;
                """;
            command.Parameters.AddWithValue("$cancelled_state", (int)AnalysisJobState.Cancelled);
            command.Parameters.AddWithValue("$cancelled_at_utc_ticks", ToUtcTicks(cancelledAt));
            command.Parameters.AddWithValue("$id", FormatId(jobId));
            var affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (affectedRows == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var updated = await ReadAnalysisJobByIdAsync(
                connection,
                transaction,
                jobId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset recoveredAtUtc,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        var recoveredAt = recoveredAtUtc.ToUniversalTime();
        var retryAt = recoveredAt.Add(retryDelay);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE analysis_jobs
            SET state = CASE
                    WHEN attempt < max_attempts THEN $retryable_state
                    ELSE $terminal_state
                END,
                not_before_utc_ticks = CASE
                    WHEN attempt < max_attempts THEN $retry_at_utc_ticks
                    ELSE NULL
                END,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at_utc_ticks = NULL,
                error_code = $lease_expired_error,
                error_detail = NULL,
                updated_at_utc_ticks = $recovered_at_utc_ticks,
                completed_at_utc_ticks = CASE
                    WHEN attempt < max_attempts THEN NULL
                    ELSE $recovered_at_utc_ticks
                END
            WHERE state BETWEEN 1 AND 5
                AND lease_expires_at_utc_ticks <= $recovered_at_utc_ticks
                AND updated_at_utc_ticks <= $recovered_at_utc_ticks;
            """;
        command.Parameters.AddWithValue(
            "$retryable_state",
            (int)AnalysisJobState.FailedRetryable);
        command.Parameters.AddWithValue("$terminal_state", (int)AnalysisJobState.FailedTerminal);
        command.Parameters.AddWithValue("$retry_at_utc_ticks", ToUtcTicks(retryAt));
        command.Parameters.AddWithValue(
            "$lease_expired_error",
            (int)AnalysisJobErrorCode.LeaseExpired);
        command.Parameters.AddWithValue("$recovered_at_utc_ticks", ToUtcTicks(recoveredAt));
        var affectedRows = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affectedRows;
    }

    private static async Task<CaptureChunk?> ReadCaptureChunkByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string chunkId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {CaptureChunkColumns}
            FROM capture_chunks
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", chunkId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCaptureChunk(reader)
            : null;
    }

    private static CaptureChunk ReadCaptureChunk(SqliteDataReader reader)
    {
        var start = ReadTimestamp(reader.GetInt64(3), reader.GetInt32(4));
        var end = ReadTimestamp(reader.GetInt64(5), reader.GetInt32(6));
        return new CaptureChunk(
            reader.GetString(0),
            new EvidenceRelativePath(reader.GetString(1)),
            new EvidenceRelativePath(reader.GetString(2)),
            new TimeRange(start, end),
            checked((uint)reader.GetInt64(7)),
            checked((uint)reader.GetInt64(8)),
            checked((uint)reader.GetInt64(9)),
            checked((uint)reader.GetInt64(10)),
            checked((uint)reader.GetInt64(11)),
            reader.GetInt64(12),
            ParseUInt64(reader.GetString(13)),
            ParseUInt64(reader.GetString(14)),
            ReadUtcTimestamp(reader.GetInt64(15)),
            ReadUtcTimestamp(reader.GetInt64(16)),
            (CaptureChunkAvailability)reader.GetInt32(17));
    }

    private static async Task<AnalysisJob?> ReadAnalysisJobByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {AnalysisJobColumns}
            FROM analysis_jobs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", FormatId(jobId));
        return await ReadSingleAnalysisJobAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AnalysisJob?> ReadAnalysisJobByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalysisJob definition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {AnalysisJobColumns}
            FROM analysis_jobs
            WHERE capture_chunk_id = $capture_chunk_id
                AND provider_profile_id = $provider_profile_id
                AND provider_profile_revision = $provider_profile_revision
                AND analysis_version = $analysis_version;
            """;
        command.Parameters.AddWithValue("$capture_chunk_id", definition.CaptureChunkId);
        command.Parameters.AddWithValue(
            "$provider_profile_id",
            FormatId(definition.ProviderProfileId));
        command.Parameters.AddWithValue(
            "$provider_profile_revision",
            definition.ProviderProfileRevision);
        command.Parameters.AddWithValue("$analysis_version", definition.AnalysisVersion);
        return await ReadSingleAnalysisJobAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AnalysisJob?> ReadSingleAnalysisJobAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAnalysisJob(reader)
            : null;
    }

    private static AnalysisJob ReadAnalysisJob(SqliteDataReader reader)
    {
        var id = Guid.Parse(reader.GetString(0));
        var state = (AnalysisJobState)reader.GetInt32(6);
        var attempt = reader.GetInt32(7);
        AnalysisJobLease? lease = null;
        if (AnalysisJobStateMachine.IsActive(state))
        {
            lease = new AnalysisJobLease(
                id,
                reader.GetString(10),
                reader.GetString(11),
                attempt,
                ReadUtcTimestamp(reader.GetInt64(12)));
        }

        AnalysisJobFailure? failure = null;
        if (state is AnalysisJobState.FailedRetryable or AnalysisJobState.FailedTerminal)
        {
            failure = new AnalysisJobFailure(
                (AnalysisJobErrorCode)reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14));
        }

        return new AnalysisJob(
            id,
            reader.GetString(1),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            state,
            attempt,
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : ReadUtcTimestamp(reader.GetInt64(9)),
            lease,
            failure,
            ReadUtcTimestamp(reader.GetInt64(15)),
            ReadUtcTimestamp(reader.GetInt64(16)),
            reader.IsDBNull(17) ? null : ReadUtcTimestamp(reader.GetInt64(17)));
    }

    private static bool HasCurrentLease(
        AnalysisJob? job,
        AnalysisJobLease lease,
        DateTimeOffset operationAtUtc)
    {
        return job?.Lease is { } current
            && current.JobId == lease.JobId
            && current.Attempt == lease.Attempt
            && string.Equals(current.Owner, lease.Owner, StringComparison.Ordinal)
            && string.Equals(current.Token, lease.Token, StringComparison.Ordinal)
            && current.ExpiresAtUtc > operationAtUtc
            && job.UpdatedAtUtc <= operationAtUtc;
    }

    private static void AddLeaseIdentityParameters(
        SqliteParameterCollection parameters,
        AnalysisJobLease lease)
    {
        parameters.AddWithValue("$id", FormatId(lease.JobId));
        parameters.AddWithValue("$attempt", lease.Attempt);
        parameters.AddWithValue("$lease_owner", lease.Owner);
        parameters.AddWithValue("$lease_token", lease.Token);
    }

    private static void AddNullableParameter(
        SqliteParameterCollection parameters,
        string name,
        object? value)
    {
        parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static void ValidateLeaseOwner(string leaseOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (leaseOwner.Length > AnalysisJobLease.MaximumOwnerLength
            || !string.Equals(leaseOwner, leaseOwner.Trim(), StringComparison.Ordinal)
            || leaseOwner.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The analysis lease owner is invalid.", nameof(leaseOwner));
        }
    }

    private static void ValidateJobId(Guid jobId)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("An analysis job identifier cannot be empty.", nameof(jobId));
        }
    }

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static long ToUtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private static DateTimeOffset ReadUtcTimestamp(long ticks) =>
        new(ticks, TimeSpan.Zero);

    private static DateTimeOffset ReadTimestamp(long utcTicks, int offsetMinutes) =>
        ReadUtcTimestamp(utcTicks).ToOffset(TimeSpan.FromMinutes(offsetMinutes));

    private static string FormatUInt64(ulong value) =>
        value.ToString("X16", CultureInfo.InvariantCulture);

    private static ulong ParseUInt64(string value) =>
        ulong.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
