using System.Globalization;
using Microsoft.Data.Sqlite;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using WinDayFlow.Infrastructure.Persistence;

namespace WinDayFlow.Infrastructure.Timeline;

public sealed class SqliteTimelineRepository : ITimelineStore
{
    private const string SelectColumns = """
        id,
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
        tags_edited_at
        """;

    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteTimelineRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task<IReadOnlyList<TimelineEntry>> GetForDayAsync(
        DateOnly day,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM timeline_entries
            WHERE local_date = $local_date
            ORDER BY start_utc_ticks, end_utc_ticks, id;
            """;
        command.Parameters.AddWithValue(
            "$local_date",
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var storedEntries = await ReadStoredEntriesAsync(command, cancellationToken)
            .ConfigureAwait(false);
        var entries = await MaterializeEntriesAsync(
                connection,
                transaction,
                storedEntries,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries;
    }

    public async Task<TimelineEntry?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry identifier cannot be empty.", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SelectColumns}
            FROM timeline_entries
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", FormatId(id));

        var storedEntries = await ReadStoredEntriesAsync(command, cancellationToken)
            .ConfigureAwait(false);
        var entries = await MaterializeEntriesAsync(
                connection,
                transaction,
                storedEntries,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return entries.SingleOrDefault();
    }

    public async Task AddAsync(
        TimelineEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var command = CreateEntryWriteCommand(
                         connection,
                         transaction,
                         entry,
                         isUpdate: false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertChildrenAsync(connection, transaction, entry, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(
        TimelineEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        int affectedRows;
        await using (var command = CreateEntryWriteCommand(
                         connection,
                         transaction,
                         entry,
                         isUpdate: true))
        {
            affectedRows = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (affectedRows == 0)
        {
            return false;
        }

        await DeleteChildrenAsync(connection, transaction, entry.Id, cancellationToken)
            .ConfigureAwait(false);
        await InsertChildrenAsync(connection, transaction, entry, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry identifier cannot be empty.", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM timeline_entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", FormatId(id));

        var affectedRows = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return affectedRows > 0;
    }

    private static SqliteCommand CreateEntryWriteCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimelineEntry entry,
        bool isUpdate)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = isUpdate
            ? """
                UPDATE timeline_entries
                SET local_date = $local_date,
                    start_utc_ticks = $start_utc_ticks,
                    start_offset_minutes = $start_offset_minutes,
                    end_utc_ticks = $end_utc_ticks,
                    end_offset_minutes = $end_offset_minutes,
                    title = $title,
                    summary = $summary,
                    category = $category,
                    productivity = $productivity,
                    origin = $origin,
                    revision = revision + 1,
                    confidence = $confidence,
                    evidence_capture_chunk_id = $evidence_capture_chunk_id,
                    evidence_artifact_path = $evidence_artifact_path,
                    analysis_version = $analysis_version,
                    range_edited_at = $range_edited_at,
                    title_edited_at = $title_edited_at,
                    summary_edited_at = $summary_edited_at,
                    category_edited_at = $category_edited_at,
                    productivity_edited_at = $productivity_edited_at,
                    tags_edited_at = $tags_edited_at
                WHERE id = $id AND revision = $revision;
                """
            : """
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
                    $range_edited_at,
                    $title_edited_at,
                    $summary_edited_at,
                    $category_edited_at,
                    $productivity_edited_at,
                    $tags_edited_at);
                """;

        AddEntryParameters(command.Parameters, entry);
        return command;
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
        parameters.AddWithValue("$start_utc_ticks", entry.Range.Start.UtcDateTime.Ticks);
        parameters.AddWithValue(
            "$start_offset_minutes",
            checked((int)entry.Range.Start.Offset.TotalMinutes));
        parameters.AddWithValue("$end_utc_ticks", entry.Range.End.UtcDateTime.Ticks);
        parameters.AddWithValue(
            "$end_offset_minutes",
            checked((int)entry.Range.End.Offset.TotalMinutes));
        parameters.AddWithValue("$title", entry.Title);
        parameters.AddWithValue("$summary", entry.Summary);
        parameters.AddWithValue("$category", (int)entry.Category);
        parameters.AddWithValue("$productivity", (int)entry.Productivity);
        parameters.AddWithValue("$origin", (int)entry.Origin);
        parameters.AddWithValue("$revision", entry.Revision);
        AddNullableParameter(parameters, "$confidence", entry.Confidence);
        AddNullableParameter(
            parameters,
            "$evidence_capture_chunk_id",
            entry.Evidence?.CaptureChunkId);
        AddNullableParameter(
            parameters,
            "$evidence_artifact_path",
            entry.Evidence?.ArtifactPath);
        AddNullableParameter(parameters, "$analysis_version", entry.AnalysisVersion);
        AddNullableParameter(
            parameters,
            "$range_edited_at",
            FormatTimestamp(entry.UserEdits.RangeEditedAt));
        AddNullableParameter(
            parameters,
            "$title_edited_at",
            FormatTimestamp(entry.UserEdits.TitleEditedAt));
        AddNullableParameter(
            parameters,
            "$summary_edited_at",
            FormatTimestamp(entry.UserEdits.SummaryEditedAt));
        AddNullableParameter(
            parameters,
            "$category_edited_at",
            FormatTimestamp(entry.UserEdits.CategoryEditedAt));
        AddNullableParameter(
            parameters,
            "$productivity_edited_at",
            FormatTimestamp(entry.UserEdits.ProductivityEditedAt));
        AddNullableParameter(
            parameters,
            "$tags_edited_at",
            FormatTimestamp(entry.UserEdits.TagsEditedAt));
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
                VALUES ($timeline_entry_id, $ordinal, $application_id, $display_name, $duration_ticks);
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

    private static async Task DeleteChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM timeline_entry_apps WHERE timeline_entry_id = $id;
            DELETE FROM timeline_entry_tags WHERE timeline_entry_id = $id;
            """;
        command.Parameters.AddWithValue("$id", FormatId(id));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StoredTimelineEntry>> ReadStoredEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var entries = new List<StoredTimelineEntry>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new StoredTimelineEntry(
                Guid.Parse(reader.GetString(0)),
                ReadTimestamp(reader.GetInt64(1), reader.GetInt32(2)),
                ReadTimestamp(reader.GetInt64(3), reader.GetInt32(4)),
                reader.GetString(5),
                reader.GetString(6),
                (ActivityCategory)reader.GetInt32(7),
                (ProductivityKind)reader.GetInt32(8),
                (TimelineEntryOrigin)reader.GetInt32(9),
                reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                ReadNullableTimestamp(reader, 15),
                ReadNullableTimestamp(reader, 16),
                ReadNullableTimestamp(reader, 17),
                ReadNullableTimestamp(reader, 18),
                ReadNullableTimestamp(reader, 19),
                ReadNullableTimestamp(reader, 20)));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<TimelineEntry>> MaterializeEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<StoredTimelineEntry> storedEntries,
        CancellationToken cancellationToken)
    {
        var entries = new List<TimelineEntry>(storedEntries.Count);
        foreach (var storedEntry in storedEntries)
        {
            var apps = await ReadAppsAsync(
                    connection,
                    transaction,
                    storedEntry.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            var tags = await ReadTagsAsync(
                    connection,
                    transaction,
                    storedEntry.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            entries.Add(storedEntry.ToDomain(apps, tags));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<AppUsage>> ReadAppsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT application_id, display_name, duration_ticks
            FROM timeline_entry_apps
            WHERE timeline_entry_id = $id
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$id", FormatId(id));

        var apps = new List<AppUsage>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            apps.Add(new AppUsage(
                reader.GetString(0),
                reader.GetString(1),
                new TimeSpan(reader.GetInt64(2))));
        }

        return apps;
    }

    private static async Task<IReadOnlyList<string>> ReadTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT value
            FROM timeline_entry_tags
            WHERE timeline_entry_id = $id
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$id", FormatId(id));

        var tags = new List<string>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static void AddNullableParameter(
        SqliteParameterCollection parameters,
        string name,
        object? value)
    {
        parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadTimestamp(long utcTicks, int offsetMinutes)
    {
        return new DateTimeOffset(utcTicks, TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(offsetMinutes));
    }

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.ParseExact(
                reader.GetString(ordinal),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
    }

    private sealed record StoredTimelineEntry(
        Guid Id,
        DateTimeOffset Start,
        DateTimeOffset End,
        string Title,
        string Summary,
        ActivityCategory Category,
        ProductivityKind Productivity,
        TimelineEntryOrigin Origin,
        long Revision,
        double? Confidence,
        string? EvidenceCaptureChunkId,
        string? EvidenceArtifactPath,
        string? AnalysisVersion,
        DateTimeOffset? RangeEditedAt,
        DateTimeOffset? TitleEditedAt,
        DateTimeOffset? SummaryEditedAt,
        DateTimeOffset? CategoryEditedAt,
        DateTimeOffset? ProductivityEditedAt,
        DateTimeOffset? TagsEditedAt)
    {
        public TimelineEntry ToDomain(
            IReadOnlyList<AppUsage> apps,
            IReadOnlyList<string> tags)
        {
            var evidence = EvidenceCaptureChunkId is null
                ? null
                : new EvidenceReference(EvidenceCaptureChunkId, EvidenceArtifactPath!);
            var provenance = new UserEditProvenance(
                RangeEditedAt,
                TitleEditedAt,
                SummaryEditedAt,
                CategoryEditedAt,
                ProductivityEditedAt,
                TagsEditedAt);

            return new TimelineEntry(
                Id,
                new TimeRange(Start, End),
                Title,
                Summary,
                Category,
                Productivity,
                apps,
                tags,
                Confidence,
                evidence,
                AnalysisVersion,
                provenance,
                Origin,
                Revision);
        }
    }
}
