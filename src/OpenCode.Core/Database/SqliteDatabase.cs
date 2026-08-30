namespace OpenCode.Core.Database;

using Microsoft.Data.Sqlite;

public interface IDatabase : IAsyncDisposable
{
    SqliteConnection CreateConnection();
    Task<T> RunInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct = default);
}

public sealed class SqliteDatabase : IDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string? dbPath = null)
    {
        dbPath ??= Path.Combine(Config.ConfigLoader.GetDefaultDataDirectory(), "opencode.db");

        _connectionString = dbPath switch
        {
            ":memory:" => "Data Source=OpenCodeInMemory;Mode=Memory;Cache=Shared",
            var path => new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString()
        };
    }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
        """;
        cmd.ExecuteNonQuery();

        return connection;
    }

    public async Task<T> RunInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> action,
        CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var result = await action(connection, transaction);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
