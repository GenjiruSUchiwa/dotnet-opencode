# MCP Runtime Handoff

## Integration

- Construct one `McpRuntime(directory, scopedRegistry, toolPermission, forms: locationFormAdapter, oauth: channelOAuthService)` per implicit-local Location, not per prompt or process. The optional adapters enable elicitation and channel-persisted OAuth. Construction only installs a stable transform; it does not connect. See [OAuth handoff](OAUTH.md) for the authorization/persistence APIs and required Server integration.
- Parse each normalized config document's `mcp` object as the existing `OpenCode.Schema.McpConfiguration`. Pass documents in precedence order to `McpRuntime.Configure`. Server definitions replace and global/per-server timeout fields merge.
- Before capturing each request's tool snapshot, await `runtime.ObserveAsync(configuration, ct)`. This performs initial discovery, waits for enabled servers, reconciles config with operational overrides, applies pending catalog notifications, and flushes the same registry transform. Reuse the returned observation for instructions. Notifications also refresh the runtime and registry between observations.
- Instruction owner has implemented `McpInstructionSource.FromObservation(observation, agent)`. Pass its result to `SessionStore.SelectInstructionsAsync(..., mcp: source)` at BOTH readiness and execution boundaries. The runtime observation matches that adapter directly; there is no second MCP guidance renderer in this subtree.
- `available: false` means configuration/source observation itself failed; existing instruction values then remain in effect. Disabled servers are a successful empty observation. Connection failures are settled per-server `failed`/`needs_auth` statuses and do not blanket-reject other servers or config.
- Preserve/expose `observation.Servers` through the Location status surface. Never discard failures just because no tools were discovered.
- Dispose the runtime with its Location. The registry registration is real and idempotently disposed; the official SDK owns connection/process shutdown.
- Tool owner wiring: add `McpRuntime Mcp` to `ToolLocationState`, construct it after builtin registration with the SAME registry and permission service, forward it through `ToolLocationLease`, and dispose MCP before the registry. `SettledObservation` is null before discovery, during an update, or after a failed registry flush; never substitute null with an empty observation for enabled servers. Prefer the return value from `ObserveAsync` for each request so tool snapshot and instructions share that observation.
- `PromptAsync` and `ReadResourceAsync` invoke real SDK operations with execution deadlines. Catalogs include actual SDK prompts and tools plus Schema resources/templates.

## Server/Client owner APIs

All types below are in `OpenCode.Core.Mcp`, except the existing Schema configuration/status types. No new project/package reference is needed.

```csharp
Task<McpObservation> AddAsync(string server, McpServerConfig config, CancellationToken ct = default);
Task<McpObservation> RemoveAsync(string server, CancellationToken ct = default);
Task<McpObservation> ConnectAsync(string server, CancellationToken ct = default);
Task<McpObservation> DisconnectAsync(string server, CancellationToken ct = default);
IReadOnlyList<McpServer> Servers { get; }
event Action<McpRuntimeChange>? Changed;
```

