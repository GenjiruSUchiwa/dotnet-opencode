namespace OpenCode.Core.Database.Migrations;

// These profiles describe the actual incremental spring/June lineage, not the later
// July marker branch. No schemas are inferred from table counts or row contents.
internal static partial class SourceSchema
{
    internal static bool IsEarlyV2(string id) => id is SourceMigrationCatalog.Initial or SourceMigrationCatalog.ProjectCommands
        or SourceMigrationCatalog.ControlAccount or SourceMigrationCatalog.LegacyWorkspace or SourceMigrationCatalog.SessionWorkspace
        or SourceMigrationCatalog.Accounts or SourceMigrationCatalog.WorkspaceFields or SourceMigrationCatalog.OrganizationState
        or SourceMigrationCatalog.MessageCursor or SourceMigrationCatalog.Events or SourceMigrationCatalog.WorkspaceName
        or SourceMigrationCatalog.SessionEntry or SourceMigrationCatalog.IconOverride
        or SourceMigrationCatalog.SessionProjection or SourceMigrationCatalog.SessionPath
        or SourceMigrationCatalog.SessionAgentModel or SourceMigrationCatalog.SyncOwner
        or SourceMigrationCatalog.WorkspaceTime
        or SourceMigrationCatalog.SessionUsage or SourceMigrationCatalog.DataMigrationState
        or SourceMigrationCatalog.SessionMetadata or SourceMigrationCatalog.NormalizePaths or SourceMigrationCatalog.PermissionDrop
        or SourceMigrationCatalog.PermissionRows
        or SourceMigrationCatalog.ProjectDirectories or SourceMigrationCatalog.ProjectionIndexes
        or SourceMigrationCatalog.ProjectionOrder or SourceMigrationCatalog.InputInbox or SourceMigrationCatalog.InputIndexes
        or SourceMigrationCatalog.EventSourcedInput
        or SourceMigrationCatalog.ContextSnapshot or SourceMigrationCatalog.ContextAgent
        or SourceMigrationCatalog.CredentialTable or SourceMigrationCatalog.CredentialReplacement or SourceMigrationCatalog.ProjectDirectoryStrategy
        or SourceMigrationCatalog.ContextSimplification;

