namespace OpenCode.Core.Database;

using Microsoft.Data.Sqlite;
using OpenCode.Core.Database.Migrations;
using OpenCode.Schema;

public interface IDatabase : IAsyncDisposable
{
    TimeProvider Clock => TimeProvider.System;
    SqliteConnection CreateConnection();
    Task<T> RunInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct = default);
}

public sealed class SqliteDatabase : IDatabase
{
    public TimeProvider Clock { get; }
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly MigrationTarget? _migrationTarget;
    private readonly Lock _gate = new();
    private SqliteConnection? _memoryAnchor;
    private bool _initialized;
    private bool _disposed;

    public bool IsInitialized
    {
        get { lock (_gate) return _initialized && !_disposed; }
    }

    public SqliteDatabase(string? dbPath = null, MigrationTarget? migrationTarget = null, TimeProvider? clock = null)
    {
        Clock = clock ?? TimeProvider.System;
        dbPath ??= migrationTarget?.Path ?? Path.Combine(Config.ConfigLoader.GetDefaultDataDirectory(), OpenCodeChannel.DatabaseFileName);
        _databasePath = dbPath == ":memory:" ? dbPath : Path.GetFullPath(dbPath);
        migrationTarget?.RequireSelectedPath(_databasePath);
        _migrationTarget = migrationTarget;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath == ":memory:" ? $"OpenCode-{Guid.NewGuid():N}" : _databasePath,
            Mode = _databasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = _databasePath != ":memory:"
        }.ToString();
    }

    public SqliteConnection CreateConnection()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_databasePath != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            var connection = new SqliteConnection(_connectionString);
            try
            {
                // Shared in-memory connections belong to this database instance and
                // survive between operations, as upstream's lifetime-owned client does.
                if (_databasePath == ":memory:" && _memoryAnchor is null)
                {
                    _memoryAnchor = new SqliteConnection(_connectionString);
                    _memoryAnchor.Open();
                }
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
                command.ExecuteNonQuery();
                if (!_initialized) DatabaseBootstrap.Apply(connection, _migrationTarget, Clock);
                // Refuse unsupported existing schemas before changing their journal mode.
                command.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA cache_size = -64000;
                    """;
                command.ExecuteNonQuery();
                if (!_initialized)
                {
                    command.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
                    command.ExecuteNonQuery();
                }
                _initialized = true;
                return connection;
            }
            catch
            {
                connection.Dispose();
                if (!_initialized)
                {
                    _memoryAnchor?.Dispose();
                    _memoryAnchor = null;
                }
                throw;
            }
        }
    }

    public async Task<T> RunInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> action,
        CancellationToken ct = default)
    {
        var connection = CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(true);
        await using var transactionLifetime = transaction.ConfigureAwait(true);
        try
        {
            var result = await action(connection, transaction).ConfigureAwait(true);
            await transaction.CommitAsync(ct).ConfigureAwait(true);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(true);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _memoryAnchor?.Dispose();
            _memoryAnchor = null;
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class DatabaseSchemaUnavailableException(string reason, Exception? inner = null)
    : InvalidOperationException(reason + " Default initialization accepts only fresh-schema bootstrap or the exact current baseline. Reviewed incremental upgrades require an explicit MigrationTarget; unknown schemas and legacy imports remain unsupported. Do not copy a live database or synthesize migration history.", inner);
