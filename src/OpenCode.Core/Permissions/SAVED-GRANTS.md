# Durable saved permissions

`SqlitePermissionGrantStore(IDatabase)` implements both `IPermissionGrantStore`
(rule evaluation and approved saves) and `IPermissionSavedStore` (host administration).
Use the host's existing durable, channel-selected database and share one store across
Locations. Do not construct another database or fall back to the memory store.

## Host API

```csharp
var saved = new SqlitePermissionGrantStore(database);
// Pass this same instance as ToolLocationFactory's IPermissionGrantStore.

IReadOnlyList<PermissionSavedInfo> projectGrants =
    await saved.ListSavedAsync(projectId, ct);
IReadOnlyList<PermissionSavedInfo> allGrants =
    await saved.ListSavedAsync(ct: ct);
await saved.RemoveAsync(savedPermissionId, ct);
```

The inputs above use the existing `ProjectId` and `PermissionSavedId` Schema types.
`ListSavedAsync` returns existing `PermissionSavedInfo` values, not a new wire DTO.
Omitting project selection lists the global saved catalog, as upstream does.
`RemoveAsync` deletes by stable saved ID; an absent ID is an idempotent no-op. These
are authenticated host administration APIs, not agent tools or approval-reply APIs.

`IPermissionGrantStore.ListAsync(string projectId, ct)` is always project-scoped and
maps those rows to allow rules. `AddAsync(projectId, action, resources, ct)` is called
by `PermissionService` only for an admitted `always` reply with save resources.
Configured denies still win before saved allows. User decline, correction feedback,
leaf policy translation and cancellation behavior are unchanged.

## Storage and provenance

This directly uses upstream `permission/saved.ts`, `permission/sql.ts`, and the existing
bootstrap table `permission`:

- `id`: existing `PermissionSavedId.Create()` generates the source `psv_` namespace.
- `project_id`, `action`, `resource`: stored verbatim and returned as the row's origin.
- `time_created`, `time_updated`: epoch milliseconds on insertion.
- Existing unique index: `(project_id, action, resource)`.
- Existing project foreign key: cascade delete. A missing project is an error; this
  store does not invent or insert a project.

All resource insertions in one save use one database transaction. `ON CONFLICT DO
NOTHING` preserves existing IDs and timestamps when a grant already exists, including
duplicates within a batch. Other database constraints and errors propagate. Empty
batches do not open the database. IDs are never reconstructed from array positions,
hashes or rule text, and saved grants do not acquire deny/ask effects.

There is no Session, request, user or tool-source column in the source table or
`PermissionSavedInfo`. Do not fabricate that provenance. The stored project identity
and stable grant ID are the available origin information.

## Serialization and cancellation

Store operations share one asynchronous gate per injected `IDatabase` object, even
if more than one store adapter is constructed. SQLite transactions and the existing
constraints remain authoritative for other writers/processes. The gate is not a
cluster lock. Lists read fresh rows; there is no stale in-memory grant cache.

Add/remove use `IDatabase.RunInTransactionAsync`; connection, commit and rollback
ownership stays with that existing boundary. Cancellation propagates while waiting
for the gate or running an operation. `PermissionService.ReplyAsync` already supplies
`CancellationToken.None` after reply admission, so the durable batch commits before
the waiting tool is approved, independent of HTTP-request cancellation. Storage
failure does not complete the pending permission as approved.

`Persistent` is true because this implementation writes to the host's durable
database. Supplying an in-memory database does not make it durable; production
composition must inject the real database. No new schema, migration, fallback store,
event stream, or grant-removal notification is introduced.

Verification is source inspection and isolated, local .NET 11 compilation only.
No database was opened or queried during verification.