- Acquire the existing shared `ToolLocationLease` and await `ObserveAsync(InstructionCatalog.ReadMcpConfiguration(directory), ct)` before the operation. This loads current configured servers rather than treating an uninitialized runtime as an empty catalog. Use the same lease/runtime for the operation. Do not edit config files or construct a second runtime to service a mutation.
- `PUT /api/mcp/{server}` can pass `McpAddPayload.Config` to `AddAsync`; `DELETE` can use `RemoveAsync`; the existing connect/disconnect routes can use the matching methods. Discard the returned observation for the source's successful no-content mutation response, or retain it internally. Server/Client routes were not edited in this pass.
- `RemoveAsync`, `ConnectAsync`, and `DisconnectAsync` throw `McpServerNotFoundException` when the name is absent; its `Server` property identifies the requested server. Map this to the existing MCP not-found wire error, not an empty success or 503.
- Failed connection attempts settle the entry to `failed` or `needs_auth` and return the real observation; successful invocation of the operation is not a claim that connection succeeded. `ConnectAsync` always reconnects, including definitions with `disabled: true`. `DisconnectAsync` closes the client, removes its tools/prompts/resources/guidance, and retains the entry as `disabled`.
- Add/replace overrides take precedence over configured definitions. Removal installs a runtime tombstone. Both survive config reloads until the Location runtime is disposed or an explicit add replaces the override. Neither writes config or credentials. A disconnect survives unchanged observations; an effective definition change can reconnect it. Operational definitions use their own timeout settings, not configured global defaults, matching TS override precedence.
- `Servers` is a nonblocking, server-sorted status snapshot, including in-progress `pending` handshakes. It does not load configuration. Use it after initial observation when the host needs status without waiting for catalog discovery again.
- `McpRuntimeChange` has `string Server` and flags `McpChangeKind Kind`: `Status = 1`, `Tools = 2`, `Prompts = 4`, `Resources = 8`. Status notifications are immediate. Catalog notifications occur after registry reload and complete observation publication. Expand combined flags if bridging to the existing `mcp.status.changed`, `mcp.tools.changed`, and `mcp.resources.changed` ephemeral events. TS `mcp.prompts.changed` is Core-local; do not invent a public wire event.
- `Changed` handlers run under the lifecycle gate. They must only enqueue work and must not synchronously wait for another runtime call. A handler can read `Servers` without blocking. Unsubscribe when the bridge closes. Subscriber failures are logged and do not stop lifecycle updates. This is an invalidation hook, not a durable event feed or replay log.
- Existing `PromptAsync`/`ReadResourceAsync` now acquire a current client under the lifecycle gate and distinguish `McpServerNotFoundException` from `McpNotConnectedException`. Their SDK result types and signatures are unchanged. No unauthenticated public tool execution endpoint was added: executable tools still go through the existing permission-aware registrations.

## Implementation source map

- `McpRuntime.Operations.cs`: `add/remove/connect/disconnect`, overrides, and effective-definition reconciliation map to `packages/core/src/mcp/index.ts` (`reconcile`, `overrides`, `State.initial`, and service operations) plus `packages/core/src/config/plugin/mcp.ts` (`draft.get` precedence). Structural config comparison ignores JSON property order.
- The bounded, coalesced notification worker maps to `index.ts` `watch`/`whenLive` and `packages/core/src/tool/mcp.ts` refresh/reload. It uses the SDK completion task and list-change handlers, checks live connection identity, and processes close/tools/prompts/resources separately. Unexpected close clears all catalogs and instructions and settles `failed`. Tool refresh failure keeps previous tools and does not block prompt/resource refresh. Cancellation during lifecycle changes still flushes changed registry state before returning.
- `McpRuntime.cs` captured executable definitions now resolve the current connection at call time, matching `tool/mcp.ts` -> `Mcp.callTool` rather than retaining an obsolete SDK client after reconnect. Permission assertions precede lookup/execution. Corrected permission feedback and expected MCP failures become tool failures; user cancellation and permission declines retain their control flow. Execution timeout uses the live definition. Original server/tool names are used on the wire; normalized names remain the permission/registry identity.
- Catalog and instruction observations are deterministically server/name sorted. Transport setup, CodeMode query fallback, roots, deadlines, result/media conversion, and optional-catalog handling map to `packages/core/src/mcp/client.ts`. MCP initialization instructions remain real server-provided values, not generated defaults.

## Elicitation: Server Form owner handoff

`McpElicitation.cs` exposes this Location-scoped adapter; no new reference is needed:

```csharp
public interface IMcpElicitationForms
{
    Task<FormState> AskAsync(FormInfo form, CancellationToken ct);
    Task<bool> TryReplyAsync(FormId id, FormAnswer answer, CancellationToken ct);
}
```

