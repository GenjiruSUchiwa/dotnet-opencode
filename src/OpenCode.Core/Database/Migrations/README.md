# Reviewed incremental database migrations

This is an explicit opt-in upgrade implementation, including a database startup
hook. It is not a complete port of all source migrations: **forty-one of 46 identities
have executable bodies**, and the split identity supports only its V2 marker branch.
No database was opened, queried, copied, or upgraded during development. Only
source inspection and compilation were performed.

## Database-owner API

```csharp
// The owner already selected and opened this dotnet-channel connection.
var target = new MigrationTarget(selectedDatabasePath);
var plan = await SourceMigrationRunner.InspectAsync(connection, target, cancellationToken);
var result = await SourceMigrationRunner.ApplyAsync(connection, target, cancellationToken);
```

The database owner can instead opt in at construction:

```csharp
var target = new MigrationTarget(selectedDatabasePath);
await using var database = new SqliteDatabase(migrationTarget: target);
// Opening remains the owner's action. No server registration is changed here.
```

Supplying both dbPath and migrationTarget requires matching paths; a mismatch
fails before opening a connection or creating a directory. Omitting migrationTarget
retains the previous strict bootstrap behavior. No target is inferred from global
configuration. The server host owner must explicitly supply its dotnet target.

`MigrationTarget` requires an explicit `opencode-dotnet.db` path matching the
open connection. An explicitly selected `:memory:` target requires a real memory
connection. It does not search global configuration, create directories, open
files/connections, follow production database conventions, or discover a legacy
database. No code in this subtree opens `opencode.db`, `opencode-next.db`, an auth
file, or a credential store.

`SqliteDatabase.CreateConnection()` passes the optional target to
`DatabaseBootstrap.Apply`. Only when that explicit target is present, bootstrap
calls the runner for an existing session database **before starting its own
transaction**. The runner accepts only profiles with a supported path to the
current baseline. Empty bootstrap remains separate; a rejected existing schema
never falls through to fresh creation. Startup does not request a partial upgrade.
Only SqliteDatabase.cs and DatabaseBootstrap.cs were changed outside this subtree;
SessionStore, CredentialStore, Event, and host registration were not changed.

`InspectAsync` returns source journal kind, exact baseline ID, completed IDs,
and pending migration descriptors. It takes a consistent read transaction and
does not stamp a journal. `ApplyAsync` takes an immediate transaction and repeats
classification under that transaction; a previously inspected plan is not a write
authorization and is never blindly reused.

An explicit bounded call permits the pre-split V2 rename without crossing the
disabled credential import:

```csharp
var result = await SourceMigrationRunner.ApplyAsync(connection, target,
    throughMigration: SourceMigrationCatalog.SplitV2);
```

InspectAsync accepts the same throughMigration argument. Supported destinations
are Current, SplitV2, and the thirty-six explicit early V2 checkpoints documented in
[INITIAL-V2.md](./INITIAL-V2.md), [APRIL-V2.md](./APRIL-V2.md), [MAY-V2.md](./MAY-V2.md) and [EARLY-V2.md](./EARLY-V2.md).
A bounded upgrade does not make an older database ready
for the application. Startup still refuses it until every required later
transition has actually completed. Neither API silently skips or stamps the
credential import, and requesting Current through that boundary rejects before
the first write. `MigrationPlan.Destination` and `MigrationResult.CurrentBaseline`
report the actual requested boundary, not an assumed current schema.

## Recognized inputs

The exact V2-only schema at each of these source checkpoints is recognized:

