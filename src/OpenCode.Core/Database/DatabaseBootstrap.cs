namespace OpenCode.Core.Database;

using Microsoft.Data.Sqlite;
using OpenCode.Core.Database.Migrations;

// Derived from upstream database/migration.ts (MIT, copyright 2025 opencode).
// Full license and input provenance are retained in DatabaseBootstrapSchema.Generated.cs.
internal static class DatabaseBootstrap
{
    private static readonly Lock Gate = new();

    internal static void Apply(SqliteConnection connection, MigrationTarget? migrationTarget, TimeProvider clock)
    {
        lock (Gate)
        {
            // Typed opt-in only. The migration runner must own its transaction so
            // foreign-key mode can be set before the workspace rebuild starts.
            // Empty bootstrap and the default strict existing-schema gate stay separate.
            if (migrationTarget is not null)
            {
                migrationTarget.Require(connection);
                using var existing = connection.CreateCommand();
                existing.CommandText = "SELECT 1 FROM main.sqlite_master WHERE type = 'table' AND name IN ('session', 'session_v2') LIMIT 1";
                if (existing.ExecuteScalar() is not null)
                    SourceMigrationRunner.ApplyAsync(connection, migrationTarget, clock: clock).GetAwaiter().GetResult();
            }
            // Serialize classification with creation, including across separate processes.
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND substr(name, 1, 1) <> '_'
                """;
            var tables = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
                while (reader.Read()) tables.Add(reader.GetString(0));

            if (tables.Contains("session") || tables.Contains("session_v2"))
            {
                RequireCurrentSchema(command, tables, migrationTarget is not null);
                transaction.Commit();
                return;
            }
            if (tables.Count != 0)
                throw new DatabaseSchemaUnavailableException("Database is not empty and has no session table.");

            foreach (var statement in DatabaseBootstrapSchema.Statements)
            {
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
            command.CommandText = "CREATE TABLE migration (id TEXT PRIMARY KEY, time_completed INTEGER NOT NULL)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO migration (id, time_completed) VALUES (@id, @completed)";
            command.Parameters.Add("@id", SqliteType.Text);
            command.Parameters.Add("@completed", SqliteType.Integer);
            // Upstream's empty-database path stamps the current schema baseline, not
            // incremental data transformations. Never use this path for existing sessions.
            foreach (var id in DatabaseBootstrapSchema.MigrationIds)
            {
                command.Parameters["@id"].Value = id;
                command.Parameters["@completed"].Value = clock.GetUtcNow().ToUnixTimeMilliseconds();
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static void RequireCurrentSchema(SqliteCommand command, HashSet<string> tables, bool migrated)
    {
        if (DatabaseBootstrapSchema.Tables.Append("migration").FirstOrDefault(table => !tables.Contains(table)) is string missing)
            throw new DatabaseSchemaUnavailableException($"Required canonical table '{missing}' is missing.");
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index'";
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) indexes.Add(reader.GetString(0));
        if (DatabaseBootstrapSchema.Indexes.FirstOrDefault(index => !indexes.Contains(index)) is string missingIndex)
            throw new DatabaseSchemaUnavailableException($"Required canonical index '{missingIndex}' is missing.");

        command.CommandText = "SELECT id, time_completed FROM migration";
        var completed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetInt64(1) < 0 || !completed.Add(reader.GetString(0)))
                    throw new DatabaseSchemaUnavailableException("The migration journal contains an invalid completion record.");
            }
        }
        catch (SqliteException error)
        {
            throw new DatabaseSchemaUnavailableException("The canonical migration journal could not be read.", error);
        }
        // A conservative existing-database gate, not a schema integrity proof or an upgrade.
        // In particular, do not seed __drizzle_migrations or repair partial history here.
        // Only the opt-in runner can validate and preserve the additional source
        // pre-split marker. Never erase it to manufacture a fresh-bootstrap journal.
        if (migrated) completed.Remove(SourceMigrationCatalog.PreSplitV2Marker);
        if (!completed.SetEquals(DatabaseBootstrapSchema.MigrationIds))
            throw new DatabaseSchemaUnavailableException($"Migration history must match the {DatabaseBootstrapSchema.MigrationIds.Length}-entry source baseline ending at {DatabaseBootstrapSchema.MigrationIds[^1]}. Older, partial, legacy-only, and newer histories are unsupported.");
    }
}
