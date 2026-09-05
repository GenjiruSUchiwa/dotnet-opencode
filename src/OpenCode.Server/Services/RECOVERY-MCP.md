# Managed recovery and MCP HTTP composition

## Restart recovery

`ServerHost.ServiceLifetime.StartingAsync` acquires the dotnet-channel registration
lease before boot. A stale registered listener must refuse connections and its
recorded process must no longer exist (or have exited). A live/reused PID blocks
startup; it is never signalled. With no registration, the acquired channel lease
is the managed startup boundary. This does not establish ownership over arbitrary
embedded SDK hosts or other channels sharing storage.

`StartedAsync` opens storage and awaits the tracked `SessionExecutionService.StartRecovery`
registration/accounting phase before publishing ready health. The combined Core
entrypoint hands off the same child drain tasks and acknowledges registration;
it does not wait for physical model/tool completion. Those drains run in the
background and their forms/permissions become answerable once ready. The existing `SessionEventBridge` subscription is
started in `SessionExecutionService.StartAsync`, before recovery can commit events.
Recovery first reads the public durable suspended-root snapshot plus validated
running subagent marker IDs, excluding current active owners. It passes that frozen
set to the shared `ShellToolJobs.RecoverAfterConfirmedRestartAsync` before any
Subagent/root sweep, retaining its returned notification IDs. Shell notices cannot
bypass resume accounting by waking one of those pending recovery targets early.
Recovery then calls the shared `SessionSubagents.RecoverSuspendedAsync(maxAttempts: 10)`
once, using that service's host lifetime. This includes roots, supported background
children, and notification recovery; no separate engine sweep or forced resume is
called. Core owns attempt accounting, instruction loading, model/tool
readiness, scheduling, and durable outcomes; Server does not clear blocked claims.

The service retains `RecoveryReport`/`RecoveryFailure` and logs blocked reasons and exhausted IDs.
Shutdown cancels recovery, awaits scheduling, and awaits active Core drains before
closing the event bridge. Recovery is not called by arbitrary SDK construction.
This is local managed-service recovery, not clustered fencing.

## MCP reads

Source contracts:

- `packages/protocol/src/groups/mcp.ts`: `mcp.list`, `mcp.resource.catalog`.
- `packages/server/src/handlers/mcp.ts`: Location response and server status shape.

`Endpoints/McpEndpoints.cs` mounts `/api/mcp` and `/api/mcp/resource`, using
`ToolLocationFactory.AcquireAsync` with the same `PermissionLocationMap` as session
execution. Both read one settled `McpRuntime.ObserveAsync` observation; neither
constructs a second registry nor reports configured declarations as connected.
The lease remains alive until the observation has completed.

Runtime add/remove/connect/disconnect now observe configuration first and invoke
the corresponding Core override operation using that same lease. Success returns
204; absence returns `McpServerNotFoundError`. Connection failures remain real
`failed`/`needs_auth` statuses rather than fabricated HTTP failures or successful
connections. No config writes substitute for those operations. OAuth and CodeMode
limitations remain Core-owned and are not relaxed by these endpoints.

`McpEventBridge` queues changes without synchronously reentering Core and maps
status/resources flags to the current public inventory in
`packages/schema/src/event-manifest.ts`. `LocalToolOptions.McpCreated` attaches it
before first observation, including session-triggered discovery. The factory owns
the returned subscription and disposes it with the runtime. Tools/prompts
notifications are not public events here. `McpForms` receives the same
  Location-owned `FormService` used by HTTP, before connection capabilities are built.

`/api/location` now uses the existing `CatalogLocation` resolver, matching
`packages/server/src/handlers/location.ts` rather than inventing `prj_local`.
The filesystem/configuration catalogs are assigned to a separate implementation
owner. That owner now supplies actual configuration source entries. Client
`GetConfigAsync` preserves the direct `ConfigEntry[]` response, entry ordering,
and omitted virtual-document paths; it does not invent a Location envelope or
relabel a merged document (`packages/server/src/handlers/config.ts`).

## Ready-path corrections

- `packages/core/src/session/session.ts:150–179`: HTTP prompt retries reconcile
  the original item but still wake unless `resume:false`; new admission precedes
  execution/model readiness. Typed prompt input now delegates to the actual
  `SessionExecutionEngine.AdmitPromptAsync` preparation boundary, preserving
  attachments and metadata. Attachment/skill failures map to `files`/`skills`
  invalid-request fields. Unsupported plugin/revert/lowering behavior remains
  Core-owned rather than being dropped by HTTP.
- `session.ts:135–149` and `packages/server/src/handlers/session.ts:541–565`:
  inbox cancel/steer/queue use Core durable mutations, exact operation-specific
  conflict text, and 204 responses. Only steer wakes, after the mutation commits.
- `session.ts:259–262`: wait checks session existence then awaits the process-local
  coordinator; cancelling the request never interrupts the active execution.
- `packages/server/src/handlers/event.ts`: SSE uses 15-second comment heartbeats,
  `no-cache, no-transform`, `X-Accel-Buffering: no`, and `nosniff`. One write gate
  serializes complete frames and flushes; both producers settle before disposal.
- `packages/server/src/middleware/authorization.ts`: a nonempty `auth_token`
  query value takes precedence over Basic headers. Unauthorized responses use
  the canonical error envelope and challenge. Query validation treats the token
  as transport authentication, not a domain query option.
- `packages/server/src/process.ts` and `cors.ts`: the existing source-matched
  origin policy now applies to every HTTP route, before authorization, including
  browser preflight requests. PTY ticket validation remains unchanged.
- `packages/protocol/src/groups/session.ts:580–625` and corresponding handlers:
  instruction-entry list/put/remove call the shared `SessionInstructionEntries`.
  The HTTP boundary checks session existence, matching session-location middleware.
  Put maps `InstructionEntryValueTooLargeException` to the exact 413 envelope.
  Core owns producer rows; no immediate event, recency update, or wake is added.
- Saved permissions use one host `SqlitePermissionGrantStore`, registered as both
  `IPermissionGrantStore` and `IPermissionSavedStore` before tool composition.
  List defaults to the resolved Location project, remove is idempotent, and an
  always reply persists before approving a tool. Native-only
  `/api/permission/capabilities` reports the actual persistent shared registration;
  it is not inferred from a pending request or the presence of save resources.
- Forms use one `FormService` per authoritative Location, shared between HTTP and
  `IMcpElicitationForms`. All seven routes and canonical events are mounted. See
  `src/OpenCode.Core/Forms/README.md` for ownership, state, validation, and client integration.
- Question/Subagent tools are registered with the same Form/permission graph and
  deferred access to the single host Subagents service. ShellTool now resolves the
  shared ShellLocationServices runtime, not its compatibility-only process arguments.
  ShellJobs is now a deferred getter for one real ShellToolJobs/JobRuntime backed by
  the coordinated JobBackgroundStore. Background support is no longer a placeholder;
  marker failures still reject handoff and malformed records remain preserved.
- One SessionTitleService is injected into execution and empty-rename HTTP handling.
  Nonempty titles retain ordinary rename behavior. Title and subagent tasks settle
  before the Session event bridge closes; no prompt-prefix title fallback is added.

The JobRuntime is also cancelled/joined before the event bridge and database close.
No service constructor, ordinary tool registration, explicit remote server, or
arbitrary SDK host invokes managed restart recovery. Existing election and
prior-owner-death checks remain the authority boundary.