| Last completed migration | Executed suffix |
| --- | --- |
| `20260127222353_familiar_lady_ursula` through `20260312043431_session_message_cursor` | Exact initial/February/March checkpoints: project commands, account/workspace tables, workspace fields (empty only), guarded organization backfill, cursor indexes and event tables; see INITIAL-V2.md |
| `20260323234822_events` | Workspace-name copy/rebuild, entry creation, icon backfill, guarded projection replacement, path and agent/model fields |
| `20260410174513_workspace-name` | Session-entry creation and the subsequent April group |
| `20260413175956_chief_energizer` | Icon backfill, then projection replacement only when entries are empty |
| `20260423070820_add_icon_url_override` | Guarded entry-to-projection replacement and subsequent fields |
| `20260427172553_slow_nightmare` | Session path and agent/model additions |
| `20260428004200_add_session_path` | Session agent/model additions, then the existing May group |
| `20260501142318_next_venus` | Sync owner, workspace time, usage backfill, data state, metadata, path normalization and guarded permission replacement |
| `20260504145000_add_sync_owner` | Workspace time and the subsequent May group |
| `20260507164347_add_workspace_time` | Usage backfill and the subsequent May group |
| `20260510033149_session_usage` | Data-state table creation, metadata and subsequent operations |
| `20260511000411_data_migration_state` | Metadata ADD branch, then path normalization and permission replacement |
| `20260511173437_session-metadata` | Path normalization, then guarded permission replacement |
| `20260601010001_normalize_storage_paths` | Permission DROP only when the legacy table is empty, then row-table creation |
| `20260601202201_amazing_prowler` | Actual row-permission table creation, then the existing June groups |
| `20260602002951_lowly_union_jack` | Directory table creation, projection indexes, guarded ordering, input inbox, input indexes, with an explicit early destination |
| `20260602182828_add_project_directories` | Projection indexes and the subsequent guarded input group |
| `20260603001617_session_message_projection_indexes` | Ordering only if session_message is empty, then the input group |
| `20260603040000_session_message_projection_order` | Input inbox creation and input indexes |
| `20260603141458_session_input_inbox` | Input lookup indexes; existing inbox rows are preserved |
| `20260603160727_jittery_ezekiel_stane` | End of new group; the event-sourced input reset remains blocked |
| `20260604172448_event_sourced_session_input` | Context snapshot creation and agent addition, with an explicit early destination |
| `20260605003541_add_session_context_snapshot` | Context agent addition, with an explicit early destination |
| `20260605042240_add_context_epoch_agent` | Credential schema creation, then replacement only when empty |
| `20260611035744_credential` | Replacement only when credential is empty, then the directory/context group |
| `20260611192811_lush_chimera` | Project-directory row-copy/rebuild, then guarded context simplification, with an explicit early destination |
| `20260612174303_project_dir_strategy` | Context simplification only when its table is empty, with an explicit early destination |
| `20260622142730_simplify_session_context_epoch` | End of second early group; source resets remain blocked |
| `20260730195856_optional_session_title` marker, with the complete known earlier prefix | V2 split rename only, when explicitly requested; Current is blocked by credential import |
| `20260804233008_loose_psylocke` | Split-only no-op; Current is blocked by credential import |
| `20260805200742_import_legacy_credentials` | Guarded workspace-domain transition and the following five migrations |
| `20260808023530_workspace_domain` | All five reviewed migrations below |
| `20260811161259_execution_claim_attempts` | Inbox, worktree, viewed state, nullable binding |
| `20260812181746_session_inbox` | Worktree, viewed state, nullable binding |
| `20260812213948_worktree` | Viewed state, nullable binding |
| `20260819222447_session_viewed_state` | Nullable binding |
| `20260823191254_nullable_workspace_binding` | No schema/data changes |

The journal must contain the **exact full prefix of source IDs** through the
checkpoint, not merely the same number of rows. Unknown/newer IDs, holes,
duplicates, and absent/empty authoritative history are rejected. The pre-split
marker is an explicitly recognized additional identity, not a replacement for
the earlier prefix. Its accepted starting journal contains all source identities
through `20260622202450_simplify_session_input` plus that marker. After the rename,
the marker is retained alongside the completed prefix. Unknown historical IDs
still reject: this is not a claim of coverage for every historical pre-split journal.
The catalog has all 46 current source identities; forty-one have executable bodies.
An unsupported descriptor has null Statements and is never
treated as an empty successful migration.

Recognized journal representations:

- Canonical `migration(id TEXT PRIMARY KEY, time_completed INTEGER NOT NULL)`.
- Reviewed Drizzle named format: `id INTEGER PRIMARY KEY`, `hash TEXT NOT NULL`,
  `created_at NUMERIC` (INTEGER affinity variant accepted), `name TEXT`,
  `applied_at TEXT`, with no defaults or additional constraints/indexes.
