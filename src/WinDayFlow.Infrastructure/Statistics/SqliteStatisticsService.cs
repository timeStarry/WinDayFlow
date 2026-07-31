using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Statistics;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Statistics;

public sealed class SqliteStatisticsService : IStatisticsService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly string _dataRoot;
    private readonly TimeProvider _timeProvider;

    public SqliteStatisticsService(
        SqliteConnectionFactory connectionFactory,
        string dataRoot,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StatisticsSnapshot> GetAsync(
        StatisticsRange range,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(range) || range == StatisticsRange.Custom)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var end = _timeProvider.GetLocalNow();
        var start = GetStart(range, end);
        return await GetAsyncCore(range, start, end, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StatisticsSnapshot> GetAsync(
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken cancellationToken = default)
    {
        if (rangeEnd <= rangeStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rangeEnd),
                rangeEnd,
                "The statistics range end must be later than its start.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await GetAsyncCore(
                StatisticsRange.Custom,
                rangeStart,
                rangeEnd,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StatisticsSnapshot> GetAsyncCore(
        StatisticsRange range,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var chunks = await ReadChunksAsync(connection, start, end, cancellationToken)
            .ConfigureAwait(false);
        var timeline = await ReadTimelineAsync(connection, start, end, cancellationToken)
            .ConfigureAwait(false);
        var installation = await ReadInstallationAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var invocations = await ReadInvocationsAsync(
                connection,
                start,
                end,
                cancellationToken)
            .ConfigureAwait(false);
        var storage = await ScanStorageAsync(cancellationToken).ConfigureAwait(false);

        var recordedDuration = UnionDuration(chunks.Select(static value => value.Range));
        var normalizedTimeline = NormalizeTimeline(timeline, start, end);
        var categories = normalizedTimeline
            .GroupBy(static value => value.Category)
            .Select(static group => new StatisticsDurationBucket<ActivityCategory>(
                group.Key,
                TimeSpan.FromTicks(group.Sum(static value => value.Range.Duration.Ticks))))
            .OrderByDescending(static value => value.Duration)
            .ThenBy(static value => value.Key)
            .ToArray();
        var productivity = normalizedTimeline
            .GroupBy(static value => value.Productivity)
            .Select(static group => new StatisticsDurationBucket<ProductivityKind>(
                group.Key,
                TimeSpan.FromTicks(group.Sum(static value => value.Range.Duration.Ticks))))
            .OrderByDescending(static value => value.Duration)
            .ThenBy(static value => value.Key)
            .ToArray();
        var focusedDuration = productivity
            .Where(static value => value.Key == ProductivityKind.Focused)
            .Aggregate(TimeSpan.Zero, static (total, value) => total + value.Duration);

        var activeDays = new HashSet<DateOnly>();
        foreach (var chunk in chunks)
        {
            AddDates(activeDays, chunk.Range.Start, chunk.Range.End);
        }
        foreach (var entry in timeline)
        {
            activeDays.Add(entry.LocalDate);
        }

        return new StatisticsSnapshot(
            range,
            start,
            end,
            installation,
            recordedDuration,
            activeDays.Count,
            focusedDuration,
            categories,
            productivity,
            new CaptureFilterStatistics(
                chunks.Sum(static value => value.SampledCount),
                chunks.Sum(static value => value.BlackCount),
                chunks.Sum(static value => value.DuplicateCount),
                chunks.Sum(static value => value.RetainedCount)),
            invocations,
            storage);
    }

    private static async Task<List<ChunkRow>> ReadChunksAsync(
        SqliteConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT start_utc_ticks, start_offset_minutes,
                end_utc_ticks, end_offset_minutes,
                captured_frame_count, black_frame_count,
                duplicate_frame_count, frame_count
            FROM capture_chunks
            WHERE availability = 0
                AND start_utc_ticks < $end_ticks
                AND end_utc_ticks > $start_ticks
            ORDER BY start_utc_ticks, end_utc_ticks, id;
            """;
        command.Parameters.AddWithValue("$start_ticks", start.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("$end_ticks", end.UtcDateTime.Ticks);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<ChunkRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var observedStart = ReadTimestamp(reader.GetInt64(0), reader.GetInt32(1));
            var observedEnd = ReadTimestamp(reader.GetInt64(2), reader.GetInt32(3));
            rows.Add(new ChunkRow(
                new TimeRange(
                    observedStart > start ? observedStart : start,
                    observedEnd < end ? observedEnd : end),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }
        return rows;
    }

    private static async Task<List<TimelineRow>> ReadTimelineAsync(
        SqliteConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_date, start_utc_ticks, start_offset_minutes,
                end_utc_ticks, end_offset_minutes, category, productivity, id
            FROM timeline_entries
            WHERE start_utc_ticks < $end_ticks
                AND end_utc_ticks > $start_ticks
            ORDER BY start_utc_ticks, end_utc_ticks, id;
            """;
        command.Parameters.AddWithValue("$start_ticks", start.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("$end_ticks", end.UtcDateTime.Ticks);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<TimelineRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TimelineRow(
                DateOnly.ParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture),
                new TimeRange(
                    ReadTimestamp(reader.GetInt64(1), reader.GetInt32(2)),
                    ReadTimestamp(reader.GetInt64(3), reader.GetInt32(4))),
                (ActivityCategory)reader.GetInt32(5),
                (ProductivityKind)reader.GetInt32(6)));
        }
        return rows;
    }

    private static async Task<DateTimeOffset> ReadInstallationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_started_at_utc_ticks FROM app_installation WHERE id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The application installation record is missing.");
        return new DateTimeOffset(Convert.ToInt64(
            value,
            System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero);
    }

    private static async Task<ProviderInvocationStatistics> ReadInvocationsAsync(
        SqliteConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                COALESCE(SUM(CASE WHEN outcome = 1 THEN 1 ELSE 0 END), 0),
                AVG(CASE WHEN completed_at_utc_ticks IS NOT NULL
                    THEN completed_at_utc_ticks - started_at_utc_ticks END),
                CASE WHEN COUNT(*) > 0 AND COUNT(input_tokens) = COUNT(*)
                    THEN SUM(input_tokens) END,
                CASE WHEN COUNT(*) > 0 AND COUNT(output_tokens) = COUNT(*)
                    THEN SUM(output_tokens) END
            FROM provider_invocations
            WHERE started_at_utc_ticks >= $start_ticks
                AND started_at_utc_ticks < $end_ticks;
            """;
        command.Parameters.AddWithValue("$start_ticks", start.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("$end_ticks", end.UtcDateTime.Ticks);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderInvocationStatistics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : TimeSpan.FromTicks(checked((long)reader.GetDouble(2))),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    private async Task<StorageStatistics> ScanStorageAsync(
        CancellationToken cancellationToken)
    {
        var databaseBytes = FileLength(Path.Combine(_dataRoot, "windayflow.db"))
            + FileLength(Path.Combine(_dataRoot, "windayflow.db-wal"))
            + FileLength(Path.Combine(_dataRoot, "windayflow.db-shm"));
        var raw = await DirectorySizeAsync("chunks", cancellationToken).ConfigureAwait(false);
        var screenings = await DirectorySizeAsync("screenings", cancellationToken).ConfigureAwait(false);
        var cache = await DirectorySizeAsync("cache", cancellationToken).ConfigureAwait(false);
        var logs = await DirectorySizeAsync("logs", cancellationToken).ConfigureAwait(false);
        var exports = await DirectorySizeAsync("exports", cancellationToken).ConfigureAwait(false);
        return new StorageStatistics(databaseBytes, raw, screenings, cache, logs, exports);
    }

    private Task<long> DirectorySizeAsync(
        string directoryName,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var root = Path.Combine(_dataRoot, directoryName);
        if (!Directory.Exists(root))
        {
            return 0L;
        }

        var total = 0L;
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + FileLength(path));
            }
        }
        catch (DirectoryNotFoundException)
        {
            return total;
        }
        catch (UnauthorizedAccessException)
        {
            return total;
        }
        return total;
    }, cancellationToken);

    private static TimelineRow[] NormalizeTimeline(
        IReadOnlyList<TimelineRow> rows,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var normalized = new List<TimelineRow>(rows.Count);
        var cursor = start;
        foreach (var row in rows.OrderBy(static value => value.Range.Start)
                     .ThenBy(static value => value.Range.End))
        {
            var itemStart = row.Range.Start > cursor ? row.Range.Start : cursor;
            var itemEnd = row.Range.End < end ? row.Range.End : end;
            if (itemEnd <= itemStart)
            {
                continue;
            }
            normalized.Add(row with { Range = new TimeRange(itemStart, itemEnd) });
            cursor = itemEnd;
        }
        return normalized.ToArray();
    }

    private static TimeSpan UnionDuration(IEnumerable<TimeRange> ranges)
    {
        var ordered = ranges.OrderBy(static value => value.Start).ToArray();
        if (ordered.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var total = TimeSpan.Zero;
        var start = ordered[0].Start;
        var end = ordered[0].End;
        foreach (var range in ordered.Skip(1))
        {
            if (range.Start <= end)
            {
                if (range.End > end)
                {
                    end = range.End;
                }
                continue;
            }
            total += end - start;
            start = range.Start;
            end = range.End;
        }
        return total + (end - start);
    }

    private static void AddDates(
        HashSet<DateOnly> dates,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var current = DateOnly.FromDateTime(start.DateTime);
        var final = DateOnly.FromDateTime(end.AddTicks(-1).DateTime);
        while (current <= final)
        {
            dates.Add(current);
            current = current.AddDays(1);
        }
    }

    private static DateTimeOffset GetStart(StatisticsRange range, DateTimeOffset end)
    {
        if (range == StatisticsRange.All)
        {
            return DateTimeOffset.MinValue;
        }
        var today = new DateTimeOffset(end.Date, end.Offset);
        return range switch
        {
            StatisticsRange.Today => today,
            StatisticsRange.SevenDays => today.AddDays(-6),
            StatisticsRange.ThirtyDays => today.AddDays(-29),
            _ => throw new ArgumentOutOfRangeException(nameof(range)),
        };
    }

    private static DateTimeOffset ReadTimestamp(long utcTicks, int offsetMinutes) =>
        new DateTimeOffset(utcTicks, TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(offsetMinutes));

    private static long FileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed record ChunkRow(
        TimeRange Range,
        long SampledCount,
        long BlackCount,
        long DuplicateCount,
        long RetainedCount);

    private sealed record TimelineRow(
        DateOnly LocalDate,
        TimeRange Range,
        ActivityCategory Category,
        ProductivityKind Productivity);
}
