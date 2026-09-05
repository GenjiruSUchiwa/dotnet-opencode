# Embedded tool execution

The default constructor/CreateAsync now supplies an owned, tool-capable embedded
host using the existing public Server composition helpers. See `OWNED-HOST.md` for
options, explicit user-decision APIs, lifetime ordering and recovery restrictions.
The dependency-injected constructor described below remains externally owned.

The six-argument `OpenCodeClient` constructor accepts the host's `SessionStore`,
`CredentialStore`, `ProviderResolver`, compatibility `ToolRegistry`, authoritative
`ToolLocationFactory`, and shared `PermissionLocationMap`. It constructs the same
`SessionExecutionEngine` used by the server, not a separate model/tool loop.
An optional `SessionMovement` argument wires the host's existing movement
domain into that engine. It must share the database/store/permission map; the SDK
does not construct a second coordinator or placement service.
The optional `instructions:` argument supplies `SessionInstructionEntries` from
that same database. `client.Instructions` exposes its list/put/remove operations;
the owned path-based constructor creates this service automatically. A host that
omits it gets an explicit composition error on access, not a separate store.

`queries:` supplies typed MessageAsync/ContextAsync reads; the owned constructor
creates this service. `reverts:` supplies the Revert domain facade. To capture real
undo boundaries, pass the same `SessionSnapshotLocations` as `snapshots:` to the
engine and Revert service and wire the prompt preparation `commitRevert` callback.
The owned SDK constructor supplies these pieces automatically. Injected services
remain host-owned. See `../OpenCode.Core/Session/REVERT-SNAPSHOTS.md`.

`subagents:` exposes the host's lifetime-owned `SessionSubagents` through
`client.Subagents`. Register QuestionTool/SubagentTool with the existing Location
FormService/PermissionService, not SDK-local replacements. See
`../OpenCode.Core/Session/Subagents/README.md` for registration and recovery limits.
Managed startup can call `client.Subagents.RecoverSuspendedAsync(maxAttempts)` after
proving the previous owner is dead and acquiring registration ownership. This combines
child/notification recovery with the existing root sweep; it is not a normal request
or constructor side effect.

`titles:` supplies `SessionTitleService` for asynchronous post-promotion titles and
explicit `GenerateTitleAsync`. The owned SDK constructor supplies/cancels/joins its
title lifetime before closing stores. AskAsync leaves the title unset instead of
using a truncated prompt as a substitute. Injected services remain host-owned; see
`../OpenCode.Core/Session/TITLE-GENERATION.md` for model selection and rename guards.

`GenerateAsync(sessionId, prompt, ct)` uses the engine's same Location/model/tool
composition for one transient request with read-only instruction/history preview.
It does not admit input, execute returned tools or publish usage/Session events.
See `../OpenCode.Core/Session/GENERATION.md` for the Server response shape and limits.

Every tool snapshot now binds Jint Code Mode. `codeModeLimits:` overrides the
explicit finite native engine policy, not upstream's unlimited defaults. Catalog
observation, execute permission suppression and execution share the same snapshot;
see `../OpenCode.Core/Instructions/CODEMODE-INTEGRATION.md`.

The factory must use that SessionStore and construct that map's Locations. Bind
`LocalToolOptions.LoadReadInstructions` to the client's
`LoadReadInstructionsAsync` callback. The callback can capture a client variable
assigned after factory/map construction: tools load lazily at readiness/execution,
not during client construction. Do not replace it with an empty callback.

The host owns database readiness, the injected services, the permission map's one
notification dispatcher, and authenticated permission replies. Pending permission
asks stay pending; the SDK never auto-approves them. The map and execution must not
use separate permission services. Memory-only grant stores continue to reject
persistent approvals according to the existing permission contract.

Use `AdmitPromptAsync` for typed URI file/agent/skill preparation and durable admission;
`AdmitAsync` is the compatible text shorthand. Both reconcile caller IDs before
preparation. `PromptAsync` has a typed overload and no longer invokes instruction
readiness before admission. See `../OpenCode.Core/Session/PROMPT-PREPARATION.md` for
limits and the optional `prompts:` image-adapter composition argument.
`WakeAsync(sessionId, lifetime)` schedules
advisory execution after admission. `ResumeHostedAsync` explicitly runs/joins and
waits for settlement. Both require a cancellable host lifetime. `AwaitIdleAsync`
includes queued successor wakes and terminal settlement.

On shutdown, cancel the execution lifetime and await owned sessions before closing
the map, stores, and HTTP transports. `DisposeAsync` does not dispose injected
services. The path-based constructor now owns the full embedded composition rather
than a text-only database/HTTP pair.

## Source mapping and limits

- `packages/sdk/src/internal/host.ts`: host composition/lifetime boundary. This
  constructor is an explicit native embedding seam, not full SDK layer parity.
- `packages/core/src/session/model-request.ts`: request definitions and execution
  capability remain paired through the engine's captured ToolSnapshot.
- `packages/core/src/session/runner/step.ts` and `runner/llm.ts`: one physical
  stream, durable tool settlement, and context reload before follow-up.
- `packages/core/src/session/instructions.ts`: read-discovered instructions use
  the existing durable loader rather than SDK-local instruction state.

No new tool modules, provider adapters, attachment preparation, automatic database
bootstrap, permission UI, or default movement service are supplied by this constructor.
These remain host/domain responsibilities. Existing Code Mode and unsupported
producer guards remain active.

Verification: SDK/Core/Schema build with the repository's .NET 11 executable and
`--artifacts-path C:\tmp\opencode\core-finish-pass`; no tests or runtime operations.