- The same INTEGER-primary-key Drizzle timestamp format without `name` and
  `applied_at`. Each integer epoch-millisecond created_at value must uniquely map
  to an actual catalog ID's UTC second prefix, as in source migration.ts.

Other Drizzle ID-column types (including SERIAL), partial name/applied_at journal
upgrades, custom journal tables/constraints/triggers, and unknown timestamp
formats remain unsupported. Source hash columns are preserved, not rewritten or
presented as validated SQL checksums. Identity matching follows the source's
name/timestamp bridge, with the additional schema checks below.

A nonempty canonical journal takes precedence over retained Drizzle history.
A recognized Drizzle journal is converted only after complete classification,
inside the same transaction as the pending SQL. Its original table is untouched.
Canonical completion times record actual conversion/upgrade time, not invented
historical completion timestamps. The pre-split marker is preserved on conversion
and accepted by the bootstrap gate only after opt-in runner validation. Existing
canonical completion records are never rewritten.

## Schema recognition

`SourceSchema` compares the reviewed tables, column names and declared types,
nullability, defaults, primary-key positions, foreign-key endpoints/actions,
explicit index names, uniqueness, ordered columns, collation, direction, and
partial predicates. Column physical order is not used: ALTER ADD appends fields,
whereas fresh source bootstrap can emit them in a different order.

The profiles come from `schema.gen.ts` plus the reviewed transitions. Missing/extra
unprefixed tables, hidden/generated columns, extra constraints/options,
unexpected indexes, core-table triggers, unrecognized legacy `session` storage, and a leftover
`__new_workspace` are rejected. Unknown native prototype schemas are never
repaired. Unrelated underscore-prefixed embedder tables remain untouched.
The supplied connection must have no attached databases or temporary schema
objects that could redirect unqualified source SQL.

The reviewed catalog must exactly match the database owner's generated bootstrap
ID list. A changed bootstrap baseline blocks this runner pending another review.
These are conservative recognized schema profiles, not a general SQLite repair
engine or a certification of historical message payloads.

The pre-workspace-domain profile uses the real legacy workspace columns, defaults,
and project foreign key from workspace-name/add_workspace_time. The pre-split
profile uses the marker branch's canonical V2 shape before the session rename,
including the old session index names and foreign-key targets. It requires the
source message table. Retained part/todo/session_share tables are accepted only
with their reviewed source definitions and are not transformed. Retired
data_migration/session_context_epoch/session_input tables may be absent; if
present they need reviewed metadata and no remaining rows before being dropped.
The account_state INTEGER primary key accepts the two source declarations:
blue_harpoon's explicit NOT NULL and fresh schema.gen.ts's implicit rowid key.
Legacy message/part index definitions include the actual later
session_message_cursor migration, not only the original creation indexes.

The thirty-six early V2 profiles are separate from the July marker branch. They require
the exact source journal prefix and all source tables, including retained legacy
message/part/todo/share storage, without running a V1 import. They distinguish the
old session/input/event shapes, absent versus present context epoch/credential
tables, each epoch column set, and project-directory type nullability/strategy.
The pre-event-sourcing input table's AUTOINCREMENT and UNIQUE(id) are recognized
only with its complete reviewed CREATE SQL, column/foreign-key metadata and
unique-key column identity. SQLite's generated unique-index name is not guessed.
See [EARLY-V2.md](./EARLY-V2.md) for the input/output boundaries and data choices.

## Executed source operations

The February/March group adds nine transitions: project commands, control_account,
legacy workspace, session workspace binding, account/account_state, workspace
fields (empty legacy workspace only), source organization-state backfill, message/
part cursor indexes, and event/event_sequence creation. Account token columns are
created, not imported or read for authentication. The organization guard rejects
non-null selected_org_id values that the source active-account join would not copy.

Two additional credential **schema-only** transitions create the original table
and replace it only when empty. They do not load or inspect credential values or
read external files. Populated replacement remains blocked. Exact profiles and
the final five non-executable identities are listed in [INITIAL-V2.md](./INITIAL-V2.md).

Six additional April-to-May transitions are now implemented:

