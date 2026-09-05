# April workspace and projection transitions

This group adds six actual source transitions, extending the explicit checkpoint
chain backward to `20260323234822_events`, which must already be completed.
It does not claim every input branch is supported.

## Exact checkpoints

| Last source identity | Distinguishing state |
| --- | --- |
| `20260323234822_events` | Event tables exist; workspace.name nullable without default; session_entry and session_message absent |
| `20260410174513_workspace-name` | Workspace name NOT NULL DEFAULT ''; other copied columns retained |
| `20260413175956_chief_energizer` | session_entry and three indexes exist; session_message absent |
| `20260423070820_add_icon_url_override` | project.icon_url_override exists after actual backfill |
| `20260427172553_slow_nightmare` | session_message and three indexes exist; session_entry absent |
| `20260428004200_add_session_path` | Nullable session.path exists; agent/model absent |
| `20260501142318_next_venus` | Nullable session.agent/model exist; following May group available |

Every checkpoint requires the complete exact source journal prefix and matching
table/column/default/FK/index profile. These are explicit throughMigration
destinations, not inferred compatibility versions. Events is an input to this
group; the preceding [initial group](./INITIAL-V2.md) now implements its actual
table creation.

## Workspace-name copy

The source inspects whether workspace.name exists. Its normal name-present branch
copies id, type, branch, name, directory, extra and project_id into __new_workspace,
then drops the old table and renames the replacement. This port executes that
actual INSERT SELECT and preserves populated rows with valid names. Existing
IDs, paths and data are copied verbatim.

The predecessor profile requires the known nullable name column from
add_workspace_fields. The source's alternative name-absent branch copies the SQL
literal ''; that alternative schema/journal lineage is not recognized here.
Missing columns are not repaired to justify a completion stamp.

For a SQL NULL name, the source copy fails the new NOT NULL constraint. No ''
substitution or inferred name is supplied. The exception rolls back all SQL and
stamps in the requested batch. An existing empty string is copied normally.

The source FK pragmas remain in the SQL. The runner owns the outer FK mode and
transaction, checks foreign keys before commit, and restores the connection's
original setting. This operation is distinct from workspace_domain: the later
provider/binding replacement still has no preserving mapping and rejects
populated old workspaces.

## Entry and projection storage

chief_energizer creates session_entry with id, session_id, type, time_created,
time_updated and data, its session FK, and the three source indexes. It does not
infer rows from legacy message/part storage.

slow_nightmare creates session_message and its source indexes, then drops
session_entry. **Source does not copy the entry rows.** This port requires the
old table to be empty before any writes in the requested batch. Populated
id/session_id/type/time_created/time_updated/data values are never discarded or
reinterpreted as canonical messages. No approval for this loss has been given;
there is no destructive override.

To apply the icon backfill while keeping populated session entries:

```csharp
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.IconOverride);
```

To request the empty-entry transition and session fields, select
SourceMigrationCatalog.SessionAgentModel explicitly. A request crossing the
populated-entry boundary rejects the whole batch, not just its last operation.

## Icon and session-field operations

add_icon_url_override executes both source statements: ALTER ADD followed by
UPDATE project SET icon_url_override = icon_url WHERE icon_url IS NOT NULL.
It copies non-null values without URI parsing or defaults; null originals leave
the new field null. Original icon_url remains intact. The backfill is not omitted.

add_session_path adds nullable path. next_venus adds nullable agent and model.
Existing rows, titles, IDs, timestamps and message/event history are preserved.
The operations do not guess a preferred agent, model or filesystem location.

## Boundaries and evidence

Output profiles are checked before completion stamps; all requested SQL and
stamps commit together. SQL errors, guard failures or later profile mismatches
roll back the whole batch. Default-off explicit MigrationTarget validation is
unchanged. Startup still requests Current and cannot cross unimplemented steps.

The later initial-group pass implements account/workspace/event and credential
schema operations with explicit preservation guards. The five remaining identity
decisions are listed in INITIAL-V2.md. Conditional metadata-ADD,
empty-permission-DROP and populated projection/context/workspace restrictions
remain as documented in the preceding group handoffs.

Source: the named TypeScript migration modules under packages/core/src/database/
migration and their reviewed predecessor definitions. SQL is transcribed directly,
not blindly rewritten. The upstream license is retained in README.md.

Validation: isolated Core build with pinned .NET 11, zero warnings and zero errors.
No database opens, SQL/migration runs, tests, native/app execution, production
reads or filesystem/network probes were performed. Build success is not a
runtime-verified data upgrade. Changes are confined to Migrations.