    private static void AddEarlyV2(Dictionary<string, Table> tables, string baseline, IReadOnlyList<ObjectInfo> objects)
    {
        bool Has(string id) => string.CompareOrdinal(baseline, id) >= 0;
        // Shared fields are copied from the explicit marker profile. Every field
        // introduced only in the later lineage is removed below using metadata,
        // not a textual rewrite of executable source SQL.
        AddPreviousV2(tables, SourceMigrationCatalog.PreSplitV2Marker, objects);
        foreach (var name in new[] { "part", "todo", "session_share" })
            if (!tables.ContainsKey(name)) throw Rejected($"The early V2 profile requires source table {name}.");
        if (Has(SourceMigrationCatalog.DataMigrationState) && !tables.ContainsKey("data_migration"))
            throw Rejected("The data-migration-state profile requires data_migration.");
        if (!Has(SourceMigrationCatalog.DataMigrationState)) tables.Remove("data_migration");
        if (Has(SourceMigrationCatalog.EventSourcedInput) && !tables.ContainsKey("session_input"))
            throw Rejected("The event-sourced input profile requires session_input.");
        foreach (var name in new[] { "kv", "instruction_blob", "instruction_entry", "instruction_state", "session_pending" })
            tables.Remove(name);
        if (string.CompareOrdinal(baseline, SourceMigrationCatalog.CredentialReplacement) < 0) tables.Remove("credential");
        if (baseline == SourceMigrationCatalog.CredentialTable)
            tables.Add("credential", new([Id(), .. Text(true, "connector_id", "method_id", "label", "value"),
                new("active", "INTEGER", true, Default: "false"), .. Integer(true, "time_created", "time_updated")], [],
                [new("credential_connector_active_idx", true, ["connector_id"], "\"credential\".\"active\" = 1")]));
        if (!Has(SourceMigrationCatalog.PermissionRows)) tables.Remove("permission");
        if (!Has(SourceMigrationCatalog.PermissionDrop))
            tables.Add("permission", new([new("project_id", "TEXT", Primary: 1),
                .. Integer(true, "time_created", "time_updated"), .. Text(true, "data")], [new("project_id", "project", "id")], []));
        tables["event"] = tables["event"] with
        {
            Columns = tables["event"].Columns.Where(column => column.Name != "created").ToArray(),
            Indexes = tables["event"].Indexes
                .Where(index => index.Name == "event_aggregate_seq_idx" ? Has(SourceMigrationCatalog.ProjectionIndexes) : Has(SourceMigrationCatalog.InputIndexes))
                .Select(index => index.Name == "event_aggregate_seq_idx" ? index with { Unique = Has(SourceMigrationCatalog.EventSourcedInput) } : index).ToArray()
        };
        if (!Has(SourceMigrationCatalog.SyncOwner))
            tables["event_sequence"] = tables["event_sequence"] with
            {
                Columns = tables["event_sequence"].Columns.Where(column => column.Name != "owner_id").ToArray()
            };
        if (!Has(SourceMigrationCatalog.WorkspaceTime))
            tables["workspace"] = tables["workspace"] with
            {
                Columns = tables["workspace"].Columns.Where(column => column.Name != "time_used").ToArray()
            };
        if (!Has(SourceMigrationCatalog.WorkspaceName))
            tables["workspace"] = tables["workspace"] with
            {
                Columns = tables["workspace"].Columns.Select(column => column.Name == "name"
                    ? column with { Required = false, Default = null } : column).ToArray()
            };
        if (!Has(SourceMigrationCatalog.IconOverride))
            tables["project"] = tables["project"] with
            {
                Columns = tables["project"].Columns.Where(column => column.Name != "icon_url_override").ToArray()
            };
        tables["session"] = tables["session"] with
        {
            Columns = tables["session"].Columns
                .Where(column => column.Name is not ("fork_session_id" or "fork_boundary" or "time_suspended"))
                .Where(column => Has(SourceMigrationCatalog.SessionUsage) || column.Name is not
                    ("cost" or "tokens_input" or "tokens_output" or "tokens_reasoning" or "tokens_cache_read" or "tokens_cache_write"))
                .Where(column => column.Name != "metadata" || Has(SourceMigrationCatalog.SessionMetadata))
                .Where(column => column.Name != "path" || Has(SourceMigrationCatalog.SessionPath))
                .Where(column => column.Name is not ("agent" or "model") || Has(SourceMigrationCatalog.SessionAgentModel))
                .Select(column => column.Name == "title" ? column with { Required = true } : column).ToArray(),
            Indexes =
            [
                new("session_project_idx", false, ["project_id"]),
                new("session_workspace_idx", false, ["workspace_id"]),
                new("session_parent_idx", false, ["parent_id"]),
            ]
        };
        if (string.CompareOrdinal(baseline, SourceMigrationCatalog.ProjectDirectoryStrategy) < 0)
            tables["project_directory"] = tables["project_directory"] with
            {
                Columns = tables["project_directory"].Columns.Where(column => column.Name != "strategy")
                    .Select(column => column.Name == "type" ? column with { Required = true } : column).ToArray()
            };

        if (!Has(SourceMigrationCatalog.ProjectDirectories)) tables.Remove("project_directory");
        var message = tables["session_message"];
        tables["session_message"] = message with
        {
            Columns = message.Columns.Where(column => column.Name != "seq" || Has(SourceMigrationCatalog.ProjectionOrder)).ToArray(),
            Indexes = Has(SourceMigrationCatalog.ProjectionOrder)
                ? message.Indexes.Select(index => index.Name == "session_message_session_seq_idx"
                    ? index with { Unique = Has(SourceMigrationCatalog.EventSourcedInput) } : index).ToArray()
                : Has(SourceMigrationCatalog.ProjectionIndexes)
                    ? [new("session_message_session_time_created_id_idx", false, ["session_id", "time_created", "id"]),
                        new("session_message_session_type_time_created_id_idx", false, ["session_id", "type", "time_created", "id"]),
                        new("session_message_time_created_idx", false, ["time_created"])]
                    : [new("session_message_session_idx", false, ["session_id"]),
                        new("session_message_session_type_idx", false, ["session_id", "type"]),
                        new("session_message_time_created_idx", false, ["time_created"])]
        };
        if (!Has(SourceMigrationCatalog.SessionProjection)) tables.Remove("session_message");
        if (Has(SourceMigrationCatalog.SessionEntry) && !Has(SourceMigrationCatalog.SessionProjection))
            tables.Add("session_entry", new(
                [Id(), .. Text(true, "session_id", "type", "data"), .. Integer(true, "time_created", "time_updated")],
                [new("session_id", "session", "id")],
                [new("session_entry_session_idx", false, ["session_id"]),
                    new("session_entry_session_type_idx", false, ["session_id", "type"]),
                    new("session_entry_time_created_idx", false, ["time_created"])]));
        if (!Has(SourceMigrationCatalog.InputInbox)) tables.Remove("session_input");
        if (Has(SourceMigrationCatalog.InputInbox) && !Has(SourceMigrationCatalog.EventSourcedInput))
            tables["session_input"] = new(
                [new("seq", "INTEGER", Primary: 1), .. Text(true, "id", "session_id", "prompt", "delivery"),
                    .. Integer(false, "promoted_seq"), .. Integer(true, "time_created")],
                [new("session_id", "session", "id")],
                Has(SourceMigrationCatalog.InputIndexes)
                    ? [new("session_input_session_pending_delivery_seq_idx", false, ["session_id", "promoted_seq", "delivery", "seq"])]
                    : [new("session_input_session_pending_seq_idx", false, ["session_id", "promoted_seq", "seq"])],
                UniqueKeys: [["id"]], ExactSql: SourceMigrationCatalog.All.Single(migration => migration.Id == SourceMigrationCatalog.InputInbox).Statements![0]);

        tables.Remove("session_context_epoch");
        if (!Has(SourceMigrationCatalog.Events)) AddInitialLineage(tables, baseline);
        if (!Has(SourceMigrationCatalog.ContextSnapshot)) return;
        List<Column> epoch =
        [
            new("session_id", "TEXT", Primary: 1), .. Text(true, "baseline", "snapshot"), .. Integer(true, "baseline_seq")
        ];
        if (baseline != SourceMigrationCatalog.ContextSimplification)
        {
            epoch.Add(new("replacement_seq", "INTEGER"));
            epoch.Add(new("revision", "INTEGER", true, Default: "0"));
            if (baseline != SourceMigrationCatalog.ContextSnapshot) epoch.Add(new("agent", "TEXT", true, Default: "'build'"));
        }
        tables.Add("session_context_epoch", new(epoch.ToArray(), [new("session_id", "session", "id")], []));
    }
}