- `workspace-name`: copies id/type/branch/name/directory/extra/project_id into the
  source replacement table. Only the recognized name-present branch is supported.
  SQL NULL names fail the source NOT NULL constraint and roll back; no coalescing
  to the default is invented.
- `chief_energizer`: creates session_entry and its three source indexes.
- `add_icon_url_override`: adds the column and copies every non-null icon_url.
- `slow_nightmare`: creates session_message, replaces the source indexes and drops
  session_entry only when the old table is empty. No source row-copy exists.
- `add_session_path`: adds the nullable path column.
- `next_venus`: adds nullable agent/model, with no invented selection values.

See [APRIL-V2.md](./APRIL-V2.md) for exact boundaries and conditional coverage.

Eight additional May-to-June transitions are now implemented:

- `add_sync_owner`: adds the nullable event-sequence owner_id without changing
  existing aggregate IDs or seq values.
- `add_workspace_time`: adds time_used with the source NOT NULL/default-zero declaration.
- `session_usage`: adds cost and five token counters, then performs the real
  source JSON-based sums over matching assistant messages. It does not merely
  leave the newly added fields at zero for populated histories.
- `data_migration_state`: creates the actual data_migration table; inserts no
  fabricated data-import completion records.
- `session-metadata`: executes ADD metadata for the recognized no-column
  predecessor. The alternative lovely_romulus/already-present-column lineage
  remains rejected, not adopted by an empty operation.
- `normalize_storage_paths`: runs the four ordered source UPDATEs, preserving
  rows/IDs while normalizing the source-selected path values.
- `amazing_prowler`: drops legacy permission only when it is empty.
- `lowly_union_jack`: creates the actual row-permission table, FK and unique index.

Exact source fields, normalization predicates, and supported boundaries are in
[MAY-V2.md](./MAY-V2.md). The new source transformations do not import files or
enable credential/V1 filesystem imports.

Five additional earlier transitions now run in source order:

- `add_project_directories`: creates the source directory table and project FK.
- `session_message_projection_indexes`: replaces projection lookup indexes and
  adds the nonunique event aggregate/sequence index without changing rows.
- `session_message_projection_order`: executes the source DELETE/ALTER/index
  statements only with an empty session_message table. It never invents seq values
  for pre-launch history or deletes populated history.
- `session_input_inbox`: creates the real AUTOINCREMENT seq inbox, UNIQUE(id),
  session FK and pending index, not the later event-sourced input representation.
- `jittery_ezekiel_stane`: adds source event/type and input/delivery indexes while
  retaining existing inbox, projection and event rows.

The group stops before the destructive event_sourced_session_input transition.

Four earlier source operations are also implemented, in their original order:

- `add_session_context_snapshot`: creates the actual context epoch table and FK.
- `add_context_epoch_agent`: adds the source NOT NULL agent column with 'build'
  default, retaining existing epoch rows and all their other fields.
- `project_dir_strategy`: adds strategy, copies every original project-directory
  row into the source replacement table, and replaces the old table. The new
  strategy is NULL; original project IDs, directories, type values, and timestamps
  are copied without conversion.
- `simplify_session_context_epoch`: executes the three source DROP COLUMN
  statements only when there are no epoch rows whose fields would be discarded.

These groups use explicit early boundaries; credential schema creation and empty
replacement are implemented, but populated replacement and later destructive
resets are not bypassed.

1. `loose_psylocke`, **marker branch only**: runs the exact V1-only-history guard,
   replaces the four session indexes, renames session to session_v2 in place,
   and drops the three retired tables only when absent or empty. The connection
   must have legacy_alter_table disabled so foreign-key references follow the
   rename. Session/message/pending rows, event IDs, event type versions, aggregate
   sequence values, and payload text are not rewritten.
2. `workspace_domain`: executes the source DROP/CREATE only for an empty old
   workspace table. A populated table rejects the entire request. The source
   provides no row-preserving binding conversion, so none is invented here.
3. `execution_claim_attempts`: adds resume_attempts with source default zero.
4. `session_inbox`: creates the actual durable inbox table and its two indexes.
5. `worktree`: creates the actual table and copies project_directory rows using
   the source CASE expression, including git_worktree → git strategy mapping.
