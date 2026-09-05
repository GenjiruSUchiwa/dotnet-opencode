# Network surface pass — frozen handoff

## Outcome

Client and Server owned-surface diagnostics are clear in the final builds. The
baseline `provider-identity-build.log` contained **129 distinct diagnostics** in
these surfaces, excluding the separately owned `ServerHost.cs`:

| Rule | Baseline |
| --- | ---: |
| MA0004 | 48 |
| MA0009 | 11 |
| MA0011 | 1 |
| MA0015 | 52 |
| MA0023 | 2 |
| MA0040 | 7 |
| MA0042 | 3 |
| MA0099 | 4 |
| MA0166 | 1 |

No rule was disabled, no project/global analyzer configuration was edited, and no
generated asset was changed. The existing versioned message/event contracts and
`OpenApiGenerateDocuments=false` remain intact. `ServerHost.cs`, Core,
Schema/Protocol, SDK, and tests were not edited by this pass.

## Analyzer and ownership changes

- Client continuations and async disposal explicitly use `ConfigureAwait(false)`.
  Stream/enumerator disposal remains inside response and linked-token lifetimes.
  SSE cancellation and PTY write serialization are unchanged. No reconnect, retry,
  provider switch, eager buffering, or new HttpClient ownership policy was added.
- Argument validation identifies the correct input parameter, including nullable-ID
  pattern variables. Server `RequestArgumentException` preserves the existing public
  message without appending CLR parameter names to HTTP error payloads.
- Regex consumers use non-backtracking matching where compatible. Startup diagnostic
  extraction retains its timeout and uses a named capture. No arbitrary timeout was
  added to public parsing, and accepted cursor/radix alphabets remain unchanged.
- Schema JSON Pointer indices use invariant integer parsing. Enum bit tests use
  explicit enum values/HasFlag. The parameterless event feed uses TimeProvider.System
  explicitly, without changing injected clocks.
- Cooperative socket cancellation is awaited. Standalone storage connection disposal
  is async. Bounded/owned finalization deliberately passes `CancellationToken.None`
  where the existing contract requires settlement after HTTP/host cancellation:
  inbox mutation, execution idle settlement, shell maintenance teardown, and callback
  completion cancellation. Request tokens were not substituted into those boundaries.

## Confirmed upstream parity work

Inspected `packages/protocol/src/groups/{integration,websearch,plugin}.ts`,
`packages/server/src/handlers/{integration,websearch,event}.ts`, the native
Wellknown/WebSearch handoffs and implementations, and native plugin/Location
composition. SSE already preserves the source connected frame, 15-second heartbeat,
headers, serialized writes, backlog-overflow failure, and cancellation ownership;
this pass did not replace it with a different transport or replay mechanism.

### Wellknown: real handler replaces the stub

- `AddIntegrationServices()` now includes `AddWellknownDiscovery()` over the existing
  channel services; no second credential/database store is introduced.
- `MapIntegrationEndpoints()` now mounts `MapWellknownEndpoints()` exactly once
  instead of the 503 stub. The success callback acquires/reloads the same native
  Integration Location after the discovery/source-store commit.
- This is the existing `POST /api/experimental/integration/wellknown` operation,
  with the existing payload, Location query, 204 response, and sanitized discovery
  failure boundary. No list/remove/authentication endpoint was invented.
- OpenAPI metadata now describes that real handler, not an always-unavailable stub.

The existing Core restrictions remain: manifest `auth.command` is metadata, not
executable registration; downloaded JavaScript plugins are not loaded; remote
configuration is not automatically merged into all config consumers. Reload covers
compatible native contributions only. A post-commit reload failure does not roll
back the saved origin. This pass does not claim otherwise. Existing typed Client
Wellknown methods already match the request/204 contract and were retained.

### WebSearch: shared native backend binding ready for parent mounting

New `Services/WebSearchHostService.cs` contains:

- `WebSearchPluginSource`, an `INativePluginSource` that initializes actual Exa,
  Firecrawl, Parallel, and Tavily implementations in source order. Providers and
  their runtime registrations belong to real native plugin scopes and are removed
  on scope disposal. Definitions alone do not create a live inventory.