- Supply the adapter as the optional fourth constructor argument: `new McpRuntime(directory, registry, permission, forms: adapter)`. The current `ToolLocationFactory` construction site must be wired by its owner. MCP does not create a parallel Form service, expose another form endpoint, or maintain a second registry of pending forms. The adapter instance must refer to the same Form service used by the host's Location-scoped Form endpoints and event feed.
- `AskAsync` receives a complete canonical Schema `FormInfo`, including a new `FormId`. Create it in the existing service, publish its normal `form.created` event, and wait for a terminal `FormAnsweredState` or `FormCancelledState`. Validate submitted answers through the Form service before settling; reject invalid answers without resolving the waiter. SDK callbacks can overlap, so the adapter must support concurrent requests.
- Cancellation must cancel the pending form, publish the normal cancellation event, and clean up the waiter before `AskAsync` completes. Do not return an invented answer on cancellation. MCP links the SDK request token to connection and Location lifetimes, checks it after the adapter returns, and propagates cancellation rather than converting it into acceptance. Disconnect/replacement/shutdown cancels the connection adapter before disposing the SDK client.
- `TryReplyAsync` validates and replies to a known form. Return `false` for not-found/already-settled only; other failures propagate. MCP invokes it with `{ elicitation: true }` on `notifications/elicitation/complete` for a matching URL request. The adapter should not create a form in this method. MCP owns only a per-connection elicitation-ID-to-Form-ID correlation map, removed when the request ends; stale/unknown completion notifications do nothing.
- All elicitation forms use `SessionId = "global"`, exactly as `mcp/index.ts` does. This is a string sentinel, **not** `OpenCode.Schema.SessionId`. Do not validate it as a persisted Session, insert a Session row, assign it an agent, or grant it Session permissions. Location identity comes from the injected service instance. Metadata contains `kind = "mcp-elicitation"`, `server`, `message`, and URL-mode `elicitationID`.
- Existing tool permission assertions remain before MCP tool execution. The source has no second `Permission.assert` for inbound elicitation, which can occur without a Session (including during connection). Do not trust inbound metadata to select a Session or bypass tool permissions. Form replies/user consent are handled by the authenticated host Form service, not an automatic MCP approval policy.
- URL mode produces one real `FormExternalField` with key `elicitation` and the server's URL. MCP does not launch a browser, fetch the URL, or forward credentials. The host/UI must present the server message and obtain consent before opening it. A valid answered external form becomes `{ action: "accept" }` with no form-answer content sent back.
- Form mode converts official SDK Boolean, number/integer, string, titled/untitled single-select, titled/untitled multiselect, and legacy `enumNames` schemas into canonical Form fields. It preserves required flags, bounds, supported formats, defaults, and option labels. Closed enums set `Custom = false`. Machine-generated titles use the source's description/key fallback. No nested-object fields or arbitrary roles are fabricated.
- Answered forms return `accept` with canonical primitive/string-array values. Cancelled/dismissed/rejected forms return `cancel`, matching TS; Schema Form has no separate declined terminal state. MCP does not invent a `FormDeclinedState` or silently turn errors into `decline`. A returned pending state is an adapter contract error. Malformed/unsupported requests fail with protocol errors, not empty forms. A **valid zero-field schema only** returns `accept` with `{}`, the explicit TS special case.
- Request and URL-completion handlers are installed in `McpClientOptions.Handlers` before SDK connection starts, so they are available during initialization. They never acquire the MCP lifecycle gate. Adapters must not synchronously reenter that gate or reacquire/initialize the runtime while answering an SDK callback. Register Form services before MCP and dispose them after MCP.
- Without an adapter, no elicitation handler/capability is advertised. The existing three-argument constructor remains supported but is not an operational Form integration. The Server/Tool composition change is required to enable the feature.

Source: `packages/core/src/mcp/client.ts` lines 190-206 (capabilities and handlers), `mcp/index.ts` lines 126-129 and 330-398 (Location ownership, form/URL flow and completion), lines 880-940 (field conversion), and `packages/core/src/form.ts` (ask cancellation and validated terminal replies).

