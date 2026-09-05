# Local Tool Host Composition

`ToolLocationFactory` is a concrete `IPermissionLocationFactory`. It constructs
Permissions, local path policy, mutation snapshots, ripgrep and one shared registry,
then installs read/grep/glob/write/edit/skill/shell through one stable ordered transform.
Shell uses the native foreground scanner and process source described in `SHELL.md`.
The factory does not itself enable model tool advertising. Webfetch is
added only when the host explicitly supplies `LocalToolOptions.WebFetch`; see `WEBFETCH.md`.

## Host Wiring

```csharp
var factory = new ToolLocationFactory(
    sessionStore, resolveLocation, grantStore,
    info => new LocalToolOptions(home, ripgrepExecutable, loadReadInstructions, Formatter: formatter));

var locations = new PermissionLocationMap(factory, (info, permission) =>
    new PermissionEventBridge(info, permission, eventFeed)
        .RunAsync(CancellationToken.None));
```

The bridge is Server-owned; this example belongs in host composition, not Core.
Register the SAME `locations` instance in the Server permission adapter and eventual
runner. Do not leave Server on a separate map using `StorePermissionLocationFactory`.

`resolveLocation` is `Func<LocationRef, CancellationToken, ValueTask<LocationInfo>>`.
It must preserve authoritative Location identity and resolve the real project.
Explicit workspace placement is rejected, not silently bound to local disk. Home
and ripgrep paths must be absolute; Home must match the host's global home selection,
including `OPENCODE_TEST_HOME` when applicable. Factory construction does not start
processes, discover executables, mutate files or make model calls.

Rule evaluation is concrete: `StorePermissionRules` fetches the current Session,
checks placement, and calls `AgentCatalog.ResolveAsync(info.Directory, agent, ct)`
for the explicit, Session-selected or default Agent on EVERY evaluation. Unsupported
config propagates an error; missing Agents deny all. Snapshots and permission
assertions share this rule source. There is no generated allow fallback.

Memory grants remain non-durable: `always` with save resources is refused until
the supplied store commits grants durably. Once/reject and pending requests work
through the actual permission queue and the single dispatcher above.

For durable grants, supply `new SqlitePermissionGrantStore(database)` over the host's
existing durable database. The same object implements `IPermissionSavedStore` for
the authenticated saved-list/remove endpoints. See `../Permissions/SAVED-GRANTS.md`.

Additional `LocalToolOptions` handoff:

```csharp
ShellEnvironment: sessionEnvironment.Get,
McpForms: formsForLocation,
McpCreated: (location, runtime) => attachMcpBridge(location, runtime)
```

`sessionEnvironment` is the host's existing `Core.Session.SessionEnvironment` singleton.
`formsForLocation` is its `IMcpElicitationForms` adapter for this `info`. The Server-owned
`attachMcpBridge` callback returns `IDisposable`; it is invoked before the runtime can
be observed and stays attached through MCP shutdown. These are actual host services,
not additional registries or no-op callbacks. See `MCP-INTEGRATION.md` and `SHELL.md`.

## Runner API

After the runner owner confirms the complete permission pipeline:

```csharp
await using var tools = await factory.AcquireAsync(locations, session.Location, ct);
var mcp = await tools.Mcp.ObserveAsync(mcpConfiguration, ct);
var snapshot = await tools.SnapshotAsync(session.Id, selectedAgentId, ct);
// Advertise snapshot.Definitions for this ONE model request; retain this snapshot.
var result = await snapshot.ExecuteAsync(callName, callInput, toolContext, ct);
```

Keep the lease until the request's calls settle. Build canonical `ToolContext` from
real Session, Agent, Message and call IDs, never from tool input. Only explicit
`ToolExecutionException` is recoverable model output. Configured policy blocks and
correction feedback are translated by the calling leaves, not a registry-wide
exception catch. User decline, interruption and defects retain control/error semantics.

`result.Output` is encoded JSON when `HasOutput` is true; `result.Content` contains
canonical text/file parts. The runner still owns durable tool call/result settlement,
generic output bounding and continuation. None of that integration was enabled here.

The lease exposes the actual `Registry` for trusted producer transforms. Request
disposal releases only the authoritative map lease. Map invalidation/shutdown closes
Permissions, drains the single bridge and active leases, then disposes the registry.
The factory has only a weak association from the authoritative permission instance
to tool state, not a second Location identity map.

## Supported Behavior

- Skill uses the existing live local Skill catalog, ID-scoped permission assertions,
  source skill-content rendering, and a ten-path resource sample. It returns canonical
  tool output and metadata; Session delivery and guidance capability handoff are in
  `SKILL-INTEGRATION.md`. It does not publish read-style synthetic instructions.
- Read declares and returns canonical `file`, `text-page`, and `list-page` outputs.
  Directories are directory-first and culture-sorted; symlinks keep their own tag.
- Text pages use 2000 lines, a 50 KiB budget, 2000-character previews, next offsets
  and out-of-range errors. Offset/limit zero use source defaults. Chunked reads retain
  only a bounded line prefix and selected page, even for large offsets or long lines.
- PNG/JPEG/GIF/WebP/PDF signatures produce base64 output and canonical model media
  parts, with the source 20 MiB ingestion cap checked while reading. Other binary
  content is rejected on NUL detection over consumed text.
- Grep/glob retain source filtering and structured results. Invalid regex, expected
  exit failures and timeout are explicit tool failures; cancellation is not timeout.
  Canonical match text stays intact with its byte offsets.
- Write/edit authorize external directories and shared `edit` action with a preview
  under the snapshot lock. Stale content/target snapshots fail before writing.
  BOM preservation and parent creation are implemented. Replacement construction
  is linear in resulting text size rather than repeated whole-file concatenation.

Explicit local limits: 20 MiB retained search records, 1,048,576 UTF-16 characters
per search record, and 100,000 enumerated directory entries. Mutation snapshots and
replacement content default to 20 MiB (`MaximumMutationBytes`). Oversized values fail
rather than being silently skipped. Diff generation also has a complexity guard.

`LoadReadInstructions` is now a required Session-owned callback. See
`READ-INSTRUCTIONS.md`: discovery now connects to the Session-owned direct durable
synthetic loader. The daemon owner has wired that boundary; other hosts must supply
the same real loader, not a no-op or inbox fallback.

The Location also owns one shared `McpRuntime`, exposed as `tools.Mcp`. Await
`tools.Mcp.ObserveAsync(configuration, ct)` BEFORE the SnapshotAsync call above and
reuse the returned observation for MCP instruction composition. The readiness and
execution owner has wired this ordering. See `MCP-INTEGRATION.md`; snapshot capture
does not create another runtime or perform duplicate observation.

Null `Formatter` now selects `Formatting.LocalFormatter`, which reads actual
`formatter` configuration, captures a pre-write plan and applies supported commands
after approval/write. An absent/false formatter setting disables formatting. Custom
adapters implement `PrepareAsync` and return an `IToolFileFormatPlan`; preparation
must not execute commands. See `../Formatting/README.md` for supported discovery,
commands, explicit npm guards and local limits. Supplied formatter/hook objects
remain host-owned. Image resizing and the full MIME database remain absent.
Common text extensions are mapped locally; others use
`application/octet-stream`. Authorization retains source lexical/symlink behavior,
not an OS sandbox. External writers can still race after snapshot validation.

## Activation

Legacy `ToolRegistry` construction remains empty. No Server DI, SDK, runner or
provider request changed. Host composition must opt into this factory and wire the
authenticated approval dispatcher before activation. Verification is build-only:
no builtins, process adapters, providers, databases, applications or tests were run.
