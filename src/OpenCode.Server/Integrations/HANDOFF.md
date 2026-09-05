# Integration backend and client handoff

## Required ServerHost wiring

No existing ServerHost, Tools factory, client root, or shared project file was edited in this pass. The owner should add:

```csharp
using OpenCode.Server.Integrations;

builder.Services.AddIntegrationServices();
// After building the authenticated main application:
app.MapIntegrationEndpoints();
app.MapCredentialEndpoints();
```

Register IntegrationHostService after the tool Location hosted service and before SessionExecutionService so shutdown drains execution, then integration attempts/listeners, then tool Locations. The composition requires the existing `CredentialStore`, `HttpClient`, and `IEventFeedService`; it does not create another database or credential path.

Tools owner must add `McpOAuthService? McpOAuth = null` to `LocalToolOptions` and change the existing runtime construction to:

```csharp
new McpRuntime(info.Directory, registry, permission, options.McpForms, options.McpOAuth)
```

Populate the host's LocalToolOptions with the IntegrationHostService's `OAuth` property. Preserve the existing MCP event bridge by composing the subscriptions:

```csharp
McpOAuth: services.GetRequiredService<IntegrationHostService>().OAuth,
McpCreated: (info, runtime) => services.GetRequiredService<IntegrationHostService>()
    .AttachMcp(info, runtime, new McpEventBridge(info, runtime,
        services.GetRequiredService<IEventFeedService>()))
```

`AttachMcp` does no I/O and does not reenter the Location map. It creates the Location's integration state and subscribes to MCP status invalidations before observation. Its returned subscription also owns/disposes the supplied existing event bridge. Do not discard that returned subscription or install a second MCP runtime.

Add this to the existing asynchronous Location-close callback, before the tool resources close:

```csharp
await services.GetRequiredService<IntegrationHostService>()
    .InvalidateAsync(new LocationRef(location.Directory, location.WorkspaceId));
```

Other credential endpoints should publish committed mutations through `IntegrationHostService.PublishCommittedAsync`. It emits only canonical `credential.updated`/`credential.switched` notification data and queues reconnection on matching live MCP runtimes. It never publishes the mutation's credential value. Reconnection is queued, not awaited under a credential or MCP lifecycle call.

## Credential management

`CredentialEndpoints.MapCredentialEndpoints()` implements the **three** operations actually declared by `packages/protocol/src/groups/credential.ts`, through `IntegrationRuntime` and the existing channel `CredentialStore`:

- `PATCH /api/credential/{credentialID}` with `{ "label": string }`
- `POST /api/credential/{credentialID}/activate`
- `DELETE /api/credential/{credentialID}`

All use canonical Location queries and return 204 after the committed store operation and normal notification publication. Missing IDs and activating an already-selected ID are source no-ops. Labels must be strings, not null/missing; the wire API permits empty strings as in source. Only the UI applies trim/nonempty validation. No credential secret/value update is accepted by these endpoints.

The source protocol has **no credential list/get HTTP endpoints**. Sanitized reads use `integration.list/get` and their `ConnectionCredentialInfo` ID/label projection. `SessionHttpClient.Credentials.cs` provides `ListCredentialConnectionsAsync` and `GetCredentialConnectionAsync` helpers over those existing routes; absent integration is distinct from an empty connection list. Mutation methods are `UpdateCredentialAsync`, `ActivateCredentialAsync`, and `RemoveCredentialAsync`.

Store selection/fallback ordering is unchanged: active/creation/ID order selects the current account; removing it selects the newest remaining credential and publishes `credential.updated` then `credential.switched`, including a null credential ID when none remain. Renaming emits updated only; activation emits switched only when selection actually changes. No new database, in-memory credential fallback, secret read route, or CredentialStore edit was needed.

Source mapping: `packages/server/src/handlers/credential.ts`, `packages/core/src/integration.ts` connection operations, and `packages/core/src/credential.ts` transaction/event behavior. Schema `CredentialInfo.Value`/stored `ValueJson` are never used as HTTP response or event payloads.

## Implemented canonical operations

`IntegrationEndpoints.MapIntegrationEndpoints` maps the exact paths from `packages/protocol/src/groups/integration.ts`:

| Method | Path | Behavior |
| --- | --- | --- |
| GET | `/api/integration` | Real sanitized integration catalog in Location.response |
| GET | `/api/integration/{integrationID}` | One integration; absent data for an unknown integration |
| POST | `/api/integration/{integrationID}/connect/key` | Validate the supported key method; persist to the existing channel store |
| POST | `/api/integration/{integrationID}/connect/oauth` | Start real authorization; return canonical Integration.Attempt |
| GET | `/api/integration/{integrationID}/connect/oauth/{attemptID}` | Location-owned canonical attempt status |
| POST | `/api/integration/{integrationID}/connect/oauth/{attemptID}/complete` | Source complete semantics; currently implemented methods are automatic |
| DELETE | `/api/integration/{integrationID}/connect/oauth/{attemptID}` | Cancel a pending attempt and release its resources |

All accept the canonical `location[directory]` and optional `location[workspace]` query. List/get/connect acquire the existing tool Location and observe its current configured MCP runtime before deriving definitions. Status/complete/cancel reuse existing state without relisting MCP catalogs, so polling does not start authorization or repeat discovery.

Command connect/status/cancel and experimental wellknown-add are mapped at their canonical paths but return a truthful 503. No command/plugin/wellknown methods are advertised. No `/mcp/auth` route was invented.

Error payloads use `InvalidRequestError` with `integration_authorization` or `integration_code_required`; unsupported capabilities use `ServiceUnavailableError`. Unknown/mismatched attempts are errors, never fabricated pending states. This implementation translates these source defect cases to InvalidRequestError rather than exposing an unhandled internal exception. Detailed provider/token responses and callback query strings are not returned in errors.