- One runtime for that Location's provider generation, using the existing channel
  `WebSearchSelectionStore`. Registered provider methods are projected into the
  existing Integration runtime; no replacement Integration catalog is constructed.
- `WebSearchLocationSource`, which borrows the authoritative tool/plugin Location
  through existing Command readiness, retains configured/discovered-JS guards,
  applies the existing effective websearch config, and releases incomplete
  acquisitions on cancellation/failure. Endpoints do not allocate per-request
  provider transports or a second tool registry.
- `AddNativeWebSearch()`, the composition extension for the parent to install.

Provider constructors receive `OpenCodeChannel.UserAgent`. The existing
`ProviderUserAgent.Apply` remains untouched: external providers retain the parent's
`dotnet-opencode` product identity. Source `websearch.updated` notifications contain
only `{}` and Location context, not credentials or queries.

The existing two handlers and typed Client methods are retained. Missing binding,
missing backend, or missing credential does not become list-success/no-results.
Queries retain explicit provider selection, cancellation, no retries, existing
fixed destinations, and error sanitization. OpenAPI sidecar bindings now include
`v2.websearch.providers` and `v2.websearch.query`. Optional request/result fields
use real JsonTypeInfo metadata without invoking omit-only codecs with null defaults.
Runtime documentation still filters against registered routes, so these bindings
do not advertise unmounted endpoints.

## Parent / EF composition handoff — integrated

The integration owner applied these two additions in
`src/OpenCode.Server/ServerHost.cs` after the EF owner froze that file. The existing
`OpenCode.Server.Services` and endpoint imports expose both extensions. Each is
registered once:

```diff
         builder.Services.AddIntegrationServices();
+        builder.Services.AddNativeWebSearch();
```

```diff
         app.MapIntegrationEndpoints();
+        app.MapWebSearchEndpoints();
```

No additional Wellknown mapping is needed: `MapIntegrationEndpoints()` now owns it.
Do not also call `MapWellknownEndpoints()` from ServerHost. The existing native
plugin source enumeration in `LocalToolOptionsFor` supplies the WebSearch plugin
definitions after `AddNativeWebSearch()` is registered.

The source now registers the shared WebSearch composition and maps both HTTP
routes. This is a source registration result, not an executed host/endpoint check.

## Remaining gaps / boundaries

- WebSearch tool-model visibility and refresh are not connected here. The existing
  native WebSearchTool requires the factory's real permission/Form context and a
  stable eligibility refresh on credential/config changes. That is Core/factory
  work; HTTP provider composition is not a substitute or a claim that the tool is
  advertised to models.
- Full source plugin execution, dynamic plugin transforms, and external JS/TS
  compatibility remain outside this native binding. No package execution or silent
  C# replacement of configured plugin declarations was introduced.
- Wellknown config-source consumer integration and compatible manifest-auth method
  registration remain separate work. Core security/isolation restrictions are kept.
- Native backend keyless fallback remains unsupported by design. Provider inventory
  is not a backend health claim. No live interoperability was checked.
- Explicit-workspace filesystem routing and the existing legacy database-upgrade
  response remain outside this network-only ownership. Schema/Protocol were not
  changed; no contract-worker handoff is required for these implemented routes.
- OpenAPI generation remains authored runtime code only; it was not invoked or
  used to infer parity from a route count.

## Exact files changed in this pass

Paths below are relative to `src/` unless otherwise stated.

**Client:**

