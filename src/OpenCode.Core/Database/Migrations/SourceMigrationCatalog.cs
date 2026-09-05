namespace OpenCode.Core.Database.Migrations;

/// <summary>Source migration identity is independent of route/implementation presence.
/// Unsupported entries have no executable statements and can never be journal-stamped by this runner.</summary>
public sealed record SourceMigration(string Id, bool ForeignKeys, IReadOnlyList<string>? Statements, string? UnsupportedReason,
    bool RequiresPreSplitV2 = false, IReadOnlyList<string>? RequiresEmptyTables = null)
{
    public string SourcePath => "packages/core/src/database/migration/" + Id + ".ts";
    public bool Implemented => Statements is not null;
}

public static class SourceMigrationCatalog
{
    public const string Initial = "20260127222353_familiar_lady_ursula";
    public const string ProjectCommands = "20260211171708_add_project_commands";
    public const string ControlAccount = "20260213144116_wakeful_the_professor";
    public const string LegacyWorkspace = "20260225215848_workspace";
    public const string SessionWorkspace = "20260227213759_add_session_workspace_id";
    public const string Accounts = "20260228203230_blue_harpoon";
    public const string WorkspaceFields = "20260303231226_add_workspace_fields";
    public const string OrganizationState = "20260309230000_move_org_to_state";
    public const string MessageCursor = "20260312043431_session_message_cursor";
    public const string Events = "20260323234822_events";
    public const string WorkspaceName = "20260410174513_workspace-name";
    public const string SessionEntry = "20260413175956_chief_energizer";
    public const string IconOverride = "20260423070820_add_icon_url_override";
    public const string SessionProjection = "20260427172553_slow_nightmare";
    public const string SessionPath = "20260428004200_add_session_path";
    public const string SessionAgentModel = "20260501142318_next_venus";
    public const string SyncOwner = "20260504145000_add_sync_owner";
    public const string WorkspaceTime = "20260507164347_add_workspace_time";
    public const string SessionUsage = "20260510033149_session_usage";
    public const string DataMigrationState = "20260511000411_data_migration_state";
    public const string SessionMetadata = "20260511173437_session-metadata";
    public const string NormalizePaths = "20260601010001_normalize_storage_paths";
    public const string PermissionDrop = "20260601202201_amazing_prowler";
    public const string PermissionRows = "20260602002951_lowly_union_jack";
    public const string ProjectDirectories = "20260602182828_add_project_directories";
    public const string ProjectionIndexes = "20260603001617_session_message_projection_indexes";
    public const string ProjectionOrder = "20260603040000_session_message_projection_order";
    public const string InputInbox = "20260603141458_session_input_inbox";
    public const string InputIndexes = "20260603160727_jittery_ezekiel_stane";
    public const string EventSourcedInput = "20260604172448_event_sourced_session_input";
    public const string ContextSnapshot = "20260605003541_add_session_context_snapshot";
    public const string ContextAgent = "20260605042240_add_context_epoch_agent";
    public const string CredentialTable = "20260611035744_credential";
    public const string CredentialReplacement = "20260611192811_lush_chimera";
    public const string ProjectDirectoryStrategy = "20260612174303_project_dir_strategy";
    public const string ContextSimplification = "20260622142730_simplify_session_context_epoch";
    public const string SplitV2 = "20260804233008_loose_psylocke";
    public const string CredentialImport = "20260805200742_import_legacy_credentials";
    public const string WorkspaceDomain = "20260808023530_workspace_domain";
    public const string EarliestRecognized = SplitV2;
    public const string Current = "20260823191254_nullable_workspace_binding";
    public const string PreSplitV2Marker = "20260730195856_optional_session_title";

