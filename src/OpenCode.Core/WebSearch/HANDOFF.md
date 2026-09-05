# Web search — real backend and host composition

## Scope

This batch adds `Core/WebSearch`, `Tools/Builtins/WebSearchTool.cs`, `Server/Endpoints/WebSearchEndpoints.cs`, and `Client/SessionHttpClient.WebSearch.cs`. The existing narrow `Schema/WebSearch.cs` contract was completed with Provider/Input and the required result `time` object. No factory, MainServer, plugin-host, root/TUI, existing integration service, or project file was edited. ToolViews and completed UI features remain unchanged.

The code is implemented but **not automatically installed by this batch**. The assigned MainServer/plugin/factory owner must attach it to the existing Location lifecycle. A missing attachment is an explicit unavailable response, never an empty success.

## Source contracts

Read implementations:

- `packages/core/src/websearch.ts`
- `packages/core/src/tool/plugin/websearch.ts`
- `packages/core/src/plugin/websearch/{exa,parallel,firecrawl,tavily,mcp}.ts`
- `packages/core/src/config/plugin/websearch.ts`
- `packages/core/src/integration.ts` (`resolveConnections`, active/resolve)
- `packages/schema/src/{websearch,config/websearch}.ts`
- `packages/plugin/src/effect/websearch.ts`
- `packages/protocol/src/groups/websearch.ts`
- `packages/server/src/handlers/{websearch,plugin-readiness}.ts`

Only public routes:

| Method/path | Input/output |
| --- | --- |
| `GET /api/websearch/provider` | `LocationResponse<IReadOnlyList<WebSearchProvider>>` |
| `POST /api/websearch` | `{ query, providerID? }` → `LocationResponse<WebSearchResponse>` |

Both use the existing `location[directory]` / `location[workspace]` query convention. There is no public default/select/credential endpoint in this group. Provider IDs are source branded strings, not invented prefix IDs. `results[].time` is required, even when empty; `published` is an optional finite Unix-millisecond number. Optional strings/numbers omit null on encoding and reject explicit null as in the schema. Genuine decoded empty result arrays are allowed; missing/malformed response data is not converted to one.

Client methods are `ListWebSearchProvidersAsync(...)` and `WebSearchAsync(WebSearchInput, ...)`. They use the existing authenticated `SessionHttpClient`, preserve errors, and never retry or switch providers. Client depends only on Schema/Protocol, not Core.

## Supported native backend contracts

Create `NativeWebSearchProvider` only for backends actually enabled by the host composition. Unsupported IDs throw unavailable; no generic endpoint override exists.

| ID | Fixed destination | Source request | Credential destination |
| --- | --- | --- | --- |
| `exa` | `https://mcp.exa.ai/mcp` | MCP `web_search_exa`, `{ query, numResults: 8 }` | `exaApiKey` query parameter; only Exa key |
| `parallel` | `https://search.parallel.ai/mcp` | MCP `web_search`, `{ objective: query, search_queries: [query] }` | Bearer Parallel key |
| `firecrawl` | `https://mcp.firecrawl.dev/v2/mcp` | MCP `firecrawl_search`, `{ query, limit: 8 }` | Bearer Firecrawl key |
| `tavily` | `https://api.tavily.com/search` | `{ query, search_depth: "basic", chunks_per_source: 3, max_results: 8 }` | Bearer Tavily key; source `X-Client-Name: opencode2` |

MCP calls are the source's single stateless JSON-RPC POST (`jsonrpc: "2.0"`, `id: 1`, `method: "tools/call"`). They are **not** new generic MCP server registrations or SDK handshakes. Responses accept direct JSON or the source `data: ` JSON SSE payload format. There is no general stream/event-feed subscription.

All requests have source 25-second cancellation. MCP uses the source 256 KiB response cap; the native Tavily adapter also uses this cap instead of unbounded buffering. A cap failure is a request error, never truncated search results. No automatic retries, redirects, cookies, browser solving, scraper, arbitrary URL, or custom authentication handler is used. The native provider owns a clean HttpClient and disposes it with its Location/plugin lifetime.