6. `session_viewed_state`: adds time_idle, time_viewed, and idle_outcome.
7. `nullable_workspace_binding`: creates __new_workspace, copies every named
   source column, drops the old table, and renames the replacement.

The SQL is compiled into SourceMigrationCatalog; no new resources, package
dependencies, project changes, Bun/Node process, or runtime source checkout are
needed. IDs, session messages, inbox records, event history, provider configuration,
and credential contents are not synthesized or rewritten by these operations.

## Transactions and failure behavior

The source driver commits each migration independently. This conservative native
slice instead applies its requested reviewed segment and any validated journal conversion
in **one immediate transaction**. All classification occurs before its first DDL
or journal write. Each actual transition's output schema is validated before its
completion record is inserted. A later SQL error, schema mismatch, cancellation, or foreign-key
violation rolls back the entire batch rather than leave a partial prefix upgrade.

Current-bound upgrades end with the source migration marked foreignKeys:false.
The runner temporarily disables foreign keys on its dedicated
connection before beginning the transaction, validates foreign keys before
commit, and restores the original connection setting on every exit (including
classification rejection). This also rejects pre-existing foreign-key violations
instead of repairing them. The split-only request uses the same transaction and
foreign-key validation policy. All row-preservation preconditions run before any
DDL or completion writes. Completion rows and their actual SQL commit together.
The runner does not reset data or seed history based only on schema presence.

If connection-setting restoration fails after a successful commit, the error
explicitly says the upgrade committed and the connection must be discarded.
If both migration and restoration fail, both errors are retained. Neither case
authorizes replaying already completed migrations.

## Explicitly unimplemented imports/lineages

- V1 session/message/part transformation and resumable `migration.v1-v2` progress.
- Pre-split journals with additional unreviewed historical identities or shapes,
  incomplete earlier prefixes, or populated retired tables.
- Automatic copying from a separate opencode-next database.
- Legacy auth.json/credential and well-known configuration imports.
- The non-marker V1 squash branch of loose_psylocke.
- Pre-launch message/event resets, credential-table replacements, and populated
  legacy workspace-domain replacement. Populated legacy permission blobs also
  remain blocked because source provides no mapping to permission rows.
  Populated session_entry replacement is also blocked; the source drops its
  id/session_id/type/timestamp/data values without a mapping.
  The five remaining non-executable identities are the initial-schema creation,
  event-sourced-input reset, reset_v2_session_state, simplify_session_input, and
  legacy credential import. Their exact names and decisions are in INITIAL-V2.md.
- Unknown native first-prototype layouts, partial repairs, downgrade/newer schemas.

The complete source V1 import was read. It pairs compaction/summary messages,
materializes embedded attachments, transforms interrupted tools and provider
errors, generates deterministic synthetic IDs, updates aggregate watermarks,
and clears old events through its own resumable workflow. This implementation
does not substitute a shallow row copy or run those transformations implicitly.

## Provenance and evidence

Authoritative inputs are the current working-tree files under
`C:/Repos/sst/kind-nebula`:

- `packages/core/src/database/migration.ts` and `migration.gen.ts`.
- `schema.gen.ts`, `database.ts`, and `packages/core/script/migration.ts`.
- The forty-one implemented migration modules and the older reset/path/credential/
  split-lineage modules referenced by catalog diagnostics.
- `v1-migration.bun.ts`, including transformSession and helper transformations.
- Installed Drizzle `sqlite-core/async/session.js`, `up-migrations/sqlite.js`,
  `up-migrations/utils.js`, and `migrator.js` for journal formats/identity mapping.
- Existing .NET DatabaseBootstrap, generated schema, and SqliteDatabase guards.

Isolated Core compilation uses pinned SDK 11.0.100-preview.7.26381.103 and
`C:/tmp/opencode/migration-core-build`. The earlier external CodeModeLexical.cs
CS0136 blocker was absent in this pass's successful build. No other-owned source
was changed to address it. Compilation remains the only verification performed.

Only compilation evidence is available. No tests, test sources, database opens,
SQL/schema queries, migration runs, production-data copies, or live configuration
evaluation were performed. Successful compilation is not a verified data upgrade.

## Source license

The copied migration SQL is from OpenCode, MIT License, copyright (c) 2025 opencode.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