    // Exact order from packages/core/src/database/migration.gen.ts, not a version count.
    public static IReadOnlyList<SourceMigration> All { get; } = Array.AsReadOnly(new[]
    {
        Blocked(Initial, "Existing-database migration requires a completed initial source schema. Empty databases use current bootstrap, not an invented old journal."),
        Sql(ProjectCommands, true, "ALTER TABLE `project` ADD `commands` text;"),
        Sql(ControlAccount, true,
            """
            CREATE TABLE `control_account` (
              `email` text NOT NULL,
              `url` text NOT NULL,
              `access_token` text NOT NULL,
              `refresh_token` text NOT NULL,
              `token_expiry` integer,
              `active` integer NOT NULL,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL,
              CONSTRAINT `control_account_pk` PRIMARY KEY(`email`, `url`)
            );
            """),
        Sql(LegacyWorkspace, true,
            """
            CREATE TABLE `workspace` (
              `id` text PRIMARY KEY,
              `branch` text,
              `project_id` text NOT NULL,
              `config` text NOT NULL,
              CONSTRAINT `fk_workspace_project_id_project_id_fk` FOREIGN KEY (`project_id`) REFERENCES `project`(`id`) ON DELETE CASCADE
            );
            """),
        Sql(SessionWorkspace, true,
            "ALTER TABLE `session` ADD `workspace_id` text;",
            "CREATE INDEX `session_workspace_idx` ON `session` (`workspace_id`);"),
        Sql(Accounts, true,
            """
            CREATE TABLE `account` (
              `id` text PRIMARY KEY,
              `email` text NOT NULL,
              `url` text NOT NULL,
              `access_token` text NOT NULL,
              `refresh_token` text NOT NULL,
              `token_expiry` integer,
              `selected_org_id` text,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL
            );
            """,
            """
            CREATE TABLE `account_state` (
              `id` integer PRIMARY KEY NOT NULL,
              `active_account_id` text,
              FOREIGN KEY (`active_account_id`) REFERENCES `account`(`id`) ON UPDATE no action ON DELETE set null
            );
            """),
        new SourceMigration(WorkspaceFields, true, Array.AsReadOnly(new[]
        {
            "ALTER TABLE `workspace` ADD `type` text NOT NULL;",
            "ALTER TABLE `workspace` ADD `name` text;",
            "ALTER TABLE `workspace` ADD `directory` text;",
            "ALTER TABLE `workspace` ADD `extra` text;",
            "ALTER TABLE `workspace` DROP COLUMN `config`;",
        }), null, RequiresEmptyTables: Array.AsReadOnly(new[] { "workspace" })),
        Sql(OrganizationState, true,
            "ALTER TABLE `account_state` ADD `active_org_id` text;",
            "UPDATE `account_state` SET `active_org_id` = (SELECT `selected_org_id` FROM `account` WHERE `account`.`id` = `account_state`.`active_account_id`);",
            "ALTER TABLE `account` DROP COLUMN `selected_org_id`;"),
        Sql(MessageCursor, true,
            "DROP INDEX IF EXISTS `message_session_idx`;",
            "DROP INDEX IF EXISTS `part_message_idx`;",
            "CREATE INDEX `message_session_time_created_id_idx` ON `message` (`session_id`,`time_created`,`id`);",
            "CREATE INDEX `part_message_id_id_idx` ON `part` (`message_id`,`id`);"),
        Sql(Events, true,
            """
            CREATE TABLE `event_sequence` (
              `aggregate_id` text PRIMARY KEY,
              `seq` integer NOT NULL
            );
            """,
            """
            CREATE TABLE `event` (
              `id` text PRIMARY KEY,
              `aggregate_id` text NOT NULL,
              `seq` integer NOT NULL,
              `type` text NOT NULL,
              `data` text NOT NULL,
              CONSTRAINT `fk_event_aggregate_id_event_sequence_aggregate_id_fk` FOREIGN KEY (`aggregate_id`) REFERENCES `event_sequence`(`aggregate_id`) ON DELETE CASCADE
            );
            """),
        // The recognized predecessor has the source's nullable name column.
        // Copy it verbatim; do not silently coalesce NULL to the new default.
        Sql(WorkspaceName, true,
            "PRAGMA foreign_keys=OFF;",
            """
            CREATE TABLE `__new_workspace` (
              `id` text PRIMARY KEY,
              `type` text NOT NULL,
              `name` text DEFAULT '' NOT NULL,
              `branch` text,
              `directory` text,
              `extra` text,
              `project_id` text NOT NULL,
              CONSTRAINT `fk_workspace_project_id_project_id_fk` FOREIGN KEY (`project_id`) REFERENCES `project`(`id`) ON DELETE CASCADE
            );
            """,
            "INSERT INTO `__new_workspace`(`id`, `type`, `branch`, `name`, `directory`, `extra`, `project_id`) SELECT `id`, `type`, `branch`, `name`, `directory`, `extra`, `project_id` FROM `workspace`;",
            "DROP TABLE `workspace`;",
            "ALTER TABLE `__new_workspace` RENAME TO `workspace`;",
            "PRAGMA foreign_keys=ON;"),
        Sql(SessionEntry, true,
            """
            CREATE TABLE `session_entry` (
              `id` text PRIMARY KEY,
              `session_id` text NOT NULL,
              `type` text NOT NULL,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL,
              `data` text NOT NULL,
              CONSTRAINT `fk_session_entry_session_id_session_id_fk` FOREIGN KEY (`session_id`) REFERENCES `session`(`id`) ON DELETE CASCADE
            );
            """,
            "CREATE INDEX `session_entry_session_idx` ON `session_entry` (`session_id`);",
            "CREATE INDEX `session_entry_session_type_idx` ON `session_entry` (`session_id`,`type`);",
            "CREATE INDEX `session_entry_time_created_idx` ON `session_entry` (`time_created`);"),
        Sql(IconOverride, true,
            """
            ALTER TABLE `project` ADD `icon_url_override` text;
            UPDATE `project` SET `icon_url_override` = `icon_url` WHERE `icon_url` IS NOT NULL;
            """),
        new SourceMigration(SessionProjection, true, Array.AsReadOnly(new[]
        {
            """
            CREATE TABLE `session_message` (
              `id` text PRIMARY KEY,
              `session_id` text NOT NULL,
              `type` text NOT NULL,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL,
              `data` text NOT NULL,
              CONSTRAINT `fk_session_message_session_id_session_id_fk` FOREIGN KEY (`session_id`) REFERENCES `session`(`id`) ON DELETE CASCADE
            );
            """,
            "DROP INDEX IF EXISTS `session_entry_session_idx`;",
            "DROP INDEX IF EXISTS `session_entry_session_type_idx`;",
            "DROP INDEX IF EXISTS `session_entry_time_created_idx`;",
            "CREATE INDEX `session_message_session_idx` ON `session_message` (`session_id`);",
            "CREATE INDEX `session_message_session_type_idx` ON `session_message` (`session_id`,`type`);",
            "CREATE INDEX `session_message_time_created_idx` ON `session_message` (`time_created`);",
            "DROP TABLE `session_entry`;",
        }), null, RequiresEmptyTables: Array.AsReadOnly(new[] { "session_entry" })),
        Sql(SessionPath, true, "ALTER TABLE `session` ADD `path` text;"),
        Sql(SessionAgentModel, true,
            "ALTER TABLE `session` ADD `agent` text;",
            "ALTER TABLE `session` ADD `model` text;"),
        Sql(SyncOwner, true, "ALTER TABLE `event_sequence` ADD `owner_id` text;"),
        Sql(WorkspaceTime, true, "ALTER TABLE `workspace` ADD `time_used` integer NOT NULL DEFAULT 0;"),
        Sql(SessionUsage, true,
            "ALTER TABLE `session` ADD `cost` real DEFAULT 0 NOT NULL;",
            "ALTER TABLE `session` ADD `tokens_input` integer DEFAULT 0 NOT NULL;",
            "ALTER TABLE `session` ADD `tokens_output` integer DEFAULT 0 NOT NULL;",
            "ALTER TABLE `session` ADD `tokens_reasoning` integer DEFAULT 0 NOT NULL;",
            "ALTER TABLE `session` ADD `tokens_cache_read` integer DEFAULT 0 NOT NULL;",
            "ALTER TABLE `session` ADD `tokens_cache_write` integer DEFAULT 0 NOT NULL;",
            """
            UPDATE session
            SET
              cost = coalesce((
                SELECT sum(coalesce(json_extract(message.data, '$.cost'), 0))
                FROM message
                WHERE message.session_id = session.id
                  AND json_extract(message.data, '$.role') = 'assistant'
              ), 0),
              tokens_input = coalesce((
                SELECT sum(coalesce(json_extract(message.data, '$.tokens.input'), 0))
                FROM message
                WHERE message.session_id = session.id
                  AND json_extract(message.data, '$.role') = 'assistant'
              ), 0),
              tokens_output = coalesce((
                SELECT sum(coalesce(json_extract(message.data, '$.tokens.output'), 0))
                FROM message
                WHERE message.session_id = session.id
                  AND json_extract(message.data, '$.role') = 'assistant'
              ), 0),
              tokens_reasoning = coalesce((
                SELECT sum(coalesce(json_extract(message.data, '$.tokens.reasoning'), 0))
                FROM message
                WHERE message.session_id = session.id
                  AND json_extract(message.data, '$.role') = 'assistant'
              ), 0),
              tokens_cache_read = coalesce((
                SELECT sum(coalesce(json_extract(message.data, '$.tokens.cache.read'), 0))
                FROM message
                WHERE message.session_id = session.id
                  AND json_extract(message.data, '$.role') = 'assistant'
              ), 0),
              tokens_cache_write = coalesce((
                SELECT sum(coalesce(json_extract(message.data, '$.tokens.cache.write'), 0))
                FROM message
                WHERE message.session_id = session.id
                  AND json_extract(message.data, '$.role') = 'assistant'
              ), 0)
            """),
        Sql(DataMigrationState, true,
            """
            CREATE TABLE `data_migration` (
              `name` text PRIMARY KEY,
              `time_completed` integer NOT NULL
            );
            """),
        // The recognized predecessor has no metadata column. The alternate
        // lovely_romulus lineage is not adopted by stamping an empty operation.
        Sql(SessionMetadata, true, "ALTER TABLE `session` ADD `metadata` text;"),
        Sql(NormalizePaths, true,
            "UPDATE project SET worktree = REPLACE(worktree, char(92), '/') WHERE worktree GLOB '[A-Za-z]:' || char(92) || '*' OR worktree LIKE char(92) || char(92) || '%';",
            "UPDATE project SET sandboxes = REPLACE(sandboxes, char(92) || char(92), '/') WHERE instr(sandboxes, char(92)) > 0 AND (worktree GLOB '[A-Za-z]:*' OR worktree LIKE '//%');",
            "UPDATE session SET directory = REPLACE(directory, char(92), '/') WHERE directory GLOB '[A-Za-z]:' || char(92) || '*' OR directory LIKE char(92) || char(92) || '%';",
            "UPDATE session SET path = REPLACE(path, char(92), '/') WHERE path IS NOT NULL AND instr(path, char(92)) > 0 AND (directory GLOB '[A-Za-z]:*' OR directory LIKE '//%');"),
        new SourceMigration(PermissionDrop, true, Array.AsReadOnly(new[] { "DROP TABLE `permission`;" }), null,
            RequiresEmptyTables: Array.AsReadOnly(new[] { "permission" })),
        Sql(PermissionRows, true,
            """
            CREATE TABLE `permission` (
              `id` text PRIMARY KEY,
              `project_id` text NOT NULL,
              `action` text NOT NULL,
              `resource` text NOT NULL,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL,
              CONSTRAINT `fk_permission_project_id_project_id_fk` FOREIGN KEY (`project_id`) REFERENCES `project`(`id`) ON DELETE CASCADE
            );
            """,
            "CREATE UNIQUE INDEX `permission_project_action_resource_idx` ON `permission` (`project_id`,`action`,`resource`);"),
        Sql(ProjectDirectories, true,
            """
            CREATE TABLE `project_directory` (
              `project_id` text NOT NULL,
              `directory` text NOT NULL,
              `type` text NOT NULL,
              `time_created` integer NOT NULL,
              CONSTRAINT `project_directory_pk` PRIMARY KEY(`project_id`, `directory`),
              CONSTRAINT `fk_project_directory_project_id_project_id_fk` FOREIGN KEY (`project_id`) REFERENCES `project`(`id`) ON DELETE CASCADE
            );
            """),
        Sql(ProjectionIndexes, true,
            "DROP INDEX IF EXISTS `session_message_session_idx`;",
            "DROP INDEX IF EXISTS `session_message_session_type_idx`;",
            "CREATE INDEX `event_aggregate_seq_idx` ON `event` (`aggregate_id`,`seq`);",
            "CREATE INDEX `session_message_session_time_created_id_idx` ON `session_message` (`session_id`,`time_created`,`id`);",
            "CREATE INDEX `session_message_session_type_time_created_id_idx` ON `session_message` (`session_id`,`type`,`time_created`,`id`);"),
        new SourceMigration(ProjectionOrder, true, Array.AsReadOnly(new[]
        {
            "DELETE FROM `session_message`;",
            "ALTER TABLE `session_message` ADD COLUMN `seq` integer NOT NULL;",
            "DROP INDEX IF EXISTS `session_message_session_type_time_created_id_idx`;",
            "CREATE INDEX `session_message_session_seq_idx` ON `session_message` (`session_id`,`seq`);",
            "CREATE INDEX `session_message_session_type_seq_idx` ON `session_message` (`session_id`,`type`,`seq`);",
        }), null, RequiresEmptyTables: Array.AsReadOnly(new[] { "session_message" })),
        Sql(InputInbox, true,
            """
            CREATE TABLE `session_input` (
              `seq` integer PRIMARY KEY AUTOINCREMENT,
              `id` text NOT NULL UNIQUE,
              `session_id` text NOT NULL,
              `prompt` text NOT NULL,
              `delivery` text NOT NULL,
              `promoted_seq` integer,
              `time_created` integer NOT NULL,
              CONSTRAINT `fk_session_input_session_id_session_id_fk` FOREIGN KEY (`session_id`) REFERENCES `session`(`id`) ON DELETE CASCADE
            );
            """,
            "CREATE INDEX `session_input_session_pending_seq_idx` ON `session_input` (`session_id`,`promoted_seq`,`seq`);"),
        Sql(InputIndexes, true,
            "DROP INDEX IF EXISTS `session_input_session_pending_seq_idx`;",
            "CREATE INDEX IF NOT EXISTS `event_aggregate_type_seq_idx` ON `event` (`aggregate_id`,`type`,`seq`);",
            "CREATE INDEX IF NOT EXISTS `session_input_session_pending_delivery_seq_idx` ON `session_input` (`session_id`,`promoted_seq`,`delivery`,`seq`);",
            "CREATE INDEX IF NOT EXISTS `session_message_session_time_created_id_idx` ON `session_message` (`session_id`,`time_created`,`id`);"),
        Blocked(EventSourcedInput, "Source deletes input/message/event/workspace rows and clears session workspace IDs. No row-preserving transform is provided."),
        Sql(ContextSnapshot, true,
            """
            CREATE TABLE `session_context_epoch` (
              `session_id` text PRIMARY KEY,
              `baseline` text NOT NULL,
              `snapshot` text NOT NULL,
              `baseline_seq` integer NOT NULL,
              `replacement_seq` integer,
              `revision` integer DEFAULT 0 NOT NULL,
              CONSTRAINT `fk_session_context_epoch_session_id_session_id_fk` FOREIGN KEY (`session_id`) REFERENCES `session`(`id`) ON DELETE CASCADE
            );
            """),
        Sql(ContextAgent, true, "ALTER TABLE `session_context_epoch` ADD `agent` text DEFAULT 'build' NOT NULL;"),
        Sql(CredentialTable, true,
            """
            CREATE TABLE `credential` (
              `id` text PRIMARY KEY,
              `connector_id` text NOT NULL,
              `method_id` text NOT NULL,
              `label` text NOT NULL,
              `value` text NOT NULL,
              `active` integer DEFAULT false NOT NULL,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL
            );
            """,
            "CREATE UNIQUE INDEX `credential_connector_active_idx` ON `credential` (`connector_id`) WHERE \"credential\".\"active\" = 1;"),
        new SourceMigration(CredentialReplacement, true, Array.AsReadOnly(new[]
        {
            "DROP INDEX IF EXISTS `credential_connector_active_idx`;",
            "DROP TABLE `credential`;",
            """
            CREATE TABLE `credential` (
              `id` text PRIMARY KEY,
              `integration_id` text,
              `label` text NOT NULL,
              `value` text NOT NULL,
              `connector_id` text,
              `method_id` text,
              `active` integer,
              `time_created` integer NOT NULL,
              `time_updated` integer NOT NULL
            );
            """,
        }), null, RequiresEmptyTables: Array.AsReadOnly(new[] { "credential" })),
        Sql(ProjectDirectoryStrategy, true,
            "ALTER TABLE `project_directory` ADD `strategy` text;",
            "PRAGMA foreign_keys=OFF;",
            """
            CREATE TABLE `__new_project_directory` (
              `project_id` text NOT NULL,
              `directory` text NOT NULL,
              `type` text,
              `strategy` text,
              `time_created` integer NOT NULL,
              CONSTRAINT `project_directory_pk` PRIMARY KEY(`project_id`, `directory`),
              CONSTRAINT `fk_project_directory_project_id_project_id_fk` FOREIGN KEY (`project_id`) REFERENCES `project`(`id`) ON DELETE CASCADE
            );
            """,
            "INSERT INTO `__new_project_directory`(`project_id`, `directory`, `type`, `time_created`) SELECT `project_id`, `directory`, `type`, `time_created` FROM `project_directory`;",
            "DROP TABLE `project_directory`;",
            "ALTER TABLE `__new_project_directory` RENAME TO `project_directory`;",
            "PRAGMA foreign_keys=ON;"),
        new SourceMigration(ContextSimplification, true, Array.AsReadOnly(new[]
        {
            "ALTER TABLE `session_context_epoch` DROP COLUMN `agent`;",
            "ALTER TABLE `session_context_epoch` DROP COLUMN `replacement_seq`;",
            "ALTER TABLE `session_context_epoch` DROP COLUMN `revision`;",
        }), null, RequiresEmptyTables: Array.AsReadOnly(new[] { "session_context_epoch" })),
        Blocked("20260622170816_reset_v2_session_state", "Source pre-launch reset deletes session projections and events; never replayed by this runner."),
        Blocked("20260622202450_simplify_session_input", "Source pre-launch reset deletes projections/events/workspaces; never replayed by this runner."),
        new SourceMigration(SplitV2, true, Array.AsReadOnly(new[]
        {
            "DROP INDEX IF EXISTS `session_project_idx`;",
            "DROP INDEX IF EXISTS `session_workspace_idx`;",
            "DROP INDEX IF EXISTS `session_parent_idx`;",
            "DROP INDEX IF EXISTS `session_time_suspended_idx`;",
            "ALTER TABLE `session` RENAME TO `session_v2`;",
            "CREATE INDEX `session_v2_project_idx` ON `session_v2` (`project_id`);",
            "CREATE INDEX `session_v2_workspace_idx` ON `session_v2` (`workspace_id`);",
            "CREATE INDEX `session_v2_parent_idx` ON `session_v2` (`parent_id`);",
            "CREATE INDEX `session_v2_time_suspended_idx` ON `session_v2` (`time_suspended`) WHERE \"session_v2\".\"time_suspended\" is not null;",
            "DROP TABLE IF EXISTS `data_migration`;",
            "DROP TABLE IF EXISTS `session_context_epoch`;",
            "DROP TABLE IF EXISTS `session_input`;",
        }), null, RequiresPreSplitV2: true,
            RequiresEmptyTables: Array.AsReadOnly(new[] { "data_migration", "session_context_epoch", "session_input" })),
        Blocked(CredentialImport, "Legacy auth.json/credential import is disabled. Stop explicitly at SplitV2; this ID is never stamped or skipped."),
        new SourceMigration(WorkspaceDomain, true, Array.AsReadOnly(new[]
        {
            "DROP TABLE `workspace`;",
            """
            CREATE TABLE `workspace` (
              `id` text PRIMARY KEY,
              `provider` text NOT NULL,
              `binding` text NOT NULL,
              `created_at` integer NOT NULL,
              `last_used_at` integer NOT NULL
            );
            """,
        }), null, RequiresEmptyTables: Array.AsReadOnly(new[] { "workspace" })),
        Sql("20260811161259_execution_claim_attempts", true,
            "ALTER TABLE `session_v2` ADD `resume_attempts` integer DEFAULT 0 NOT NULL;"),
        Sql("20260812181746_session_inbox", true,
            """
            CREATE TABLE `session_inbox` (
              `id` text PRIMARY KEY,
              `session_id` text NOT NULL,
              `type` text NOT NULL,
              `payload` text NOT NULL,
              `delivery` text NOT NULL,
              `enqueued_seq` integer NOT NULL,
              `time_created` integer NOT NULL,
              CONSTRAINT `fk_session_inbox_session_id_session_v2_id_fk` FOREIGN KEY (`session_id`) REFERENCES `session_v2`(`id`) ON DELETE CASCADE
            );
            """,
            "CREATE INDEX `session_inbox_session_delivery_seq_idx` ON `session_inbox` (`session_id`,`delivery`,`enqueued_seq`);",
            "CREATE UNIQUE INDEX `session_inbox_session_enqueued_seq_idx` ON `session_inbox` (`session_id`,`enqueued_seq`);"),
        Sql("20260812213948_worktree", true,
            """
            CREATE TABLE `worktree` (
              `project_id` text NOT NULL,
              `directory` text NOT NULL,
              `strategy` text,
              `time_created` integer NOT NULL,
              CONSTRAINT `worktree_pk` PRIMARY KEY(`project_id`, `directory`),
              CONSTRAINT `fk_worktree_project_id_project_id_fk` FOREIGN KEY (`project_id`) REFERENCES `project`(`id`) ON DELETE CASCADE
            );
            """,
            """
            INSERT INTO `worktree` (`project_id`, `directory`, `strategy`, `time_created`)
            SELECT
              `project_id`,
              `directory`,
              CASE
                WHEN `strategy` = 'git_worktree' THEN 'git'
                WHEN `strategy` IS NOT NULL THEN `strategy`
                WHEN `type` = 'git_worktree' THEN 'git'
              END,
              `time_created`
            FROM `project_directory`;
            """),
        Sql("20260819222447_session_viewed_state", true,
            "ALTER TABLE `session_v2` ADD `time_idle` integer;",
            "ALTER TABLE `session_v2` ADD `time_viewed` integer;",
            "ALTER TABLE `session_v2` ADD `idle_outcome` text;"),
        Sql(Current, false,
            """
            CREATE TABLE `__new_workspace` (
              `id` text PRIMARY KEY,
              `provider` text NOT NULL,
              `binding` text,
              `created_at` integer NOT NULL,
              `last_used_at` integer NOT NULL
            );
            """,
            "INSERT INTO `__new_workspace`(`id`, `provider`, `binding`, `created_at`, `last_used_at`) SELECT `id`, `provider`, `binding`, `created_at`, `last_used_at` FROM `workspace`;",
            "DROP TABLE `workspace`;",
            "ALTER TABLE `__new_workspace` RENAME TO `workspace`;"),
    });

    private static SourceMigration Blocked(string id, string reason = "This source schema/data transition is not implemented in a reviewed executable profile group.") =>
        new(id, true, null, reason);
    private static SourceMigration Sql(string id, bool foreignKeys, params string[] statements) => new(id, foreignKeys, Array.AsReadOnly(statements), null);
}