Official SDK reference inspected: installed `ModelContextProtocol.Core` 2.2.0 `lib/net10.0/ModelContextProtocol.Core.xml`, especially `McpClientHandlers.ElicitationHandler`/`NotificationHandlers`, `ElicitationCapability`, `ElicitRequestParams` schema classes, `ElicitationCompleteNotificationParams`, and `ElicitResult`. The compiled implementation uses only public callbacks/types. The XML also lists an internal `ElicitResult.WithDefaults` method, which is not publicly callable; no call to it remains. Defaults are preserved in the Form descriptors and response conversion follows TS `mcp/index.ts`; no unsupported `applyDefaults` property is added to the SDK capability.

## SDK Verification

NuGet resolved `ModelContextProtocol` to stable version `2.2.0` on 2026-08-31. Added only that PackageReference to Core. The installed package's `lib/net10.0/ModelContextProtocol.Core.xml` documents the APIs used and the assembly compiles against this repository's .NET 11 preview 7 SDK.

The SDK stdio transport exposes command, arguments, cwd, environment inheritance/overrides, and bounded shutdown, but no process factory or injected .NET 11 process handle. This implementation uses its supported `System.Diagnostics.Process` ownership. It does not substitute Bun, a shell command wrapper, a hand-coded JSON transport, or a guessed new process API. A non-local execution plane requires another explicit supported transport and is not implemented.

Verification reference: `ModelContextProtocol.Core.xml` entries `StdioClientTransport.#ctor(StdioClientTransportOptions, ILoggerFactory)` and `StdioClientTransportOptions` (installed package lines 3815-4027). The public options have no launcher injection member. Process creation is dependency-owned; this project does not claim that the SDK uses the new .NET 11 process API.

## Remaining Boundaries

- OAuth persistence, public SDK authorization callbacks, noninteractive token refresh, and runtime credential-switch reconnect are implemented behind injected host services. See [OAUTH.md](OAUTH.md). Server must wire its existing channel store, credential notification publication, integration methods, attempt ownership, and callback listener. No browser or callback listener is started implicitly. Static OAuth client configuration does not manufacture a token.
- Catalog list-change and connection-close notifications are now processed without another observation. The host must bridge `Changed` to its bus; Core does not own that Server wiring. Resources/templates are cached until initial discovery or a list-change notification, unlike TS `resourceCatalog()` which lists on each call.
- Prompt, resource, and resource-template discovery failures independently yield empty catalogs and log a warning, without failing the connected server or removing its tools. Each catalog has its own deadline. Tool discovery failure fails initial connection; a later tool-list refresh failure retains the last successful tools, as in TS.
- Elicitation request/response conversion is implemented behind the injected adapter above; production Form service composition and UI behavior remain other owners' work. Server-side logging publication remains unimplemented. OAuth host/API composition is detailed in OAUTH.md; no console credentials are forwarded to MCP endpoints.
- Lifecycle and notification work uses the existing one-Location gate, not the TS per-server keyed locks. Initial handshakes are serialized and observation waits for them; slow discovery can delay other operations in that Location. The bounded notification worker does not create one task per notification. No claim of full lifecycle concurrency parity is made.
- SDK list pagination and schema validation remain SDK-owned; the TS duplicate-cursor check and targeted invalid-output-schema fallback are not separately implemented. Prompt/resource invocation failures still throw rather than return TS's optional undefined result.
- MCP config is already fully represented in `OpenCode.Schema/Mcp.cs`; no schema or instruction-owner files were edited.

## Verification

Finish-pass verification used only the pinned local SDK:

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Core\OpenCode.Core.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass -p:NuGetAudit=false -v:minimal
.\.dotnet\dotnet.exe build src\OpenCode.Core\OpenCode.Core.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -v:minimal
```

The first full Core build was blocked by five concurrently incomplete CodeMode symbols outside this subtree. The final full Core build succeeded with **0 warnings and 0 errors** after that owner's files became available. This was not a changed-files-only compilation. No tests were added, edited, or run. No app, MCP server/network call, live configuration evaluation, database access, or process-control operation was run. Only `src/OpenCode.Core/Mcp` source/documentation files were edited. No shared project references were changed and no commit was made.

The subsequent elicitation pass also uses the same isolated full Core build with `--no-restore`; no dependency changes or network access are needed. Runtime behavior has not been exercised under the build-only constraint.
