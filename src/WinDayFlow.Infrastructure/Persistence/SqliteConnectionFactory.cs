using Microsoft.Data.Sqlite;

namespace WinDayFlow.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        var fileName = Path.GetFileName(DatabasePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("The database path must include a file name.", nameof(databasePath));
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
