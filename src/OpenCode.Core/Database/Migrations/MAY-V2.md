# May usage, path and permission migrations

These bounded operations extend the source-ordered chain backward from the
permission-row checkpoint. They do not make the partial migrator suitable for
automatic server compatibility. The existing core hook stays default-off,
requires an explicit validated MigrationTarget, and requests Current. Any
unimplemented operation in that route rejects the whole request before writes.

## Exact checkpoints

Every listed identity must be the end of a complete known source journal prefix.
Names, columns and defaults are checked; schema appearance or a row count never
substitutes for completion history.

| Last completed source identity | Distinguishing schema state |
| --- | --- |
| `20260501142318_next_venus` | Session agent/model exist; event_sequence.owner_id and workspace.time_used are absent |
| `20260504145000_add_sync_owner` | Nullable event_sequence.owner_id exists; workspace.time_used absent |
| `20260507164347_add_workspace_time` | Workspace time_used INTEGER NOT NULL DEFAULT 0; session usage fields absent |
| `20260510033149_session_usage` | Cost and five token counters exist; data_migration table absent |
| `20260511000411_data_migration_state` | data_migration exists; session.metadata absent |
| `20260511173437_session-metadata` | Nullable session.metadata exists; legacy per-project permission blob table remains |
| `20260601010001_normalize_storage_paths` | Same structural profile, but the path transform has actually completed according to the journal |
| `20260601202201_amazing_prowler` | Permission table absent after the guarded source DROP |
| `20260602002951_lowly_union_jack` | Row-permission table and unique project/action/resource index exist |

The last checkpoint was already recognized by the following June group; its
creation is implemented in this pass. The first checkpoint is an input to this
group; the preceding [April group](./APRIL-V2.md) now implements next_venus's
actual column additions. Neither group fabricates its completion.
All entries are explicit throughMigration destinations after validation.

The source shapes retain message/part/todo/session_share and session_message.
They have no input inbox, project_directory, context epoch, credential, kv or
instruction tables. Session title remains required; fork/suspend/usage/metadata
fields appear only at the appropriate known boundaries. Each known index uses
the applicable source definition, not a modern bootstrap substitute.

## Actual source transformations

### Owner and workspace time

`add_sync_owner` adds nullable owner_id to event_sequence. It does not claim
ownership, synthesize values or change aggregate_id/seq. `add_workspace_time`
adds time_used INTEGER NOT NULL DEFAULT 0, preserving populated workspace rows.
This is not the later workspace-domain replacement and does not assign bindings.

### Usage backfill

`session_usage` adds six default-zero fields, then executes the upstream UPDATE:

- cost from `message.data.$.cost`;
- tokens_input/output/reasoning from `$.tokens.input/output/reasoning`;
- tokens_cache_read/write from `$.tokens.cache.read/write`.

Each sum selects only rows where message.session_id equals session.id and the
JSON role equals assistant. Both the extracted values and final sums use the
source coalesce(..., 0) expressions. This is a real backfill, not default-zero
initialization presented as migration. Legacy message JSON is read from the
caller-selected database only during an authorized migration invocation; it is
not modified, imported from another file or converted to a newer message type.
SQLite's JSON/numeric/error semantics are retained. A SQL error rolls back the
new fields, the UPDATE and all completion writes; malformed JSON is not silently
replaced with zero by a managed fallback.

### Data state and metadata

`data_migration_state` creates `(name TEXT PRIMARY KEY, time_completed INTEGER
NOT NULL)`. It inserts no data-import markers. `session-metadata` executes the
real ALTER ADD under a predecessor profile that has no metadata column.
The source also has an already-present-column branch for the historical
`20260530232709_lovely_romulus` lineage. That lineage is not recognized here:
unknown IDs or an unexpected metadata column reject before DDL, rather than
turning this identity into an empty completion stamp.

### Path normalization

The four source UPDATEs run in their original order:

1. Normalize project.worktree backslashes for drive-root and UNC patterns.
2. Normalize escaped backslash pairs in project.sandboxes using the source SQL
   predicates and the already-updated worktree value.
3. Normalize session.directory for drive-root and UNC patterns.
4. Normalize non-null session.path using the source predicate and updated directory.

The SQL uses the source GLOB/LIKE/instr/char(92)/REPLACE expressions, not managed
Path APIs, filesystem inspection, JSON reserialization or blanket slash rewriting.
It changes the intended stored path fields while retaining all rows and IDs.
These values are not used to open files during migration.

## Permission replacement: explicit data-loss boundary

The old permission table stores project_id, time_created, time_updated and data.
`amazing_prowler` drops that table; `lowly_union_jack` creates a different table
with id, project_id, action, resource and timestamps, plus its FK/unique index.
Source contains **no mapping from the old data blobs to the new rows**.

The implemented DROP requires an empty old table. Populated old rows reject the
entire requested segment before any usage/path changes or completion inserts.
No approval to discard project permission data has been given, and no destructive
override, invented row IDs, blob parser or lossy conversion is implemented.

To perform the preserving operations without crossing that boundary:

```csharp
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.NormalizePaths);
```

To request replacement of an empty legacy table, use PermissionRows explicitly.
Its CREATE statements execute even when there is no permission data; this is not
an empty migration body. Starting at PermissionDrop requires the table to be
absent and that exact identity to already be recorded. Starting after replacement
preserves existing modern permission rows.

## Transaction and validation

All SQL executes in catalog/source order. Every actual transition's resulting
schema is checked before its completion stamp; the full requested segment and
validated journal conversion commit together. Path normalization has the same
input/output structure, but its four UPDATEs still run before the stamp. No
route executes merely because its name appears in the catalog.

Projection-order, event/input resets, populated context simplification and
populated workspace-domain blockers are unchanged. This group does not edit
SharedKVStore, Session models, Event code, CredentialStore, Host, or terminal input.

Source: the named TypeScript modules in packages/core/src/database/migration,
plus the original permission/session definitions and the previously reviewed
shared schema/index lineage. SQL is compiled directly into SourceMigrationCatalog;
the upstream license is retained in README.md.

Validation is isolated compilation with the pinned .NET 11 SDK only. No database
opens, SQL/migration execution, tests, native/app execution, filesystem probes,
production credential reads or network probes were performed. A successful build
does not establish runtime correctness or a verified data upgrade.