The host supplies its actual App User-Agent string (`opencode/{channel}/{version}/{name}`); no model input controls it. Exa uses the source behavior without adding this header. Backend response errors retain only provider ID/status/safe messages, not response bodies or credential-bearing request exceptions. This is particularly important for Exa's source query-string credential.

### Credential resolution and intentional restrictions

Pass the **existing channel `CredentialStore`**. The resolver reads `GetActiveCredentialAsync(providerID)`, preserving the store's source ordering. Only a `CredentialKey` for exactly that integration is usable. If no saved connection exists, it reads only that backend's source environment variable: `EXA_API_KEY`, `PARALLEL_API_KEY`, `FIRECRAWL_API_KEY`, or `TAVILY_API_KEY`. The optional environment accessor must expose the actual authorized host environment, not a copied production config or credential inventory.

A selected stored non-key/empty credential does not fall through to a different credential or environment identity. OAuth/Console/OpenAI/MCP tokens are not refreshed or forwarded. Arbitrary saved metadata/configuration cannot change the backend URL. The provider's `Integration` property returns actual key/env method definitions for composition into the existing IntegrationRuntime catalog; it performs no registration/write itself. Do not replace the whole integration catalog with only these entries. The current integration UI's environment-connection projection remains that owner's responsibility.

**Requested restriction:** TS adapters can attempt keyless access, but this native batch does not. Missing backend/credential is unavailable. In particular, no Tavily `X-Tavily-Access-Mode: keyless` fallback is sent. A valid HTTP response that explicitly contains an empty results array may produce the source no-results text; unavailable/error/malformed payloads may not.

Firecrawl `success: false`, MCP `isError: true`, absent MCP result, and unparseable Exa text fail explicitly. Exa's source URL/Title/Published/Highlights/Text block parser is used; a nonempty text payload with no source-format URL records is considered unsupported response data, not proof of no results. Published date strings use .NET invariant DateTimeOffset parsing and invalid dates are omitted; no publication date is invented.

## Location runtime and selection

Construct one `WebSearchRuntime` in the real Location scope. `Register(IEnumerable<IWebSearchProvider>)` adds real complete provider implementations and returns an idempotent disposable registration. Later active entries for an ID win; disposal reveals earlier entries. It does not create an alternative tool executor type or attach anything to a process-global tool registry.

Apply the latest effective `OpenCodeConfiguration.WebSearch` with `runtime.Configure(...)` from the existing config observer. Config selection overrides stored selection, including disabled. No config loader or second observer is constructed here.

For source interactive selection, inject `WebSearchSelectionStore(existingChannelDatabase)`, which implements `IWebSearchSelectionStore` over the existing `kv` table/key **`websearch:provider`**. It writes the source string or `false` JSON, awaits commit, and has no memory fallback. Source invalid stored values are removed. This is a preference, not a Session event, inbox item, or execution claim. It requires no migration/new table/file. The broader generic KV service does not currently exist in this port; this adapter is deliberately limited to this source preference key.

`DefaultAsync` follows source disabled/provider/random behavior. Random chooses among registered implementations; it does not silently reroute an uncredentialed selected backend. Register only the host-supported set, or select an explicit credentialed default. An explicit query `providerID` overrides the default, including disabled, as in the source API. An unknown explicit ID is provider-not-found; an unresolved default is provider-required.

The optional `updated` callback is the host boundary for source `websearch.updated` notification/catalog refresh. It must be a safe synchronous notification/scheduling callback, not async work under a registration mutation. This batch does not add a second event bus or edit global event inventories. Credential/config changes and stored preference changes must cause the owner to refresh the tool's eligibility before subsequent model snapshots.

## Tool registration — factory owner

Acquire/capture the real Location `WebSearchRuntime`, existing `IToolPermission`, and optional existing Location `FormService` during producer composition:

```csharp
var producer = new WebSearchTool(websearch, permission, forms);
var definition = await producer.CreateIfAvailableAsync(ct);
// Inside the existing native plugin scope, not a second ToolRegistry:
scope.TransformTools(draft =>
{
    if (definition is not null) draft.Add(definition);
});
```