- `OpenCode.Client/ApiHttpClient.cs`
- `OpenCode.Client/PtyConnection.cs`
- `OpenCode.Client/ServiceDaemon.cs`
- `OpenCode.Client/ServiceDeployment.cs`
- `OpenCode.Client/ServiceStartupDiagnostics.cs`
- `OpenCode.Client/SessionHttpClient.cs`
- `OpenCode.Client/SessionHttpClient.Commands.cs`
- `OpenCode.Client/SessionHttpClient.Config.cs`
- `OpenCode.Client/SessionHttpClient.Context.cs`
- `OpenCode.Client/SessionHttpClient.Events.cs`
- `OpenCode.Client/SessionHttpClient.Fork.cs`
- `OpenCode.Client/SessionHttpClient.Forms.cs`
- `OpenCode.Client/SessionHttpClient.PersistentPty.cs`
- `OpenCode.Client/SessionHttpClient.Pty.cs`
- `OpenCode.Client/SessionHttpClient.Skills.cs`
- `OpenCode.Client/SessionHttpClient.Stats.cs`

**Server:**

- `OpenCode.Server/Documentation/ContractCatalog.cs`
- `OpenCode.Server/Documentation/NativeSchemaExporter.cs`
- `OpenCode.Server/Documentation/OpenApiDocument.cs`
- `OpenCode.Server/Documentation/SchemaReferences.cs`
- `OpenCode.Server/Endpoints/CredentialEndpoints.cs`
- `OpenCode.Server/Endpoints/FeatureEndpoints.cs`
- `OpenCode.Server/Endpoints/FilesystemLocations.cs`
- `OpenCode.Server/Endpoints/IntegrationEndpoints.cs`
- `OpenCode.Server/Endpoints/LocationEndpoints.cs`
- `OpenCode.Server/Endpoints/PermissionEndpoints.cs`
- `OpenCode.Server/Endpoints/PtyEndpoints.cs`
- `OpenCode.Server/Endpoints/RequestArgumentException.cs` (new)
- `OpenCode.Server/Endpoints/RequestLocation.cs`
- `OpenCode.Server/Endpoints/SessionArchiveEndpoints.cs`
- `OpenCode.Server/Endpoints/SessionEndpoints.cs`
- `OpenCode.Server/Endpoints/SessionQueryParameters.cs`
- `OpenCode.Server/Endpoints/SessionSkillEndpoints.cs`
- `OpenCode.Server/Endpoints/SessionStatsEndpoints.cs`
- `OpenCode.Server/Endpoints/ShellEndpoints.cs`
- `OpenCode.Server/Endpoints/VcsEndpoints.cs`
- `OpenCode.Server/Endpoints/WebSearchEndpoints.cs`
- `OpenCode.Server/Hosting/StandaloneLifetime.cs`
- `OpenCode.Server/Integrations/IntegrationCallbackListener.cs`
- `OpenCode.Server/Integrations/IntegrationHostService.cs`
- `OpenCode.Server/Pty/PtyHostServices.cs`
- `OpenCode.Server/Pty/PtyRequestPolicy.cs`
- `OpenCode.Server/Pty/PtyWebSocket.cs`
- `OpenCode.Server/Services/EventFeedService.cs`
- `OpenCode.Server/Services/SessionExecutionService.cs`
- `OpenCode.Server/Services/WebSearchHostService.cs` (new)
- `OpenCode.Server/Shell/ShellLocationServices.cs`
- `OpenCode.Server/StartupDiagnostics.cs`

**Report:** `docs/network-surface-pass.md` (new).

## Build-only verification

Pinned executable: `.dotnet/dotnet.exe`; SDK `11.0.100-preview.7.26381.103`.
Every build used a unique `C:/tmp/opencode/network-pass-<guid>` directory,
`--disable-build-servers`, and `-p:OpenApiGenerateDocuments=false`.

Final artifacts/logs:

`C:/tmp/opencode/network-pass-e315aade9431421f9955debad781f99f/`

| Build | Errors | Owned-surface warnings | Dependency warnings |
| --- | ---: | ---: | ---: |
| Server (`server-build.log`) | 0 | 0 | 40 |
| Client (`client-build.log`) | 0 | 0 | 3 |

The Server build's 40 warnings are in dependency projects, not Server/Client. The
Client build's 3 warnings are in Schema. These are snapshots of concurrently edited
dependencies, not a claim that the whole graph is warning-free. Earlier artifacts
are under `network-pass-bd25150172d544b0b00b992c66efc7be`.

