# Early V2 migration groups

These are bounded, source-derived migrations, not a way to start the current
application with an old schema. The startup hook still requests Current and
rejects an incomplete route before writing. It remains off without an explicit
MigrationTarget. No server compatibility claim or registration change is made.

The earlier May usage/path/permission group is described in [MAY-V2.md](./MAY-V2.md).

## Group 0: projection order and the first input inbox

| Exact last source identity | Schema state | Supported next transition |
| --- | --- | --- |
| `20260602002951_lowly_union_jack` | Row permissions already created; no project_directory; projection has no seq | `20260602182828_add_project_directories` |
| `20260602182828_add_project_directories` | Directory table exists; original projection indexes | `20260603001617_session_message_projection_indexes` |
| `20260603001617_session_message_projection_indexes` | Timestamp-based projection indexes; nonunique event aggregate/sequence index | `20260603040000_session_message_projection_order`, only with an empty projection table |
| `20260603040000_session_message_projection_order` | Projection seq exists with nonunique session/seq index; no inbox | `20260603141458_session_input_inbox` |
| `20260603141458_session_input_inbox` | Inbox seq INTEGER PRIMARY KEY AUTOINCREMENT; id TEXT NOT NULL UNIQUE | `20260603160727_jittery_ezekiel_stane` |
| `20260603160727_jittery_ezekiel_stane` | Event/type index and input pending/delivery index exist | Stop; the event-sourced-input reset remains blocked |

These six source identities are explicit destinations as well as input profiles.
This June group starts at the permission-row baseline. The separate preceding
May group can now create that baseline from recognized predecessors, but only
when the legacy permission table to be dropped is empty.

```csharp
// This index-only request preserves populated session_message rows.
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.ProjectionIndexes);

// Reaches the first inbox only if ordering would not delete any existing row.
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.InputIndexes);
```

The first directory table uses NOT NULL type and has no strategy column. No
directory rows are inferred from projects/sandboxes. Index migrations preserve
event IDs, type/version strings, payloads and sequence values, and do not resolve
duplicates by deleting rows. The first event aggregate/sequence index and message
session/sequence index are intentionally nonunique, matching this source stage.

The input inbox has a database-generated seq primary key and a distinct unique
text id. It is not replaced with the later id-primary-key/admitted_seq schema.
Recognition checks the full source CREATE SQL to permit AUTOINCREMENT narrowly,
then checks all columns, foreign keys, unique-key columns and explicit indexes.
The engine-generated unique-index name is irrelevant; its origin and key are
verified. sqlite_sequence is SQLite-owned and is not journal-stamped or reset.
The subsequent jittery index transition changes no inbox data, including seq,
id, session_id, prompt, delivery, promoted_seq and time_created.

## Group 1: context snapshots

Recognized inputs and results:

| Exact last source identity | Schema state | Supported next transition |
| --- | --- | --- |
| `20260604172448_event_sourced_session_input` | Event-sourced session_input; no epoch or credential table | `20260605003541_add_session_context_snapshot` |
| `20260605003541_add_session_context_snapshot` | Epoch has baseline, snapshot, baseline_seq, replacement_seq and revision | `20260605042240_add_context_epoch_agent` |
| `20260605042240_add_context_epoch_agent` | Epoch also has agent with NOT NULL / 'build' default | Credential schema creation, now implemented by the initial-group pass |

The snapshot migration executes CREATE TABLE with the source session foreign key.
The agent migration executes ALTER TABLE ADD and preserves populated epoch rows.
It uses the source default; it does not infer an agent from session/message data.

```csharp
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.ContextAgent);
```

ContextSnapshot is also a supported destination. Starting at an already completed
checkpoint is idempotent only after its journal and schema are validated; no
missing migration ID is inserted to make the checkpoint appear complete.

## Group 2: directory strategies and epoch simplification

| Exact last source identity | Schema state | Supported next transition |
| --- | --- | --- |
| `20260611192811_lush_chimera` | New credential schema already completed; project_directory.type NOT NULL; no strategy | `20260612174303_project_dir_strategy` |
| `20260612174303_project_dir_strategy` | Directory type nullable; strategy present | `20260622142730_simplify_session_context_epoch` |
| `20260622142730_simplify_session_context_epoch` | Epoch retains session_id, baseline, snapshot and baseline_seq | Stop; the following message/event reset is blocked |

