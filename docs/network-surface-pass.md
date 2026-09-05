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
