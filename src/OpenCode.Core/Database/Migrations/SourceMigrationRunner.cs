namespace OpenCode.Core.Database.Migrations;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>Explicit owner-invoked upgrade of recognized V2
/// schemas. It never opens another database, imports credentials, or repairs prototypes.</summary>
public static class SourceMigrationRunner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<MigrationPlan> InspectAsync(SqliteConnection connection, MigrationTarget target, CancellationToken ct = default,
        string throughMigration = SourceMigrationCatalog.Current)
    {
        target.Require(connection);
        RequireCatalogBaseline();
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            var plan = await InspectLockedAsync(connection, transaction, throughMigration, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return plan;
        }
        finally { Gate.Release(); }
    }

    public static async Task<MigrationResult> ApplyAsync(SqliteConnection connection, MigrationTarget target, CancellationToken ct = default,
        string throughMigration = SourceMigrationCatalog.Current, TimeProvider? clock = null)
    {
        target.Require(connection);
        RequireCatalogBaseline();
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var originalForeignKeys = await ForeignKeysAsync(connection, ct).ConfigureAwait(false);
            Exception? failure = null;
            var committed = false;
            try
            {
                // Current-bound upgrades reach nullable_workspace_binding,
                // whose source explicitly requires foreign_keys=OFF. SQLite cannot
                // toggle that setting inside a transaction. This is connection-local,
                // restored even when classification rejects the database without writes.
                if (originalForeignKeys != 0) await SetForeignKeysAsync(connection, 0, ct).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction(deferred: false);
                var plan = await InspectLockedAsync(connection, transaction, throughMigration, ct).ConfigureAwait(false);
                var converted = plan.Journal != MigrationJournalKind.Canonical;
                if (converted)
                {
                    await MigrationSql.ExecuteAsync(connection, transaction,
                        "CREATE TABLE IF NOT EXISTS migration (id TEXT PRIMARY KEY, time_completed INTEGER NOT NULL)", ct).ConfigureAwait(false);
                    foreach (var id in plan.Completed)
                        await CompleteAsync(connection, transaction, id, ct, clock ?? TimeProvider.System).ConfigureAwait(false);
                }
                foreach (var migration in plan.Pending)
                {
                    if (migration.Statements is null) throw new MigrationRejectedException("unsupported_migration", migration.UnsupportedReason ?? "Migration is not implemented.");
                    try
                    {
                        foreach (var statement in migration.Statements)
                            await MigrationSql.ExecuteAsync(connection, transaction, statement, ct).ConfigureAwait(false);
                        // Validate the actual output profile before claiming this
                        // transition completed. A mismatch rolls back the whole batch.
                        await SourceSchema.RequireAsync(connection, transaction, migration.Id,
                            await SourceSchema.ObjectsAsync(connection, transaction, ct).ConfigureAwait(false), ct,
                            plan.Completed.Contains(SourceMigrationCatalog.PreSplitV2Marker, StringComparer.Ordinal)).ConfigureAwait(false);
                        // Actual SQL and the completion record are committed together.
                        await CompleteAsync(connection, transaction, migration.Id, ct, clock ?? TimeProvider.System).ConfigureAwait(false);
                    }
                    catch (SqliteException error) { throw new SourceMigrationFailedException(migration.Id, error); }
                }
                if (plan.Pending.Count > 0 || converted)
                {
                    var after = await InspectLockedAsync(connection, transaction, throughMigration, ct).ConfigureAwait(false);
                    if (after.Baseline != plan.Destination || after.Pending.Count != 0)
                        throw new MigrationRejectedException("incomplete_upgrade", "The migration result did not match the reviewed destination schema.");
                }
                if (plan.Pending.Count > 0)
                {
                    await using var check = connection.CreateCommand();
                    check.Transaction = transaction;
                    check.CommandText = "PRAGMA main.foreign_key_check";
                    await using var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                        throw new MigrationRejectedException("foreign_key_violation", "The upgrade would leave a foreign-key violation. No repair or partial commit was attempted.");
                }
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                committed = true;
                return new(plan.Baseline, plan.Destination, plan.Journal,
                    Array.AsReadOnly(plan.Pending.Select(migration => migration.Id).ToArray()), converted);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                try { await SetForeignKeysAsync(connection, originalForeignKeys, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception restoreError)
                {
                    var restoration = new InvalidOperationException(committed
                        ? "The upgrade committed, but the original connection foreign-key setting could not be restored. Discard this connection; do not replay completed migrations."
                        : "The upgrade did not commit, and the original connection foreign-key setting could not be restored. Discard this connection.", restoreError);
                    if (failure is not null) throw new AggregateException("Migration and connection restoration failed.", failure, restoration);
                    throw restoration;
                }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<MigrationPlan> InspectLockedAsync(SqliteConnection connection, SqliteTransaction transaction,
        string throughMigration, CancellationToken ct)
    {
        if (throughMigration is not (SourceMigrationCatalog.Current or SourceMigrationCatalog.SplitV2) && !SourceSchema.IsEarlyV2(throughMigration))
            throw new MigrationRejectedException("unsupported_destination", "Destination must be a reviewed early V2 checkpoint, the V2 split, or the current baseline.");
        var databases = await MigrationSql.ReadAsync(connection, transaction, "PRAGMA database_list", row => row.GetString(1), ct).ConfigureAwait(false);
        if (databases.Any(name => name is not ("main" or "temp")))
            throw new MigrationRejectedException("attached_database", "Upgrade requires a dedicated connection without attached databases.");
        var temporary = await MigrationSql.ReadAsync(connection, transaction, "SELECT name FROM sqlite_temp_master WHERE type IN ('table','view','trigger')",
            row => row.GetString(0), ct).ConfigureAwait(false);
        if (temporary.Count > 0) throw new MigrationRejectedException("temporary_schema", "Upgrade requires an empty temporary schema so source SQL cannot resolve to shadow objects.");
        var objects = await SourceSchema.ObjectsAsync(connection, transaction, ct).ConfigureAwait(false);
        if (!objects.Any(item => item.Type == "table" && item.Name is "session_v2" or "session"))
            throw new MigrationRejectedException("not_existing_v2", "Only existing recognized V2 schemas can be upgraded. Empty-database bootstrap remains with the database owner.");
        var canonical = objects.Any(item => item.Type == "table" && item.Name == "migration");
        var completed = new List<string>();
        var kind = MigrationJournalKind.Canonical;
        if (canonical)
        {
            await SourceSchema.RequireJournalColumnsAsync(connection, transaction, "migration", kind, ct).ConfigureAwait(false);
            completed = await MigrationSql.ReadAsync(connection, transaction, "SELECT id, time_completed FROM main.migration ORDER BY id",
                row =>
                {
                    if (row.IsDBNull(0) || row.GetValue(1) is not long time || time < 0)
                        throw new MigrationRejectedException("invalid_journal", "The canonical journal contains an invalid completion record.");
                    return row.GetString(0);
                }, ct).ConfigureAwait(false);
        }
        if (completed.Count == 0)
        {
            if (!objects.Any(item => item.Type == "table" && item.Name == "__drizzle_migrations"))
                throw new MigrationRejectedException("missing_journal", "No authoritative migration history was found. Schema presence alone never authorizes journal stamping.");
            var columns = await MigrationSql.ReadAsync(connection, transaction,
                "SELECT name FROM pragma_table_info('__drizzle_migrations', 'main')", row => row.GetString(0), ct).ConfigureAwait(false);
            kind = columns.Contains("name", StringComparer.Ordinal) ? MigrationJournalKind.DrizzleNamed : MigrationJournalKind.DrizzleTimestamp;
            await SourceSchema.RequireJournalColumnsAsync(connection, transaction, "__drizzle_migrations", kind, ct).ConfigureAwait(false);
            completed = kind == MigrationJournalKind.DrizzleNamed
                ? await MigrationSql.ReadAsync(connection, transaction, "SELECT name FROM main.__drizzle_migrations", row =>
                    row.IsDBNull(0) ? throw new MigrationRejectedException("invalid_journal", "Named Drizzle rows must identify actual source migrations.") : row.GetString(0), ct).ConfigureAwait(false)
                : await MigrationSql.ReadAsync(connection, transaction, "SELECT created_at FROM main.__drizzle_migrations", row => TimestampMigration(row.GetValue(0)), ct).ConfigureAwait(false);
        }
        var identities = completed.ToHashSet(StringComparer.Ordinal);
        if (identities.Count != completed.Count) throw new MigrationRejectedException("invalid_journal", "Duplicate migration identities are not a recognized source history.");
        var previousV2 = identities.Remove(SourceMigrationCatalog.PreSplitV2Marker);
        var known = SourceMigrationCatalog.All.Select(migration => migration.Id).ToArray();
        if (identities.Count == 0 || !identities.SetEquals(known.Take(identities.Count)))
            throw new MigrationRejectedException("unknown_history", "Migration identities must form an exact known prefix, without holes, unknown IDs or newer migrations. No count-based inference is used.");
        var splitIndex = Array.IndexOf(known, SourceMigrationCatalog.SplitV2);
        if (previousV2 && identities.Count < splitIndex)
            throw new MigrationRejectedException("unknown_history", "The pre-split marker cannot substitute for missing earlier source migrations.");
        var baseline = previousV2 && identities.Count == splitIndex ? SourceMigrationCatalog.PreSplitV2Marker : known[identities.Count - 1];
        if (!previousV2 && !SourceSchema.IsEarlyV2(baseline) && string.CompareOrdinal(baseline, SourceMigrationCatalog.EarliestRecognized) < 0)
            throw new MigrationRejectedException("unsupported_lineage", "Earlier storage requires an unimplemented source transition. No reset, V1 squash or legacy import is enabled.");
        var end = Array.IndexOf(known, throughMigration);
        if (end < identities.Count - 1) throw new MigrationRejectedException("downgrade", "The requested destination precedes the completed source history.");
        var pending = SourceMigrationCatalog.All.Skip(identities.Count).Take(end + 1 - identities.Count).ToArray();
        if (pending.FirstOrDefault(migration => !migration.Implemented) is { } blocked)
            throw new MigrationRejectedException("unsupported_migration", $"{blocked.Id}: {blocked.UnsupportedReason}");
        if (pending.Any(migration => migration.RequiresPreSplitV2) && !previousV2)
            throw new MigrationRejectedException("v1_squash_disabled", "The split SQL is implemented only for the authoritative pre-split V2 marker branch, not the destructive V1 squash.");
        await SourceSchema.RequireAsync(connection, transaction, baseline, objects, ct, previousV2).ConfigureAwait(false);
        if (objects.Any(item => item.Type == "trigger" && item.Table is "migration" or "__drizzle_migrations"))
            throw new MigrationRejectedException("invalid_journal", "Migration journal triggers are not part of the recognized source schema.");
        await RequireDataPreconditionsAsync(connection, transaction, pending, objects, ct).ConfigureAwait(false);
        return new(kind, baseline, Array.AsReadOnly(completed.Order(StringComparer.Ordinal).ToArray()), Array.AsReadOnly(pending));
    }

    private static async Task RequireDataPreconditionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<SourceMigration> pending, IReadOnlyList<SourceSchema.ObjectInfo> objects, CancellationToken ct)
    {
        // The source removes these tables or stored fields without mapping their contents. Under this
        // row-preserving contract, refuse populated inputs instead of fabricating a
        // replacement representation or silently executing the destructive branch.
        foreach (var table in pending.SelectMany(migration => migration.RequiresEmptyTables ?? []).Distinct(StringComparer.Ordinal))
        {
            if (!objects.Any(item => item.Type == "table" && item.Name == table)) continue;
            var rows = await MigrationSql.ReadAsync(connection, transaction,
                "SELECT 1 FROM main." + MigrationSql.Identifier(table) + " LIMIT 1", row => row.GetInt32(0), ct).ConfigureAwait(false);
            if (rows.Count != 0) throw new MigrationRejectedException("destructive_transition", $"The source transition removes stored data from populated '{table}' without a preserving mapping. No rows or fields were removed and no migration was stamped.");
        }
        if (pending.Any(migration => migration.Id == SourceMigrationCatalog.OrganizationState)
            && objects.Any(item => item.Type == "table" && item.Name == "account"))
        {
            // Source copies only active accounts' selections before dropping the
            // old field. Refuse any non-null selection that would not be copied.
            var unmapped = await MigrationSql.ReadAsync(connection, transaction, """
                SELECT 1 FROM account
                WHERE selected_org_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM account_state WHERE account_state.active_account_id = account.id)
                LIMIT 1
                """, row => row.GetInt32(0), ct).ConfigureAwait(false);
            if (unmapped.Count != 0)
                throw new MigrationRejectedException("unmapped_organization", "Source move_org_to_state would discard a non-active account's selected_org_id. No mapping or data-loss approval exists; the batch was not started.");
        }
        if (!pending.Any(migration => migration.RequiresPreSplitV2)) return;
        var legacyAlter = await MigrationSql.ReadAsync(connection, transaction, "PRAGMA legacy_alter_table", row => row.GetInt32(0), ct).ConfigureAwait(false);
        if (legacyAlter.Single() != 0)
            throw new MigrationRejectedException("legacy_alter_table", "The source rename requires SQLite foreign-key references to follow ALTER TABLE RENAME. Use a connection with legacy_alter_table disabled.");
        var v1Only = await MigrationSql.ReadAsync(connection, transaction, """
            SELECT 1
            FROM message
            WHERE NOT EXISTS (
              SELECT 1 FROM session_message WHERE session_message.session_id = message.session_id
            )
            LIMIT 1
            """, row => row.GetInt32(0), ct).ConfigureAwait(false);
        if (v1Only.Count != 0)
            throw new MigrationRejectedException("v1_only_history", "Previous V2 database contains V1-only session history. No rename or V1 import was attempted.");
    }

    private static string TimestampMigration(object value)
    {
        var time = value switch
        {
            long integer when integer >= 0 => integer,
            double number when double.IsFinite(number) && number >= 0 && Math.Truncate(number) == number && number <= 253_402_300_799_999d => (long)number,
            _ => throw new MigrationRejectedException("invalid_journal", "Drizzle created_at must contain an integer epoch-millisecond timestamp.")
        };
        if (time > 253_402_300_799_999L) throw new MigrationRejectedException("invalid_journal", "Drizzle timestamp is outside the recognized range.");
        // Source migration.ts maps UTC second prefixes, never row counts or inferred order.
        var prefix = DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_";
        var matches = SourceMigrationCatalog.All.Select(migration => migration.Id).Append(SourceMigrationCatalog.PreSplitV2Marker)
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new MigrationRejectedException("unknown_history", "A Drizzle timestamp does not uniquely identify a known source migration.");
    }

    private static Task CompleteAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken ct, TimeProvider clock) =>
        MigrationSql.ExecuteAsync(connection, transaction, "INSERT INTO migration (id, time_completed) VALUES (@id, @completed)", ct,
            ("@id", id), ("@completed", clock.GetUtcNow().ToUnixTimeMilliseconds()));

    private static async Task<int> ForeignKeysAsync(SqliteConnection connection, CancellationToken ct) =>
        (await MigrationSql.ReadAsync(connection, null, "PRAGMA foreign_keys", row => row.GetInt32(0), ct).ConfigureAwait(false)).Single();

    private static async Task SetForeignKeysAsync(SqliteConnection connection, int enabled, CancellationToken ct)
    {
        if (enabled is not (0 or 1)) throw new MigrationRejectedException("foreign_key_setting", "The SQLite foreign-key setting is unsupported.");
        await MigrationSql.ExecuteAsync(connection, null, enabled == 1 ? "PRAGMA foreign_keys = ON" : "PRAGMA foreign_keys = OFF", ct).ConfigureAwait(false);
        if (await ForeignKeysAsync(connection, ct).ConfigureAwait(false) != enabled)
            throw new MigrationRejectedException("foreign_key_setting", "Foreign-key mode could not be changed. Invoke migration outside an existing transaction.");
    }

    private static void RequireCatalogBaseline()
    {
        if (!SourceMigrationCatalog.All.Select(migration => migration.Id).SequenceEqual(DatabaseBootstrapSchema.MigrationIds))
            throw new MigrationRejectedException("stale_catalog", "The reviewed migration catalog and database owner's generated bootstrap baseline differ. Regenerate/review before upgrading.");
    }
}