## Catalog and authentication implementations

- **OpenCode**: `device` / “OpenCode Console account”; key / “API key (service account)”. Calls the existing ConsoleIntegrationService for device start/poll/persistence. The optional `answer.server` uses that service's normalization. Org-aware default labels remain provider-owned.
- **OpenAI**: key plus `chatgpt-browser` / “ChatGPT Pro/Plus (browser)” and `chatgpt-headless` / “ChatGPT Pro/Plus (headless)”. Reuses OpenAiOAuthService only; no provider flow or token endpoint is copied.
- **MCP**: effective OAuth-enabled remote definitions from `McpRuntime.OAuthRegistrationsAsync`, including operational additions/removals. Uses the actual source name-plus-original-URL integration/method ID. The read model exposes only callback settings and server identity, not headers/client secrets. No OAuth service injection means these methods are not claimed as available.
- Connections contain stored credential IDs and labels in the existing reversed channel selection order, not credential material. This bounded catalog does not yet port the full models.dev/config/plugin/env integration registry.

MCP credential creation already publishes through the injected McpOAuthCredentialStore bridge. Console/OpenAI return committed CredentialMutation results, which are published once by the integration implementation. Key connections use the same store and publisher. No credential import, console-to-MCP token forwarding, or detached token store is used.

## Attempts and callback listeners

Core `IntegrationRuntime` is the single attempt registry for one Location. It returns the existing Schema `IntegrationAttempt`/`IntegrationAttemptStatus` unions. Automatic flows start one background completion worker, have provider/source ten-minute expiry, and retain terminal statuses for one minute with a 30-second scrub interval. Cancelling unknown, mismatched, or terminal attempts is a no-op as in source. Completing an automatic flow while its worker is active is an error; callers should poll its status.

The existing provider completion APIs include both network exchange and persistence. The registry therefore observes their actual result: if cancellation races with a successfully committed result, it retains `complete` instead of claiming that the saved credential was cancelled. It cannot reproduce TS's separate internal `persisting` phase without splitting those provider APIs. No change to Core/Llm was made.

`IntegrationCallbackFactory` creates an owned Kestrel loopback listener only during explicit browser/MCP authorization. Binding occurs before the SDK/provider creates its authorization URL. It supports HTTP `localhost` and `127.0.0.1`; custom remote/HTTPS callbacks require another explicitly configured implementation and fail clearly here. OpenAI tries 1455, then 1457 on address-in-use; it does not probe or cancel another process. MCP respects configured redirect URI/port or binds an actual ephemeral loopback port.

Listeners are armed with the authorization URL's state before it is returned to the UI. They validate actual callback state with constant-time comparison, reject missing/duplicate code/state and OAuth errors, and forward the real optional `iss` to MCP's SDK validation. Callback logging is disabled; responses use no-store/no-referrer and contain no secrets. No browser is launched. Success text acknowledges receiving the callback, not successful token persistence.

Cancellation, expiry, failure, Location invalidation, and host shutdown close the owned listeners and MCP authorization transports. Normal MCP connection/token refresh never creates a listener or performs interactive login. The pre-existing public-SDK limitation remains: MCP login needs a real challenge from the temporary handshake; an anonymous/header-authenticated endpoint that does not challenge cannot produce a fabricated authorization URL.

## Client API

`SessionHttpClient.Integrations.cs` contains network-only methods:

- `ListIntegrationsAsync`, `GetIntegrationAsync`
- `ConnectIntegrationKeyAsync`
- `ConnectIntegrationOAuthAsync`, `IntegrationOAuthStatusAsync`
- `CompleteIntegrationOAuthAsync`, `CancelIntegrationOAuthAsync`
- Canonical wellknown and command-operation clients, which preserve the server's unsupported response

Methods use Schema IDs/payloads and Protocol Location envelopes, plus an optional-data `IntegrationGetResponse`. Their JSON context is local to the new partial file. No Client runtime dependency on Core/Server or client-root/context edit was added.

## Source mapping

- Protocol paths/payloads/envelopes: `packages/protocol/src/groups/integration.ts`.
- HTTP authorization error mapping: `packages/server/src/handlers/integration.ts`.
- Catalog connection projection, key validation/persistence, attempt lifetime/retention/cancellation: `packages/core/src/integration.ts`.
- OpenCode labels/device flow: `packages/core/src/plugin/provider/opencode.ts`.
- OpenAI labels/browser/headless flow: `packages/core/src/plugin/provider/openai.ts`.
- MCP integration registration and OAuth callback ownership: `packages/core/src/mcp/index.ts` and `oauth.ts`; .NET public SDK details remain in `Core/Mcp/OAUTH.md`.

## Build-only verification

Pinned repository-local `.dotnet/dotnet.exe`; artifacts isolated to `C:\tmp\opencode\mcp-finish-pass`. The Client build succeeded with 0 warnings/errors. The full Server build succeeded with 0 errors and one existing warning in `Services/PermissionAwareCommandShell.cs` outside this work's ownership.

No tests were added/edited/run. No app, listener, OAuth/device polling, network/MCP probe, browser, DB/credential operation, or process-control verification was run. No shared project files or commits were changed. Live behavior remains unverified under the requested restriction.

The subsequent credential/UI pass's final **full CLI dependency-graph build succeeded with 0 warnings and 0 errors**, including CredentialEndpoints and Client credential methods. No CredentialStore edits were made. UI/root mount contract: `src/OpenCode.Cli/Tui/Integrations/HANDOFF.md`.