No tests were added, edited, or run. No application, Server/Client/SDK runtime, DI
startup, DB/SQL/migration, auth command, provider/MCP/PTY/native code, network probe,
or documentation endpoint was executed. No production data/credentials were read.
No Git operations, commits, publishing, installs, or delegation. Parent owns merge,
CI, push, and release. Owned source is frozen for integration.

## Pass 2 — frozen shared-tool glue and contract-preserving fixes

This section supersedes the earlier mounting/tool-binding handoff status. Parent
integrated pass 1 (`9c7bbc6`) and mounted `AddNativeWebSearch()` and
`MapWebSearchEndpoints()` in `ServerHost.cs` (`2080d27`). Source inspection confirms
both registrations are present. This is a registration claim, not runtime verification.
Wellknown is still mapped exactly once by `IntegrationEndpoints`; no second mapping
was added in ServerHost.

### Completed WebSearch tool handoff

The Core tools worker completed `LocalToolOptions.WebSearchReady` and
`WebSearchToolBinding`. This pass applied exactly its requested Server glue:

```csharp
PluginChanged: id => OpenCode.Server.Plugins.NativePluginComposition.Publish(services, location, id),
WebSearchReady: ct => services.GetRequiredService<WebSearchPluginSource>().ReadyAsync(location, ct));
```

`WebSearchPluginSource.ReadyAsync(LocationInfo, CancellationToken)` is now public
and documented as a **borrowed-runtime accessor**. It returns the actual initialized
native generation without acquiring a Location or constructing any providers. Its
caller must retain the real Location lease, or call it during factory initialization
after backend activation. The Core binding uses actual permission/Form/clock services
and refreshes eligibility at acquisition/model-snapshot boundaries; that implementation
was not edited here.

Do not substitute `IWebSearchLocationSource.AcquireAsync`: it enters
CommandHostService and reacquires ToolLocationFactory, recursively entering the
Location being constructed. This glue introduces no such acquisition and no second
backend/runtime/transport set.

Two accessor corrections support the shared binding:

- Recheck entry identity after awaited configuration loading. If the entry closed
  or was replaced while acquisition waited, return unavailable rather than handing
  out the old generation. The caller still owns the enclosing Location/readiness
  boundary; this is not a new cluster lease or plugin scheduler.
- Apply `WebSearchRuntime.Configure` only when effective selection changes (or on
  first configuration). It publishes `websearch.updated`; unconditional reapplication
  on every HTTP/tool snapshot would generate spurious observer refreshes. This
  does not cache credential eligibility or bypass the Core worker's refresh checks.

Configured/discovered JS guards, native keyless restrictions, exact-integration
credential lookup, plugin disposal ownership, and parent User-Agent branding remain.

### Other confirmed existing-contract fixes

1. **Client fs.read path encoding.** Upstream generated `encodePath` in
   `packages/client/src/promise/generated/client.ts` splits on `/`, encodes each
   segment, and rejoins with `/`. The native client now does the same instead of
   converting backslashes and encoding the entire path as one segment. A literal
   backslash in a Unix server filename is no longer changed into a directory
   separator based on client assumptions. Raw bytes, HTTP cancellation, and response
   ownership are unchanged.
2. **Compaction admission and advisory wake.** Source
   `packages/core/src/session/session.ts:Session.compact` checks the Session, admits
   the control, and then wakes; it does not require a ready model before admission.
   Removed the endpoint's premature `RequireReady()` check. The Server's advisory
   `WakeAsync` now selects Schedule's recording-ready boundary rather than its model
   readiness boundary. Explicit Resume still requires execution readiness. Host-owned
   scheduling/settlement, admission cancellation, and Core attempt/claim policies
   are unchanged. A provider/tool resolution failure belongs to the independently
   owned drain, not a pre-admission refusal of otherwise valid compaction work.

No HTTP route, payload, response, or Schema/Protocol shape was added or changed.
Reviewed filesystem, message pagination, global/session generation, Session admission,
and WebSearch surfaces; no unsupported handler was converted into an empty success.

### Remaining parent/domain work

