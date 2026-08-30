# Porting Ruleset 04: Persistence and Storage

This rulebook defines how to translate Drizzle ORM and SQLite queries from `@opencode-ai/core` into idiomatic, Native-AOT compatible C# (.NET 10).

---

## 1. Persistence Philosophy

OpenCode uses SQLite with Drizzle ORM for local state persistence. Key characteristics:
1. Standard tables: `session_v2`, `session_inbox`, `session_message`, `project`, `credential`, `workspace`.
2. JSON columns: Complex nested structures (e.g. `metadata`, `revert`, `model`, `diffs`) are stored as JSON text.
3. Durable inbox: Transactions consume inbox entries and insert visible messages atomically.
4. Embedded support: Operates on file-backed SQLite or in-memory (`:memory:`) for tests and embedded hosts.

In .NET 10:
- Use **`Microsoft.Data.Sqlite`** as the low-level provider.
- Use **Dapper** (or source-generated Dapper / lightweight AOT command helpers) for type-safe query mapping without heavyweight EF Core overhead.
- Enable SQLite WAL mode (`PRAGMA journal_mode = WAL;`) and busy timeouts (`PRAGMA busy_timeout = 5000;`).

---

## 2. Table Schema Migration

Create migrations as embedded SQL scripts executed during initialization:

```sql
-- Migration 001_initial_schema.sql
CREATE TABLE IF NOT EXISTS project (
    id TEXT PRIMARY KEY,
    worktree TEXT NOT NULL,
    vcs TEXT,
    time_created INTEGER NOT NULL,
    time_updated INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS session_v2 (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES project(id) ON DELETE CASCADE,
    workspace_id TEXT,
    parent_id TEXT,
    fork_session_id TEXT,
    fork_boundary TEXT, -- JSON
    slug TEXT NOT NULL,
    directory TEXT NOT NULL,
    path TEXT,
    title TEXT,
    version TEXT NOT NULL,
    share_url TEXT,
    cost REAL NOT NULL DEFAULT 0,
    tokens_input INTEGER NOT NULL DEFAULT 0,
    tokens_output INTEGER NOT NULL DEFAULT 0,
    tokens_reasoning INTEGER NOT NULL DEFAULT 0,
    tokens_cache_read INTEGER NOT NULL DEFAULT 0,
    tokens_cache_write INTEGER NOT NULL DEFAULT 0,
    revert TEXT, -- JSON
    permission TEXT, -- JSON
    agent TEXT,
    model TEXT, -- JSON
    metadata TEXT, -- JSON
    time_created INTEGER NOT NULL,
    time_updated INTEGER NOT NULL,
    time_idle INTEGER,
    time_viewed INTEGER,
    idle_outcome TEXT,
    time_compacting INTEGER,
    time_archived INTEGER,
    time_suspended INTEGER,
    resume_attempts INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS session_v2_project_idx ON session_v2(project_id);

CREATE TABLE IF NOT EXISTS session_inbox (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES session_v2(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    delivery TEXT NOT NULL,
    payload TEXT NOT NULL, -- JSON
    time_created INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS session_inbox_session_idx ON session_inbox(session_id);
```

---

## 3. Database Context and Connection Management

```csharp
namespace OpenCode.Core.Database;

using Microsoft.Data.Sqlite;

public interface IDatabase
{
    SqliteConnection CreateConnection();
    Task<T> RunInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct = default);
}

public sealed class SqliteDatabase : IDatabase, IAsyncDisposable
{
    private readonly string _connectionString;

    public SqliteDatabase(DatabaseOptions options)
    {
        _connectionString = options.Path switch
        {
            ":memory:" => "Data Source=InMemorySession;Mode=Memory;Cache=Shared",
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
```

---

## 4. Handling JSON Columns

Define helpers for serializing and deserializing JSON columns using `System.Text.Json`:

```csharp
namespace OpenCode.Core.Database;

public static class SqliteJson
{
    public static string ToJson<T>(T? value) where T : class =>
        value is null ? null! : JsonSerializer.Serialize(value, OpenCodeJsonContext.Default.Options);

    public static T? FromJson<T>(string? json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, OpenCodeJsonContext.Default.Options);
}
```

---

## 5. Atomic Inbox Admission and Delivery

The durable prompt contract mandates:
- Admission inserts `session_inbox`.
- Delivery deletes `session_inbox` and inserts visible `session_message` in one atomic transaction.

```csharp
public async Task<bool> DeliverInboxItemAsync(
    SessionId sessionId,
    string inboxId,
    SessionMessage message,
    CancellationToken ct)
{
    return await _db.RunInTransactionAsync(async (conn, tx) =>
    {
        // 1. Delete from inbox
        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = tx;
        deleteCmd.CommandText = "DELETE FROM session_inbox WHERE id = @id AND session_id = @sessionId";
        deleteCmd.Parameters.AddWithValue("@id", inboxId);
        deleteCmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
        var rowsDeleted = await deleteCmd.ExecuteNonQueryAsync(ct);
        if (rowsDeleted == 0) return false;

        // 2. Insert visible message
        await using var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT INTO session_message (id, session_id, type, payload, time_created)
            VALUES (@id, @sessionId, @type, @payload, @timeCreated);
        """;
        insertCmd.Parameters.AddWithValue("@id", message.Id.Value);
        insertCmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
        insertCmd.Parameters.AddWithValue("@type", message.GetType().Name);
        insertCmd.Parameters.AddWithValue("@payload", JsonSerializer.Serialize(message, OpenCodeJsonContext.Default.SessionMessage));
        insertCmd.Parameters.AddWithValue("@timeCreated", message.Time.Created.ToUnixTimeMilliseconds());
        await insertCmd.ExecuteNonQueryAsync(ct);

        return true;
    }, ct);
}
```
