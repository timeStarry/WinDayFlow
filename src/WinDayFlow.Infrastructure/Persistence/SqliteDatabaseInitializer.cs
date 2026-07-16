using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WinDayFlow.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer
{
    private const int LatestSchemaVersion = 2;

    private const string CreateMigrationTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );
        """;

    private const string MigrationVersion1Sql = """
        CREATE TABLE timeline_entries (
            id TEXT NOT NULL PRIMARY KEY,
            local_date TEXT NOT NULL,
            start_utc_ticks INTEGER NOT NULL,
            start_offset_minutes INTEGER NOT NULL,
            end_utc_ticks INTEGER NOT NULL,
            end_offset_minutes INTEGER NOT NULL,
            title TEXT NOT NULL CHECK (length(trim(title)) > 0),
            summary TEXT NOT NULL,
            category INTEGER NOT NULL,
            productivity INTEGER NOT NULL,
            origin INTEGER NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            confidence REAL NULL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
            evidence_capture_chunk_id TEXT NULL,
            evidence_artifact_path TEXT NULL,
            analysis_version TEXT NULL CHECK (
                analysis_version IS NULL OR length(trim(analysis_version)) > 0
            ),
            range_edited_at TEXT NULL,
            title_edited_at TEXT NULL,
            summary_edited_at TEXT NULL,
            category_edited_at TEXT NULL,
            productivity_edited_at TEXT NULL,
            tags_edited_at TEXT NULL,
            CHECK (end_utc_ticks > start_utc_ticks),
            CHECK (category BETWEEN 0 AND 9),
            CHECK (productivity BETWEEN 0 AND 4),
            CHECK (origin IN (0, 1)),
            CHECK (
                (evidence_capture_chunk_id IS NULL AND evidence_artifact_path IS NULL)
                OR
                (evidence_capture_chunk_id IS NOT NULL AND evidence_artifact_path IS NOT NULL)
            ),
            CHECK (
                (origin = 0
                    AND confidence IS NOT NULL
                    AND evidence_capture_chunk_id IS NOT NULL
                    AND analysis_version IS NOT NULL)
                OR
                (origin = 1
                    AND confidence IS NULL
                    AND evidence_capture_chunk_id IS NULL
                    AND analysis_version IS NULL)
            )
        );

        CREATE INDEX ix_timeline_entries_local_date_start
            ON timeline_entries(local_date, start_utc_ticks, end_utc_ticks, id);

        CREATE TABLE timeline_entry_apps (
            timeline_entry_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            application_id TEXT NOT NULL CHECK (length(trim(application_id)) > 0),
            display_name TEXT NOT NULL CHECK (length(trim(display_name)) > 0),
            duration_ticks INTEGER NOT NULL CHECK (duration_ticks >= 0),
            PRIMARY KEY (timeline_entry_id, ordinal),
            FOREIGN KEY (timeline_entry_id) REFERENCES timeline_entries(id) ON DELETE CASCADE
        );

        CREATE TABLE timeline_entry_tags (
            timeline_entry_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            value TEXT NOT NULL CHECK (length(trim(value)) > 0),
            PRIMARY KEY (timeline_entry_id, ordinal),
            FOREIGN KEY (timeline_entry_id) REFERENCES timeline_entries(id) ON DELETE CASCADE
        );
        """;

    private const string MigrationVersion2Sql = """
        CREATE TABLE app_settings (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            theme INTEGER NOT NULL CHECK (theme BETWEEN 0 AND 2),
            capture_enabled INTEGER NOT NULL CHECK (capture_enabled IN (0, 1)),
            cloud_analysis_enabled INTEGER NOT NULL CHECK (cloud_analysis_enabled IN (0, 1)),
            capture_consent_version INTEGER NULL CHECK (
                capture_consent_version IS NULL OR capture_consent_version > 0
            ),
            capture_consent_granted_at_utc TEXT NULL CHECK (
                capture_consent_granted_at_utc IS NULL
                OR length(trim(capture_consent_granted_at_utc)) > 0
            ),
            CHECK (
                (capture_consent_version IS NULL AND capture_consent_granted_at_utc IS NULL)
                OR
                (capture_consent_version IS NOT NULL AND capture_consent_granted_at_utc IS NOT NULL)
            ),
            CHECK (
                capture_enabled = 0
                OR (
                    capture_consent_version IS NOT NULL
                    AND capture_consent_granted_at_utc IS NOT NULL
                )
            )
        );

        INSERT INTO app_settings(
            id,
            theme,
            capture_enabled,
            cloud_analysis_enabled,
            capture_consent_version,
            capture_consent_granted_at_utc)
        VALUES (1, 0, 0, 0, NULL, NULL);
        """;

    private static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, MigrationVersion1Sql),
        new(2, MigrationVersion2Sql),
    ];

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteDatabaseInitializer(
        SqliteConnectionFactory connectionFactory,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
                connection,
                transaction: null,
                CreateMigrationTableSql,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = connection.BeginTransaction(deferred: false);

        var appliedVersions = await ReadAppliedVersionsAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        var unsupportedVersion = appliedVersions.FirstOrDefault(
            static version => version > LatestSchemaVersion);
        if (unsupportedVersion > 0)
        {
            throw new InvalidOperationException(
                $"Database schema version {unsupportedVersion} is newer than supported version {LatestSchemaVersion}.");
        }

        foreach (var migration in Migrations.OrderBy(static migration => migration.Version))
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            await ExecuteAsync(
                    connection,
                    transaction,
                    migration.Sql,
                    cancellationToken)
                .ConfigureAwait(false);

            await RecordMigrationAsync(
                    connection,
                    transaction,
                    migration.Version,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";

        var versions = new HashSet<int>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES ($version, $applied_at_utc);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue(
            "$applied_at_utc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record Migration(int Version, string Sql);
}