- Global `POST /api/generate` (`v2.generate.text`) remains distinct from existing
  Session-context generation. Upstream uses a stateless Generate service in the base
  config Location. No matching native Core stateless operation or shared typed
  payload was found; this pass did not create a parallel Server model loop or guess
  a contract. Coordinate Core plus parent/Schema owner agreement before implementing
  that missing surface.
- Full Wellknown configuration consumption and compatible external plugin execution
  stay with their Core owners. This pass did not execute or register manifest auth
  commands as a shortcut.
- Dynamic plugin replacement beyond existing native Location/readiness ownership
  remains the plugin host's responsibility. No independent runtime was introduced
  for requests or model tools.

### Exact pass-2 files changed

- `src/OpenCode.Server/ServerHost.cs` — requested WebSearchReady callback only.
- `src/OpenCode.Server/Services/WebSearchHostService.cs` — documented public borrowed
  accessor, generation identity check, and unchanged-config notification suppression.
- `src/OpenCode.Server/Services/SessionExecutionService.cs` — advisory wake uses the
  recording-ready scheduling boundary.
- `src/OpenCode.Server/Endpoints/SessionEndpoints.cs` — remove pre-admission model
  readiness check from compaction.
- `src/OpenCode.Client/SessionHttpClient.Files.cs` — source path-segment encoding.
- `docs/network-surface-pass.md` — this append-only handoff.

No Core/Schema/Protocol/project/global/generated/test files were changed by this pass.

### Build status and blocking dependency

Both build attempts used the pinned `.dotnet/dotnet.exe` SDK
`11.0.100-preview.7.26381.103`, unique artifacts, `--disable-build-servers`, and
`-p:OpenApiGenerateDocuments=false`.

1. `C:/tmp/opencode/network-pass-136fc7001b614bcc8b4be0443f6652d8/`
   - Server passed with **0 warnings / 0 errors**, before the final WebSearchReady
     ServerHost line was added.
   - Client encountered a concurrently introduced Schema duplicate-type error.
2. `C:/tmp/opencode/network-pass-28ed8fcc1a5c497ca6e4a8bb489a80e9/`
   - Final Server and Client builds both stop in Schema with **2 errors / 0 warnings**.
   - `SessionIdleEventData` is declared in both
     `src/OpenCode.Schema/ClientEventDefinitions.cs:45` and
     `src/OpenCode.Schema/SessionEvent.cs:58` (`CS0101`, `CS8863`).
   - These files belong to the Schema worker and were not edited. Parent should
     reconcile the canonical definition, then rebuild Server and Client. The final
     glue is source-complete but is **not claimed as build-verified past this blocker**.

Logs are `server-build.log` and `client-build.log` in each artifact root. No owned
warnings were reported; the final dependency failure prevents a complete final
owned-compilation result. No diagnostic suppression was added.

Pass 2 is frozen for parent integration. No tests, app/Server/Client/SDK execution,
DI startup, plugin loading, DB/SQL/migration, provider/MCP/PTY/native execution, or
production data access occurred. No Git/push/install/publication or delegation.

## Pass 3 — metadata checkpoint frozen; generation handoff pending

Pass 2 was integrated by parent as `d96f512`. The shared WebSearchReady callback
remains active in source. This checkpoint changes only
`src/OpenCode.Server/Documentation/NativeSchemaExporter.cs` and this report.
No canonical document/hash, generated asset, Schema/Protocol, Core, project/global
configuration, or test file was edited.

### Completed schema/Protocol-owner exporter handoff

Read the actual converter and serialization callbacks referenced in
`docs/schema-protocol-pass.md:92–113`. The exporter now handles these constraints
explicitly rather than inferring unconstrained CLR fields:

- `TuiToastEventJsonConverter` / `TuiToastShowEventData`: required string message,
  required variant from the actual `TuiToastVariant` enum codec (info, success,
  warning, error), optional string title, and positive-integer duration. The emitted
  object requires duration because the encoder always writes it. Its schema carries
  the default annotation 5000, `x-native-decoding-optional: true`, and an explicit
  description of the decoding-only default. This does not make emitted duration
  optional or change the converter/constructor.
