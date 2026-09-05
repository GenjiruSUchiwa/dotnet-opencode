namespace OpenCode.Core.Database.Migrations;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

/// <summary>Reviewed V2 profiles. These checks compare identities,
/// columns/defaults/PKs, foreign keys and index definitions—not row or table counts.</summary>
internal static partial class SourceSchema
{
    private sealed record Column(string Name, string Type, bool Required = false, int Primary = 0, string? Default = null);
    private sealed record ForeignKey(string From, string Table, string To, string Delete = "CASCADE");
    private sealed record Index(string Name, bool Unique, string[] Columns, string? Predicate = null);
    private sealed record Table(Column[] Columns, ForeignKey[] ForeignKeys, Index[] Indexes,
        string[][]? UniqueKeys = null, string? ExactSql = null);
    internal sealed record ObjectInfo(string Type, string Name, string Table, string? Sql);

    internal static Task<List<ObjectInfo>> ObjectsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct) =>
        MigrationSql.ReadAsync(connection, transaction, "SELECT type, name, tbl_name, sql FROM main.sqlite_master ORDER BY name",
            row => new ObjectInfo(row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3)), ct);

    internal static async Task RequireAsync(SqliteConnection connection, SqliteTransaction transaction,
        string baseline, IReadOnlyList<ObjectInfo> objects, CancellationToken ct, bool previousV2 = false)
    {
        var expected = Definition(baseline);
        if (previousV2) AddPreviousV2(expected, baseline, objects);
        if (!previousV2 && IsEarlyV2(baseline)) AddEarlyV2(expected, baseline, objects);
        if (objects.Any(item => item.Name is "__new_workspace" or "__new_project_directory"))
            throw Rejected("A source rebuild target already exists; no scratch table will be overwritten.");
        foreach (var item in objects.Where(item => !item.Name.StartsWith("sqlite_", StringComparison.Ordinal)))
        {
            if (item.Type == "table" && !item.Name.StartsWith('_') && item.Name != "migration" && !expected.ContainsKey(item.Name))
                throw Rejected($"Unrecognized source table: {item.Name}.");
            if (item.Type is "view" or "trigger" && (!item.Name.StartsWith('_') || expected.ContainsKey(item.Table)))
                throw Rejected($"Unrecognized source {item.Type}: {item.Name}.");
        }
        foreach (var pair in expected)
        {
            var table = objects.SingleOrDefault(item => item.Type == "table" && item.Name == pair.Key)
                ?? throw Rejected($"Required source table is missing: {pair.Key}.");
            // The early input inbox intentionally uses AUTOINCREMENT and UNIQUE.
            // Accept those only with its complete reviewed CREATE statement, not
            // by disabling the general table-options guard for other schemas.
            if (table.Sql is null || (pair.Value.ExactSql is { } exact
                    ? CanonicalSql(table.Sql) != CanonicalSql(exact)
                    : ExtraTableFeatures().IsMatch(table.Sql)))
                throw Rejected($"Unsupported table options/constraints on {pair.Key}.");
            var columns = await ColumnsAsync(connection, transaction, pair.Key, ct).ConfigureAwait(false);
            // 20260228203230_blue_harpoon explicitly used INTEGER PRIMARY KEY NOT NULL;
            // fresh schema.gen.ts omits NOT NULL on that same rowid primary key.
            if (pair.Key == "account_state" && columns.FirstOrDefault(column => column.Name == "id") is { Required: true })
                columns = columns.Select(column => column.Name == "id" ? column with { Required = false } : column).ToList();
            if (!columns.OrderBy(column => column.Name, StringComparer.Ordinal).SequenceEqual(pair.Value.Columns.OrderBy(column => column.Name, StringComparer.Ordinal)))
                throw Rejected($"Columns, defaults or primary key do not match the recognized {baseline} profile for {pair.Key}.");
            var foreignKeys = await MigrationSql.ReadAsync(connection, transaction,
                "SELECT seq, \"table\", \"from\", \"to\", on_update, on_delete, match FROM pragma_foreign_key_list(@table, 'main')",
                row => (Sequence: row.GetInt32(0), Table: row.GetString(1), From: row.GetString(2), To: row.IsDBNull(3) ? "" : row.GetString(3),
                    Update: row.GetString(4), Delete: row.GetString(5), Match: row.GetString(6)), ct, ("@table", pair.Key)).ConfigureAwait(false);
            if (foreignKeys.Any(key => key.Sequence != 0 || key.Update != "NO ACTION" || key.Match != "NONE")
                || !foreignKeys.Select(key => new ForeignKey(key.From, key.Table, key.To, key.Delete)).OrderBy(key => key.From, StringComparer.Ordinal)
                    .SequenceEqual(pair.Value.ForeignKeys.OrderBy(key => key.From, StringComparer.Ordinal)))
                throw Rejected($"Foreign keys do not match the source profile for {pair.Key}.");
            var indexes = await MigrationSql.ReadAsync(connection, transaction,
                "SELECT name, \"unique\", origin, partial FROM pragma_index_list(@table, 'main')",
                row => (Name: row.GetString(0), Unique: row.GetInt32(1) != 0, Origin: row.GetString(2), Partial: row.GetInt32(3) != 0), ct, ("@table", pair.Key)).ConfigureAwait(false);
            var primary = pair.Value.Columns.Where(column => column.Primary > 0).OrderBy(column => column.Primary).ToArray();
            var needsPrimaryIndex = primary.Length > 0 && !(primary.Length == 1 && primary[0].Type == "INTEGER");
            if (indexes.Count(index => index.Origin == "pk") != (needsPrimaryIndex ? 1 : 0)) throw Rejected($"Primary-key index mismatch on {pair.Key}.");
            if (indexes.Any(index => index.Origin is not ("pk" or "c" or "u"))
                || indexes.Count(index => index.Origin == "u") != (pair.Value.UniqueKeys?.Length ?? 0))
                throw Rejected($"Unexpected unique constraints on {pair.Key}.");
            if (!indexes.Where(index => index.Origin == "c").Select(index => index.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(pair.Value.Indexes.Select(index => index.Name))) throw Rejected($"Unexpected or missing indexes on {pair.Key}.");
            foreach (var index in indexes)
            {
                var indexed = await MigrationSql.ReadAsync(connection, transaction,
                    "SELECT name, \"desc\", coll, cid FROM pragma_index_xinfo(@index, 'main') WHERE \"key\" = 1 ORDER BY seqno",
                    row => (Name: row.IsDBNull(0) ? "" : row.GetString(0), Descending: row.GetInt32(1) != 0,
                        Collation: row.IsDBNull(2) ? "" : row.GetString(2), Column: row.GetInt32(3)), ct, ("@index", index.Name)).ConfigureAwait(false);
                var specification = index.Origin switch
                {
                    "pk" => new Index(index.Name, true, primary.Select(column => column.Name).ToArray()),
                    "u" => new Index(index.Name, true, pair.Value.UniqueKeys?.SingleOrDefault(key => key.SequenceEqual(indexed.Select(column => column.Name)))
                        ?? throw Rejected($"Unique constraint columns differ from the source on {pair.Key}.")),
                    _ => pair.Value.Indexes.Single(item => item.Name == index.Name),
                };
                if (index.Unique != specification.Unique || index.Partial != (specification.Predicate is not null))
                    throw Rejected($"Index attributes differ from the source: {index.Name}.");
                if (indexed.Any(column => column.Descending || column.Collation != "BINARY" || column.Column < 0)
                    || !indexed.Select(column => column.Name).SequenceEqual(specification.Columns)) throw Rejected($"Index columns differ from the source: {index.Name}.");
                if (specification.Predicate is not null)
                {
                    var definition = objects.SingleOrDefault(item => item.Type == "index" && item.Name == index.Name)?.Sql ?? "";
                    var where = Regex.Match(definition, @"\bWHERE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                    if (!where.Success || CanonicalSql(definition[(where.Index + where.Length)..]) != CanonicalSql(specification.Predicate))
                        throw Rejected($"Partial index predicate differs from the source: {index.Name}.");
                }
            }
        }
    }

    internal static async Task RequireJournalColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string table,
        MigrationJournalKind kind, CancellationToken ct)
    {
        var columns = await ColumnsAsync(connection, transaction, table, ct).ConfigureAwait(false);
        var definitions = await MigrationSql.ReadAsync(connection, transaction,
            "SELECT sql FROM main.sqlite_master WHERE type = 'table' AND name = @table", row => row.IsDBNull(0) ? "" : row.GetString(0), ct, ("@table", table)).ConfigureAwait(false);
        var keys = await MigrationSql.ReadAsync(connection, transaction,
            "SELECT id FROM pragma_foreign_key_list(@table, 'main')", row => row.GetInt32(0), ct, ("@table", table)).ConfigureAwait(false);
        var indexes = await MigrationSql.ReadAsync(connection, transaction,
            "SELECT origin FROM pragma_index_list(@table, 'main')", row => row.GetString(0), ct, ("@table", table)).ConfigureAwait(false);
        if (definitions.Count != 1 || ExtraTableFeatures().IsMatch(definitions[0]) || keys.Count != 0
            || indexes.Any(origin => origin != "pk") || indexes.Count != (kind == MigrationJournalKind.Canonical ? 1 : 0))
            throw new MigrationRejectedException("invalid_journal", "Migration journal constraints or indexes are not part of the recognized source schema.");
        if (kind == MigrationJournalKind.Canonical)
        {
            Column[] expected = [new("id", "TEXT", Primary: 1), new("time_completed", "INTEGER", true)];
            if (!columns.OrderBy(column => column.Name, StringComparer.CurrentCulture).SequenceEqual(expected.OrderBy(column => column.Name, StringComparer.CurrentCulture)))
                throw new MigrationRejectedException("invalid_journal", "The canonical migration journal has an unrecognized schema.");
            return;
        }
        // Reviewed Drizzle sqlite-core/async/session.js and up-migrations/sqlite.js:
        // version 1 adds BOTH name and applied_at. Partial journal upgrades are rejected.
        var names = kind == MigrationJournalKind.DrizzleNamed ? new[] { "id", "hash", "created_at", "name", "applied_at" } : ["id", "hash", "created_at"];
        if (!columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names)
            || columns.Any(column => column.Default is not null)
            || columns.Single(column => column.Name == "id") is not { Type: "INTEGER", Primary: 1 }
            || columns.Single(column => column.Name == "hash") is not { Type: "TEXT", Required: true, Primary: 0 }
            || columns.Single(column => column.Name == "created_at") is not { Type: "NUMERIC" or "INTEGER", Primary: 0 }
            || kind == MigrationJournalKind.DrizzleNamed && (columns.Single(column => column.Name == "name") is not { Type: "TEXT", Primary: 0 }
                || columns.Single(column => column.Name == "applied_at") is not { Type: "TEXT", Primary: 0 }))
            throw new MigrationRejectedException("invalid_journal", "The Drizzle migration journal has an unrecognized schema.");
    }

    private static Task<List<Column>> ColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string table, CancellationToken ct) =>
        MigrationSql.ReadAsync(connection, transaction,
            "SELECT name, type, \"notnull\", pk, dflt_value, hidden FROM pragma_table_xinfo(@table, 'main')",
            row => row.GetInt32(5) != 0 ? throw Rejected($"Generated/hidden columns are unsupported on {table}.")
                : new Column(row.GetString(0), row.GetString(1).ToUpperInvariant(), row.GetInt32(2) != 0, row.GetInt32(3),
                    row.IsDBNull(4) ? null : row.GetString(4).Trim()), ct, ("@table", table));

    private static Dictionary<string, Table> Definition(string baseline)
    {
        bool Has(string id) => string.CompareOrdinal(baseline, id) >= 0;
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        Add("account_state", [new("id", "INTEGER", Primary: 1), .. Text(false, "active_account_id", "active_org_id")], [new("active_account_id", "account", "id", "SET NULL")]);
        Add("account", [Id(), .. Text(true, "email", "url", "access_token", "refresh_token"), .. Integer(false, "token_expiry"), .. Integer(true, "time_created", "time_updated")]);
        Add("control_account", [new("email", "TEXT", true, 1), new("url", "TEXT", true, 2), .. Text(true, "access_token", "refresh_token"),
            .. Integer(false, "token_expiry"), .. Integer(true, "active", "time_created", "time_updated")]);
        Add("credential", [Id(), .. Text(false, "integration_id", "connector_id", "method_id"), .. Text(true, "label", "value"),
            .. Integer(false, "active"), .. Integer(true, "time_created", "time_updated")]);
        Add("event_sequence", [new("aggregate_id", "TEXT", Primary: 1), .. Integer(true, "seq"), .. Text(false, "owner_id")]);
        Add("event", [Id(), .. Text(true, "aggregate_id", "type", "data"), .. Integer(true, "seq"), new("created", "INTEGER", true, Default: "0")],
            [new("aggregate_id", "event_sequence", "aggregate_id")],
            [new("event_aggregate_seq_idx", true, ["aggregate_id", "seq"]), new("event_aggregate_type_seq_idx", false, ["aggregate_id", "type", "seq"])]);
        Add("kv", [new("key", "TEXT", Primary: 1), .. Text(true, "value"), .. Integer(true, "time_created", "time_updated")]);
        Add("permission", [Id(), .. Text(true, "project_id", "action", "resource"), .. Integer(true, "time_created", "time_updated")],
            [new("project_id", "project", "id")], [new("permission_project_action_resource_idx", true, ["project_id", "action", "resource"])]);
        Add("project_directory", [new("project_id", "TEXT", true, 1), new("directory", "TEXT", true, 2), .. Text(false, "type", "strategy"), .. Integer(true, "time_created")], [new("project_id", "project", "id")]);
        Add("project", [Id(), .. Text(true, "worktree", "sandboxes"), .. Text(false, "vcs", "name", "icon_url", "icon_url_override", "icon_color", "commands"),
            .. Integer(true, "time_created", "time_updated"), .. Integer(false, "time_initialized")]);
        Add("instruction_blob", [new("hash", "TEXT", Primary: 1), .. Text(false, "value")]);
        Add("instruction_entry", [new("session_id", "TEXT", true, 1), new("key", "TEXT", true, 2), .. Text(false, "value"),
            new("removed", "INTEGER", true, Default: "false"), .. Integer(true, "time_created", "time_updated")], [new("session_id", "session_v2", "id")]);
        Add("instruction_state", [new("session_id", "TEXT", Primary: 1), .. Integer(true, "epoch_start", "through_seq"),
            .. Text(true, "initial_values", "current_values")], [new("session_id", "session_v2", "id")]);
        Add("session_message", [Id(), .. Text(true, "session_id", "type", "data"), .. Integer(true, "seq", "time_created", "time_updated")], [new("session_id", "session_v2", "id")],
            [new("session_message_session_seq_idx", true, ["session_id", "seq"]), new("session_message_session_type_seq_idx", false, ["session_id", "type", "seq"]),
                new("session_message_session_time_created_id_idx", false, ["session_id", "time_created", "id"]), new("session_message_time_created_idx", false, ["time_created"])]);
        Add("session_pending", [Id(), .. Text(true, "session_id", "type", "data"), .. Text(false, "delivery"), .. Integer(true, "admitted_seq", "time_created")],
            [new("session_id", "session_v2", "id")], [new("session_pending_session_delivery_seq_idx", false, ["session_id", "delivery", "admitted_seq"]),
                new("session_pending_session_compaction_idx", true, ["session_id"], "\"session_pending\".\"type\" = 'compaction'"),
                new("session_pending_session_admitted_seq_idx", true, ["session_id", "admitted_seq"])]);
        List<Column> session =
        [
            Id(), .. Text(true, "project_id", "slug", "directory", "version"),
            .. Text(false, "workspace_id", "parent_id", "fork_session_id", "fork_boundary", "path", "title", "share_url", "summary_diffs", "metadata", "revert", "permission", "agent", "model"),
            .. Integer(false, "summary_additions", "summary_deletions", "summary_files", "time_compacting", "time_archived", "time_suspended"),
            .. Integer(true, "time_created", "time_updated"), new("cost", "REAL", true, Default: "0")
        ];
        session.AddRange(new[] { "tokens_input", "tokens_output", "tokens_reasoning", "tokens_cache_read", "tokens_cache_write" }.Select(name => new Column(name, "INTEGER", true, Default: "0")));
        if (Has("20260811161259_execution_claim_attempts")) session.Add(new("resume_attempts", "INTEGER", true, Default: "0"));
        if (Has("20260819222447_session_viewed_state")) { session.AddRange(Integer(false, "time_idle", "time_viewed")); session.AddRange(Text(false, "idle_outcome")); }
        Add("session_v2", session.ToArray(), [new("project_id", "project", "id")], [new("session_v2_project_idx", false, ["project_id"]),
            new("session_v2_workspace_idx", false, ["workspace_id"]), new("session_v2_parent_idx", false, ["parent_id"]),
            new("session_v2_time_suspended_idx", false, ["time_suspended"], "\"session_v2\".\"time_suspended\" is not null")]);
        if (Has(SourceMigrationCatalog.WorkspaceDomain))
            Add("workspace", [Id(), .. Text(true, "provider"), new("binding", "TEXT", !Has(SourceMigrationCatalog.Current)), .. Integer(true, "created_at", "last_used_at")]);
        if (!Has(SourceMigrationCatalog.WorkspaceDomain))
            Add("workspace", [Id(), .. Text(true, "type", "project_id"), new("name", "TEXT", true, Default: "''"),
                .. Text(false, "branch", "directory", "extra"), new("time_used", "INTEGER", true, Default: "0")], [new("project_id", "project", "id")]);
        if (Has("20260812181746_session_inbox")) Add("session_inbox", [Id(), .. Text(true, "session_id", "type", "payload", "delivery"), .. Integer(true, "enqueued_seq", "time_created")],
            [new("session_id", "session_v2", "id")], [new("session_inbox_session_delivery_seq_idx", false, ["session_id", "delivery", "enqueued_seq"]),
                new("session_inbox_session_enqueued_seq_idx", true, ["session_id", "enqueued_seq"])]);
        if (Has("20260812213948_worktree")) Add("worktree", [new("project_id", "TEXT", true, 1), new("directory", "TEXT", true, 2), .. Text(false, "strategy"), .. Integer(true, "time_created")], [new("project_id", "project", "id")]);
        return tables;
        void Add(string name, Column[] columns, ForeignKey[]? keys = null, Index[]? indexes = null) => tables.Add(name, new(columns, keys ?? [], indexes ?? []));
    }

    private static Column Id() => new("id", "TEXT", Primary: 1);
    private static IEnumerable<Column> Text(bool required, params string[] names) => names.Select(name => new Column(name, "TEXT", required));
    private static IEnumerable<Column> Integer(bool required, params string[] names) => names.Select(name => new Column(name, "INTEGER", required));
    private static MigrationRejectedException Rejected(string message) => new("unrecognized_schema", message + " No prototype repair or legacy import was attempted.");

    private static string CanonicalSql(string input)
    {
        var result = new StringBuilder();
        var literal = false;
        foreach (var character in input.Trim().TrimEnd(';'))
        {
            if (character == '\'') { literal = !literal; result.Append(character); continue; }
            if (literal) { result.Append(character); continue; }
            if (char.IsWhiteSpace(character) || character is '"' or '`' or '[' or ']') continue;
            result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    [GeneratedRegex(@"\b(?:CHECK|COLLATE|GENERATED|AUTOINCREMENT|WITHOUT|STRICT|VIRTUAL|DEFERRABLE)\b|--|/\*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ExtraTableFeatures();
}