Starting after lush_chimera does not execute or authorize deletion of populated
credentials. Its schema-only CREATE predecessor and empty-table replacement are
now implemented; see INITIAL-V2.md. Nonempty credential rows remain blocked from
replacement, and credential values are neither imported nor inspected.

The directory migration copies project_id, directory, type and time_created for
every source row into __new_project_directory. The source adds strategy before
the copy but omits it from the INSERT; all newly introduced strategy values are
therefore NULL. No existing strategy is discarded: the input profile rejects
databases that already have that column under an earlier journal identity.
Existing directory IDs/paths/type values are not normalized or reinterpreted.
The source FK pragmas are retained verbatim. Inside the owned transaction they
cannot toggle enforcement; the runner's existing outer policy disables it before
the transaction, checks foreign keys before commit, and restores the original mode.

```csharp
// This row-preserving copy is allowed even with populated context epochs.
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.ProjectDirectoryStrategy);

// A separate explicit request can simplify only an empty epoch table.
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.ContextSimplification);
```

## Concrete data-loss choices

- **Populated pre-order projections:** source session_message_projection_order
  deletes the entire session_message table contents before adding seq. The deleted
  fields would be id, session_id, type, time_created, time_updated and data. Source
  explains that these pre-launch projections lack reliable durable event order;
  it provides no truthful backfill. This port requires the table to be empty,
  keeps the actual DELETE/ALTER/index SQL, and does not fabricate sequence values.
  No approval to delete populated projections has been granted. Such loss would
  need a separate explicitly authorized workflow, which is not implemented.

- **Populated context epochs:** source simplify_session_context_epoch drops agent,
  replacement_seq and revision without storing their values elsewhere. This port
  refuses that operation when the table contains any row. It does not assume
  values are redundant or silently archive them into another schema. Supporting
  source-exact loss would need a separately authorized destructive workflow.
- **Populated old workspaces:** workspace_domain only drops and recreates the
  table; no provider/binding mapping exists in the reviewed source. Populated
  workspaces stay blocked. The row-copy mapping in project_dir_strategy is for
  project directories, not a conversion of legacy workspace rows.
- **Pre-launch resets:** event_sourced_session_input and the later reset/simplify
  migrations delete message/input/event rows and sometimes clear workspace IDs.
  Their completion may identify an input checkpoint, but this runner does not
  execute those resets or synthesize their completion records.

All requested-segment data preconditions are checked before its first write. For
example, requesting directory rebuild plus simplification against a populated
epoch rejects the entire batch; it does not commit the directory step first.

## Recognition and transaction rules

Each checkpoint requires the full exact ordered source identity prefix, not a
timestamp cutoff or number of completed migrations. Existing canonical and the
previously reviewed named/timestamp Drizzle representations use that same rule.
The July pre-split marker is not accepted as a substitute for these June profiles.

Required June structures include all original message/part/todo/session_share
tables and data_migration. Before InputInbox, session_input must be absent; the
next two checkpoints require the AUTOINCREMENT inbox; EventSourcedInput and later
require the id-primary-key/admitted_seq variant. The old session
has a required title and no fork/session-suspend columns. The event table has no
created column. There are no instruction tables, session_pending or kv table.
The first group has no credential table; the second requires the source post-
replacement credential metadata without inspecting its contents. Unexpected
tables, indexes, defaults, constraints, partial histories or scratch rebuild
tables reject before DDL. Source message/part indexes come from
20260312043431_session_message_cursor.

The runner executes source statements in order, validates the resulting profile,
then inserts that migration's completion record. The whole requested group and
any validated journal conversion remain one transaction. Any error rolls it back.
No event payload, event type/version, aggregate sequence, canonical ID, credential
value, or configuration document is generated or rewritten by these additions.

## Source and evidence

Source files are under `packages/core/src/database/migration` in the TypeScript
worktree: the twelve checkpoint modules above, familiar_lady_ursula,
session_message_cursor, session-metadata, add_project_directories, events,
add_sync_owner and the source index/input migrations preceding the checkpoints.
SQL is reviewed and compiled into SourceMigrationCatalog; no SQL rewrite or
resource-generation process is involved. Source license is retained in README.md.

Validation is compilation only. The isolated pinned .NET 11 Core build succeeded
in the later May-group pass; the external CodeModeLexical.cs CS0136 blocker from
the prior pass was no longer present. No database was opened, no SQL/schema/migration execution or
tests were run, and no production data was inspected. These profiles and SQL are
not runtime-verified upgrades.