- `ServiceHealthResponse`: healthy is literal true; version is required string;
  pid is a required nonnegative Int32-range integer. Existing richer native health
  state/build/channel metadata remains unchanged.
- `PermissionSource`: type is literal tool; messageID and id remain required
  strings, without invented ID-prefix restrictions.
- `PluginInfo`: separate active/failed schema branches. Both require source/status/
  tui; active additionally requires id and failed requires error. Parent review
  aligned this emitted-object schema with the subsequently added union converter:
  active has no error field; failed permits an optional id.
- `WorktreeErrorResponse`: name is literal WorktreeError and data is required.
  WorktreeErrorData uses actual JsonTypeInfo metadata so its optional forceRequired
  codec is not invoked with a null constructor-default sentinel.
- `InstructionEntryInfo` and `InstructionEntrySnapshot`: key matches the callback's
  `^[a-z0-9][a-z0-9._-]*$` pattern. Value is required JSON, including JSON null; the
  snapshot additionally requires boolean removed. The JSON-value schema is
  intentionally unconstrained JSON, not a fallback for an unknown typed codec.

Mappings are in `Custom`, so they apply to nested occurrences as well as directly
requested roots. No unsupported codec fallback was added. These are authored
exporter rules; no exporter, serializer, callback, or `/openapi.json` invocation was
used to verify them. The schemas describe emitted objects while explicitly noting
toast's different decoding requirement.

### Global generation: inspected, not wired before owner handoff

Read upstream `packages/protocol/src/groups/generate.ts`,
`packages/server/src/handlers/generate.ts`, and `packages/core/src/generate.ts`.
The existing route is global `POST /api/generate` with `{ prompt, model? }` and
`{ data: { text } }`, not a Session prompt or Session-context generation. Source
uses the base configuration Location, plugin readiness, and Core Generate error
mapping. It permits a real empty text result; no fake Session should be created.

The contract worker has authored `GenerateTextInput`, `GenerateTextResult`,
`GenerateTextResponse`, and `GenerateProtocolJsonContext`. A Core GenerateService
implementation is also visible, but no explicit foundation/parent service handoff
was received before this checkpoint. Per instruction, no endpoint, Client method,
DI registration, or model-execution glue was added against an in-progress service.
Parent should supply its final constructor, TextAsync signature, error contracts,
and readiness ownership before this pass resumes generation integration. Existing
Session generation, detached managed server ownership, and Generic Host architecture
were not changed.

### Exact build evidence

Pinned repository `.dotnet/dotnet.exe`, SDK `11.0.100-preview.7.26381.103`, unique
artifacts, `--disable-build-servers`, and `-p:OpenApiGenerateDocuments=false`:

- First build: `C:/tmp/opencode/network-pass-c376bae6f8fa4b4d98d18f83076c878f/`.
  Blocked by four concurrent Core references to missing SessionText in compaction/
  title services. Those files were not changed here.
- Final build: `C:/tmp/opencode/network-pass-888127bdde2c4c8fb97688892efc849a/`.
  Server and its dependency graph passed with **0 warnings and 0 errors**.
  Evidence: `server-build.log`. Client source was unchanged in this checkpoint.

Metadata checkpoint is frozen for parent integration; global generation remains
explicitly pending. No tests, runtime/DI/SDK/app/model/DB/SQL/migration/native/
provider/MCP/PTY execution, network verification, production data access, Git,
install, publishing, or delegation occurred. No runtime parity claim is made.

## Focused integration HTTP 503 — diagnostic checkpoint

Observed user symptom: the selected local .NET service returns 503 for
`GET /api/integration?location[directory]=C:\Repos\Hona\opencode-dotnet`.
The response body and an authenticated integration probe were not available to this
pass. **The observed failure's root cause is not yet confirmed.** No 503 was changed
to 200, no empty catalog was substituted, and no readiness/config failure was ignored.

### Source trace

