namespace OpenCode.Core.Database.Migrations;

internal static partial class SourceSchema
{
    // Initial CREATEs plus the reviewed February/March transitions. Adapt schema
    // metadata only; executable SQL is retained verbatim in the source catalog.
    private static void AddInitialLineage(Dictionary<string, Table> tables, string baseline)
    {
        bool Has(string id) => string.CompareOrdinal(baseline, id) >= 0;
        tables.Remove("event");
        tables.Remove("event_sequence");
        if (!Has(SourceMigrationCatalog.ProjectCommands))
            tables["project"] = tables["project"] with
            {
                Columns = tables["project"].Columns.Where(column => column.Name != "commands").ToArray()
            };
        if (!Has(SourceMigrationCatalog.ControlAccount)) tables.Remove("control_account");
        if (!Has(SourceMigrationCatalog.Accounts))
        {
            tables.Remove("account");
            tables.Remove("account_state");
        }
        if (Has(SourceMigrationCatalog.Accounts) && !Has(SourceMigrationCatalog.OrganizationState))
        {
            tables["account"] = tables["account"] with { Columns = [.. tables["account"].Columns, new("selected_org_id", "TEXT")] };
            tables["account_state"] = tables["account_state"] with
            {
                Columns = tables["account_state"].Columns.Where(column => column.Name != "active_org_id").ToArray()
            };
        }
        if (!Has(SourceMigrationCatalog.LegacyWorkspace)) tables.Remove("workspace");
        if (Has(SourceMigrationCatalog.LegacyWorkspace) && !Has(SourceMigrationCatalog.WorkspaceFields))
            tables["workspace"] = new([Id(), .. Text(false, "branch"), .. Text(true, "project_id", "config")],
                [new("project_id", "project", "id")], []);
        if (!Has(SourceMigrationCatalog.SessionWorkspace))
            tables["session"] = tables["session"] with
            {
                Columns = tables["session"].Columns.Where(column => column.Name != "workspace_id").ToArray(),
                Indexes = tables["session"].Indexes.Where(index => index.Name != "session_workspace_idx").ToArray()
            };
        if (!Has(SourceMigrationCatalog.MessageCursor))
        {
            tables["message"] = tables["message"] with { Indexes = [new("message_session_idx", false, ["session_id"])] };
            tables["part"] = tables["part"] with
            {
                Indexes = [new("part_message_idx", false, ["message_id"]), new("part_session_idx", false, ["session_id"])]
            };
        }
    }
}
