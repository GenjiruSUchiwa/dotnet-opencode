namespace OpenCode.Core.Database.Migrations;

// The split migration's marker branch renames canonical V2 storage in place.
// Derive its inverse metadata explicitly; never rewrite SQL strings or invent IDs.
internal static partial class SourceSchema
{
    private static void AddPreviousV2(Dictionary<string, Table> tables, string baseline, IReadOnlyList<ObjectInfo> objects)
    {
        var beforeSplit = baseline == SourceMigrationCatalog.PreSplitV2Marker;
        var session = beforeSplit ? "session" : "session_v2";
        if (beforeSplit)
        {
            var definition = tables["session_v2"];
            tables.Remove("session_v2");
            tables.Add("session", definition with
            {
                Indexes =
                [
                    new("session_project_idx", false, ["project_id"]),
                    new("session_workspace_idx", false, ["workspace_id"]),
                    new("session_parent_idx", false, ["parent_id"]),
                    new("session_time_suspended_idx", false, ["time_suspended"], "\"session\".\"time_suspended\" is not null"),
                ]
            });
            foreach (var name in new[] { "instruction_entry", "instruction_state", "session_message", "session_pending" })
                tables[name] = tables[name] with { ForeignKeys = [new("session_id", "session", "id")] };
        }

        // The marker branch queries message and does not delete the legacy tables.
        // Columns come from familiar_lady_ursula; indexes come from the later
        // session_message_cursor transition. Rows remain untouched.
        tables.Add("message", new([Id(), .. Text(true, "session_id", "data"), .. Integer(true, "time_created", "time_updated")],
            [new("session_id", session, "id")], [new("message_session_time_created_id_idx", false, ["session_id", "time_created", "id"])]));
        if (Present("part")) tables.Add("part", new([Id(), .. Text(true, "message_id", "session_id", "data"), .. Integer(true, "time_created", "time_updated")],
            [new("message_id", "message", "id")], [new("part_message_id_id_idx", false, ["message_id", "id"]), new("part_session_idx", false, ["session_id"])]));
        if (Present("todo")) tables.Add("todo", new([new("session_id", "TEXT", true, 1), new("position", "INTEGER", true, 2),
            .. Text(true, "content", "status", "priority"), .. Integer(true, "time_created", "time_updated")],
            [new("session_id", session, "id")], [new("todo_session_idx", false, ["session_id"])]));
        if (Present("session_share")) tables.Add("session_share", new([new("session_id", "TEXT", Primary: 1), .. Text(true, "id", "secret", "url"),
            .. Integer(true, "time_created", "time_updated")], [new("session_id", session, "id")], []));
        if (!beforeSplit) return;

        // DROP IF EXISTS permits absent retired tables. When present, require the
        // exact known metadata and (separately, before any DDL) no remaining rows.
        if (Present("data_migration")) tables.Add("data_migration", new([new("name", "TEXT", Primary: 1), .. Integer(true, "time_completed")], [], []));
        if (Present("session_context_epoch")) tables.Add("session_context_epoch", new([new("session_id", "TEXT", Primary: 1),
            .. Text(true, "baseline", "snapshot"), .. Integer(true, "baseline_seq")], [new("session_id", "session", "id")], []));
        if (Present("session_input")) tables.Add("session_input", new([Id(), .. Text(true, "session_id", "prompt", "delivery"),
            .. Integer(true, "admitted_seq", "time_created"), .. Integer(false, "promoted_seq")], [new("session_id", "session", "id")],
            [new("session_input_session_pending_delivery_seq_idx", false, ["session_id", "promoted_seq", "delivery", "admitted_seq"]),
                new("session_input_session_admitted_seq_idx", true, ["session_id", "admitted_seq"]),
                new("session_input_session_promoted_seq_idx", true, ["session_id", "promoted_seq"])]));
        bool Present(string name) => objects.Any(item => item.Type == "table" && item.Name == name);
    }
}
