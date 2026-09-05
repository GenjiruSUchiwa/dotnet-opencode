# Server Adapter Contract

Core has no dependency on Server. The Server owner implements its existing
`IPermissionLocationServices` over one host-owned
`OpenCode.Core.Locations.PermissionLocationMap`. Tools must obtain their permission
instance from this same map; do not create a second map for HTTP requests.

## Composition

`StorePermissionLocationFactory` implements `IPermissionLocationFactory`. Supply:

- The authoritative `SessionStore`.
- `Func<LocationRef, CancellationToken, ValueTask<LocationInfo>>` for authoritative
  Location/project resolution. Core does not call CatalogLocation or infer daemon cwd.
- `Func<LocationInfo, AgentId?, CancellationToken, ValueTask<AgentInfo?>>` for live,
  Location-scoped Agent/config resolution. It runs on every evaluation. Null Agent
  means apply the real Agent resolver's default selection; null result denies all.
- An `IPermissionGrantStore`. Its `Persistent` property must be true only if writes
   commit durably before returning. `MemoryPermissionGrantStore.Persistent` is false.
- An optional per-Location permission-hook factory.

Alternatively implement `IPermissionLocationFactory` directly if the host's complete
Location graph owns additional scoped dependencies. Return `PermissionLocationScope`
with those dependencies in `OwnedResources`; do not put shared global stores there.

`StorePermissionRules` fetches the current Session on each evaluation, rejects a
Session routed to the wrong Location, and resolves the explicit Agent or current
Session Agent before requesting current rules. It does not use `AgentInfo.CreateDefault`
or the manually populated `LocalPermissionRules` as an authority fallback.

The Server-side map construction can supply the bridge without a Core dependency:

```csharp
var map = new PermissionLocationMap(factory, (location, permissions) =>
    new PermissionEventBridge(location, permissions, feed)
        .RunAsync(CancellationToken.None));
```

The map starts this dispatcher exactly once per loaded service. Do not start a
bridge per lease or attach an HTTP-request cancellation token. On invalidation or
shutdown, Core closes Permissions, waits for the bridge to finish, drains leases,
and disposes owned dependencies. Dispatcher failure closes Permissions, so waiting
assertions cannot remain stranded behind a dead event bridge.

## Lease Adapter

- `AcquireAsync(string? directory, string? workspaceId, ct)` must first resolve the
  selectors through the host's canonical Location selection, then call
  `map.AcquireAsync(LocationRef, ct)`. Missing selectors must not invent a daemon cwd.
- `TryAcquireLoadedAsync(LocationRef, ct)` delegates directly to the Core map. It
  never constructs a service. Null means authoritatively unloaded; closing or failed
  services raise `NotSupportedException` rather than an empty-list success.
- Wrap `PermissionLocationLease`, forwarding `Location`, `Permissions`,
  `PersistentGrants`, and `DisposeAsync`. Lease disposal does not dispose Permissions.
- Location lifecycle invalidation calls `map.InvalidateAsync(ref)`. Host shutdown
  calls `map.DisposeAsync()`. Do not invoke either while holding a lease that the
  caller itself must release.

The map retains idle entries until explicit invalidation/shutdown. It does not yet
implement the source's filesystem-existence-based zero-idle-TTL eviction. Location
construction is serialized; independent loaded Locations execute independently.
Explicit workspace identity stays in the key; the injected factory must reject
unsupported placement rather than silently treat it as local.

## Canonical Request Creation

`PermissionService.AskAsync(PermissionAskInput, ct)` accepts:

```csharp
new PermissionAskInput(
    SessionId: session.Id,
    Action: payload.Action,
    Resources: payload.Resources,
    Id: payload.Id,
    Agent: payload.Agent,
    Save: payload.Save,
    Metadata: payload.Metadata,
    Source: payload.Source)
```

The Session ID comes from the authenticated route/context, not a body override.
All fields after Resources are optional. Caller IDs are retained and validated
with the source-compatible `per` prefix. Only pending duplicate IDs fail; this is
not an idempotent admission API. Missing IDs are generated. Missing source stays
missing: do not create a fake ToolContext or tool-call ID. Tool leaves retain their
existing overload, which constructs source from the actual invocation context.
Collections and JSON metadata are copied before asynchronous evaluation.

Ask returns `PermissionDecision(Id, Effect)` for allow, deny, or ask. Only ask creates
pending state and emits `PermissionAsked`. `AssertAsync(PermissionAskInput, ct)` is
also available when the caller needs to wait. Caller cancellation removes only that
assertion's pending instance, even if its ID is reused after an earlier reply.

Map `PermissionSessionNotFoundException.SessionId` to the existing Session-not-found
response. `PermissionLocationMismatchException.SessionId` means placement changed;
resolve current placement rather than apply stale rules. Duplicate pending IDs
raise `InvalidOperationException`, consistent with the source's non-idempotent
duplicate failure. Invalid fields raise `ArgumentException`.

`ReplyAsync` still requires the owning Session ID. Core now enforces the durable
grant guard itself: `always` with nonempty save resources throws
`NotSupportedException` if `PersistentGrants` is false, without completing the
pending request or adding a grant. The existing Server guard can remain. `once`,
`reject`, and `always` with no save resources do not require durable storage.

## Not Enabled

No Server, SDK, Schema, runner or CatalogLocation files were changed. No HTTP
registration or tool auto-registration was added. Permission notifications remain
ephemeral. `SqlitePermissionGrantStore` now supplies durable saved-grant listing/removal
through `IPermissionSavedStore`; see `SAVED-GRANTS.md` for the Server handoff.
Keep tools disabled until Location/config resolution, authenticated replies and
the one-bridge pipeline are wired and verified by their owners.
