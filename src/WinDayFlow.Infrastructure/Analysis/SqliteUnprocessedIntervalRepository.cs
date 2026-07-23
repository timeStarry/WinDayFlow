using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Analysis;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Analysis;

public sealed class SqliteUnprocessedIntervalRepository : IUnprocessedIntervalRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteUnprocessedIntervalRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<UnprocessedInterval>> GetForUtcRangeAsync(
        TimeRange utcRange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utcRange);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH ranked_jobs AS (
                SELECT
                    id,
                    capture_chunk_id,
                    state,
                    attempt,
                    error_code,
                    ROW_NUMBER() OVER (
                        PARTITION BY capture_chunk_id
                        ORDER BY
                            created_at_utc_ticks DESC,
                            provider_profile_revision DESC,
                            id DESC
                    ) AS job_rank
                FROM analysis_jobs
            )
            SELECT
                chunks.id,
                chunks.start_utc_ticks,
                chunks.start_offset_minutes,
                chunks.end_utc_ticks,
                chunks.end_offset_minutes,
                jobs.id,
                jobs.state,
                jobs.attempt,
                jobs.error_code
            FROM capture_chunks AS chunks
            LEFT JOIN ranked_jobs AS jobs
                ON jobs.capture_chunk_id = chunks.id
                AND jobs.job_rank = 1
            WHERE chunks.availability = $available
                AND chunks.start_utc_ticks < $range_end_utc_ticks
                AND chunks.end_utc_ticks > $range_start_utc_ticks
                AND NOT EXISTS (
                    SELECT 1
                    FROM analysis_jobs AS completed_jobs
                    WHERE completed_jobs.capture_chunk_id = chunks.id
                        AND completed_jobs.state = $completed_state
                )
            ORDER BY chunks.start_utc_ticks, chunks.id;
            """;
        command.Parameters.AddWithValue(
            "$available",
            (int)CaptureChunkAvailability.Available);
        command.Parameters.AddWithValue(
            "$completed_state",
            (int)AnalysisJobState.Completed);
        command.Parameters.AddWithValue(
            "$range_start_utc_ticks",
            utcRange.Start.UtcDateTime.Ticks);
        command.Parameters.AddWithValue(
            "$range_end_utc_ticks",
            utcRange.End.UtcDateTime.Ticks);

        var intervals = new List<UnprocessedInterval>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            intervals.Add(ReadInterval(reader));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intervals.AsReadOnly();
    }

    private static UnprocessedInterval ReadInterval(SqliteDataReader reader)
    {
        var range = new TimeRange(
            ReadTimestamp(reader.GetInt64(1), reader.GetInt32(2)),
            ReadTimestamp(reader.GetInt64(3), reader.GetInt32(4)));
        if (reader.IsDBNull(5))
        {
            return new UnprocessedInterval(
                reader.GetString(0),
                range,
                UnprocessedIntervalState.LocalOnly,
                latestJobId: null,
                attempt: null,
                errorCode: null);
        }

        var jobState = (AnalysisJobState)reader.GetInt32(6);
        AnalysisJobErrorCode? errorCode = reader.GetInt32(8) is var persistedError
            && persistedError != (int)AnalysisJobErrorCode.None
                ? (AnalysisJobErrorCode)persistedError
                : null;
        return new UnprocessedInterval(
            reader.GetString(0),
            range,
            MapState(jobState),
            Guid.Parse(reader.GetString(5)),
            reader.GetInt32(7),
            errorCode);
    }

    private static UnprocessedIntervalState MapState(AnalysisJobState state)
    {
        return state switch
        {
            AnalysisJobState.Pending => UnprocessedIntervalState.Queued,
            AnalysisJobState.Claimed
                or AnalysisJobState.Extracting
                or AnalysisJobState.Observing
                or AnalysisJobState.Summarizing
                or AnalysisJobState.Committing => UnprocessedIntervalState.Processing,
            AnalysisJobState.FailedRetryable => UnprocessedIntervalState.RetryScheduled,
            AnalysisJobState.FailedTerminal => UnprocessedIntervalState.Failed,
            AnalysisJobState.Cancelled => UnprocessedIntervalState.Cancelled,
            AnalysisJobState.Completed => throw new InvalidDataException(
                "A completed analysis job cannot be projected as unprocessed."),
            _ => throw new InvalidDataException(
                "The latest analysis job has an unsupported state."),
        };
    }

    private static DateTimeOffset ReadTimestamp(long utcTicks, int offsetMinutes)
    {
        return new DateTimeOffset(utcTicks, TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(offsetMinutes));
    }
}