1. Server readiness middleware can return 503 before endpoint execution.
2. IntegrationEndpoints resolves the request Location, then calls
   IntegrationHostService.AcquireAsync with observation enabled. Its filter maps
   NotSupportedException and CatalogLocationUnavailableException to 503.
3. Acquisition enters the authoritative ToolLocationFactory. LocalToolOptionsFor
   resolves LoadReadInstructions, ripgrep, Forms, MCP OAuth, Shell/Subagent jobs,
   and native plugins. A missing/invalid ripgrep executable is an explicit
   NotSupportedException and therefore a concrete possible 503 source. Native
   catalog access currently requires this tool dependency too.
4. The factory initializes backend plugins before WebSearchToolBinding.RefreshAsync.
   Its WebSearchReady callback reads the existing WebSearchPluginSource directly;
   it does not recursively acquire CommandHostService/the factory. Forms.ForLocation
   similarly does not reenter Location acquisition. The inspected registrations for
   these services and shared job instances are present; no missing DI registration
   was established from source.
5. After factory acquisition, ReadMcpConfiguration calls
   ProducerConfiguration.RequireNoPluginSources. Configured/discovered JS plugin
   sources produce explicit NotSupportedException even before MCP observation.
   Those guards remain intact; this pass did not inspect or dump user configuration.
6. ObserveAsync initializes/reconciles configured MCP servers. Only afterward does
   IntegrationProviders read their real OAuth registrations and combine native
   providers, WebSearch integrations, and registered command methods. ListAsync
   projects these definitions with the channel credential store.

The upstream integration handler reads its Location's Integration service. Native
provider definition creation does not call provider login/model endpoints. However,
the existing native list acquisition explicitly observes MCP: an integration GET is
not a pure health probe and can initialize configured plugins/MCP/processes. No such
request or provider network call was made for this investigation.

### Authored diagnostic change

Only `src/OpenCode.Server/Integrations/IntegrationHostService.cs` changed, plus this
report. Acquisition now logs a fixed stage and CLR exception type before rethrowing:
`tool-location`, `integration-location`, `mcp-configuration`, `mcp-observation`, or
`integration-catalog`. Normal caller cancellation is not logged as a failure. The
log intentionally excludes exception text, request values, configuration, URLs,
credentials, and successful catalog contents. Incomplete acquisitions still release
the borrowed tool Location. Existing public status/body mapping remains unchanged.

This is diagnostic instrumentation, **not a claimed fix of the user's 503**.
Removing ripgrep requirements, changing MCP initialization, or bypassing config guards
without identifying the actual branch would be speculative and was not done.

### Exact diagnostic permission/evidence needed from parent

First preference: the already-observed failing response's `_tag`, `service`, and
sanitized `message`, without headers/authentication or a config/credential dump.
These distinguish a pre-ready host response from an Integration capability/config
failure. If the existing response cannot be recovered, authorize **one authenticated
GET of that exact Integration URL against the verified registered .NET instance**,
explicitly acknowledging that it may initialize MCP/plugins. Prior health-only
permission does not authorize it. Keep the credential in memory and never display it.
On success report status only; do not dump connection/credential inventory.

If the response message is insufficient, the new stage-only warning requires an
explicitly authorized deployment/restart to be loaded; this pass did not stop or
replace any process. Old PID 27896 was not touched. Parent can then supply the stage
and exception type for the same single authorized request.

### Build / freeze

Pinned SDK `11.0.100-preview.7.26381.103`, repository `.dotnet/dotnet.exe`,
`OpenApiGenerateDocuments=false`. Server and dependency graph passed with
**0 warnings / 0 errors**.

Evidence: `C:/tmp/opencode/integration-503-f52e4d13641646f5809fa294c9082fb1/server-build.log`.

Frozen pending the diagnostic permission/evidence above. No tests, app/TUI/DI startup,
integration/health request, provider/MCP/DB/SQL/native/PTY runtime, production data,
credential/config dump, service mutation, Git operation, or delegation occurred.
Persistent server/UI separation and strict local-build negotiation remain unchanged.