Use source plugin identity `opencode.tool.websearch` and `CodeMode: false` from the returned complete ToolInfo. The actual native backend plugin identities are `opencode.websearch.exa`, `.parallel`, `.firecrawl`, and `.tavily`. `NativeWebSearchProvider` implements `IAsyncDisposable`, so `scope.Own(provider)` can retain its transport. Retain/dispose its runtime provider registration in the same scope (for example with the existing `NativePluginRegistration(registration.Dispose)` resource wrapper).

For reload, compute eligibility asynchronously **before** replay, replace the producer-owned captured definition, and reload the same stable tool transform. Do not run credential reads inside a synchronous draft transform, append a new transform on every credential event, or override later plugins. The above sample's single captured definition must be refreshed by the owner; it is not a permanent availability cache.

`CreateIfAvailableAsync` returns no tool for disabled/missing credential/backend. With no selected default, it requires both the actual Forms service and selection persistence plus an available backend. A selected, credentialed configured provider works without Forms/KV. Construction of `ToolInfo` validates the supported input/output schemas. No unconditional always-present websearch stub is registered.

Every execution still asserts permission `websearch` with resources `[query]`, save `["*"]`, metadata `{query}`, and the invocation's canonical Session/Agent/Message/Call context **before** forms or requests. Permission decline and caller interruption propagate; policy block/correction retain the existing leaf handling. Visibility is not execution authorization.

When selection is required, the source two-stage Forms flow is real: allow/choose/disable, then optional provider choice, with a process-wide selection semaphore and one-minute cancellation. Only credentialed native backends are offered. Selection is persisted before use; no auto-approval. The model sees provider progress and terminal `{ provider, results }` machine output, source Markdown result sections/Published text, and `{provider}` metadata. Genuine zero results use `No search results found. Please try a different query.` Expected 401/429/other status errors use source tool messages. The existing sealed ToolExecutionException cannot carry dedicated error metadata; the selected provider remains in progress and in the safe inner WebSearchException rather than adding another exception/registry protocol.

## Server attachment — MainServer owner

Call `MapWebSearchEndpoints()` once in the existing authenticated server surface. Bind **`IWebSearchLocationSource`** to the actual shared Location/plugin host. Its `AcquireAsync` must:

1. Acquire the same authoritative Location used by tool execution.
2. Await actual plugin/config readiness with the supplied cancellation token.
3. Return `WebSearchLocationLease(actualLocationInfo, sameRuntime, releaseActualLease)`.

Do not construct a new runtime/provider registry per endpoint request. The endpoint allows the source five seconds for readiness; cancellation must release incomplete acquisition. Disposal releases only that lease, not the host-owned runtime/providers. A missing source binding or no registered backend returns 503 explicitly. Registered provider inventory can include uncredentialed providers (it is not a health claim); queries still fail unavailable and tools are not advertised for them.

The handler maps provider-required/not-found/disabled to the source 400 `InvalidRequestError` kinds/field and backend request/unavailable to 503 `ServiceUnavailableError`. Upstream HTTP status is not used as the local response status. User query endpoints do not invent a tool context or perform a model-tool permission assertion; the actual tool leaf does so separately.

## Verification

Core, Server, and Client builds succeeded with **0 warnings and 0 errors**, using only the pinned repository-local .NET 11 SDK and isolated artifacts. OpenAPI document execution was disabled:

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Core\OpenCode.Core.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -p:OpenApiGenerateDocuments=false -v:minimal
.\.dotnet\dotnet.exe build src\OpenCode.Server\OpenCode.Server.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -p:OpenApiGenerateDocuments=false -v:minimal
.\.dotnet\dotnet.exe build src\OpenCode.Client\OpenCode.Client.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -p:OpenApiGenerateDocuments=false -v:minimal
```

No tests added/edited/run. No search, HTTP/backend/provider request, API call, credential lookup, environment credential inspection, DB/selection operation, application/process/native runtime verification, commits, or delegation was executed. Source inspection and compilation do not establish live backend interoperability.
