# MCP OAuth handoff

## Host composition

OAuth uses the installed official `ModelContextProtocol` 2.2.0 SDK and an injected adapter to the **existing .NET channel credential store**. It does not open a database, choose a credential path, read TypeScript credentials, import `auth.json`, or use console/OpenAI credentials.

```csharp
var persistence = new McpOAuthCredentialStore(existingChannelStore, publishCommittedMutation);
var oauth = new McpOAuthService(persistence);
var mcp = new McpRuntime(directory, registry, permission, forms: locationForms, oauth: oauth);
```

`publishCommittedMutation` has type `Func<CredentialMutation, CancellationToken, Task>`. Publish only the mutation's normal credential notifications, not its secret-bearing `Credential`. Mutation publication runs after commit, including when the request token has subsequently been cancelled. The callback must enqueue reconnect work, not synchronously await a runtime whose connection may have initiated the refresh.

The adapter accepts an already constructed `CredentialStore`; channel placement remains its injected database's responsibility. A host with another persistence boundary can implement `IMcpOAuthCredentials` instead:

```csharp
Task<CredentialInfo?> SelectedAsync(string integrationId, CancellationToken ct);
Task<CredentialInfo?> GetAsync(CredentialId id, CancellationToken ct);
Task<CredentialId> CreateAsync(string integrationId, CredentialOAuth value, string? label, CancellationToken ct);
Task<bool> UpdateAsync(CredentialId id, CredentialOAuth value, CancellationToken ct);
Task<bool> RemoveAsync(CredentialId id, string? expectedRefresh, CancellationToken ct);
```

`SelectedAsync` must follow the existing channel selection order, not return an arbitrary matching row. Update returns false when removed. Remove returns false when absent or when a non-null `expectedRefresh` no longer matches. The provided store bridge uses the current public store's read/check/remove operations, like the TS source; it does not claim an atomic cross-process compare-and-delete or clustered refresh lock.

Tools owner: pass the optional `oauth` constructor argument from the host's Location factory; do not construct another store or register MCP twice. Server/Integration owner: register the existing integration/method UI using `McpOAuthService.IntegrationId(server, originalUrl)`, the server name as label, and source metadata `mcp`. Both integration and OAuth method IDs use the same source-derived identity. SDK token caches are not a second credential registry.

## Runtime API for Server/Client

Load current configuration with `ObserveAsync` on the shared Location runtime before invoking these methods:

```csharp
Task<McpOAuthAuthorization> StartAuthorizationAsync(
    string server, Uri redirectUri, CancellationToken ct = default);

Task<CredentialId> CompleteAuthorizationAsync(
    McpOAuthAuthorization attempt, string code, string state,
    string? issuer = null, string? label = null, CancellationToken ct = default);

ValueTask CancelAuthorizationAsync(McpOAuthAuthorization attempt);
Task<bool> RevokeAuthorizationAsync(string server, CancellationToken ct = default);
Task<McpObservation> CredentialChangedAsync(string integrationId, CancellationToken ct = default);
```

The underlying `McpOAuthService` also exposes `StartAsync(server, config, redirectUri, ct, lifetime)`, `CompleteAsync`, `CancelAsync`, and `RevokeAsync` for the host's existing Integration orchestration. It has no static attempt registry. Use either the runtime wrapper or the existing Integration owner, not parallel independent attempt managers.

### Start and callback ownership

1. The host binds its callback listener **before** Start and supplies the actual absolute HTTP(S) `redirectUri`. Respect configured `redirect_uri` and `callback_port`; Start rejects mismatches. With neither set, the host can bind an ephemeral loopback port and supply `http://127.0.0.1:<port>/callback`. Core never guesses a free port or opens a listener.
2. Start performs real SDK discovery and dynamic client registration as needed. It returns only when the SDK produces an authorization URL. The host can expose the attempt's `Server`, `IntegrationId`, `AuthorizationUri`, `RedirectUri`, and `ExpiresAt` through an explicit wire DTO. Do not serialize the attempt as a durable record. Retain the actual object under the host's existing attempt ID.
3. Present/open the authorization URL only as part of the user's explicit login operation. Pass the callback's actual `code`, `state`, and optional `iss` to Complete. Never substitute locally remembered state for missing callback state. The SDK validates exact state and issuer before code exchange and owns PKCE. A callback reporting an OAuth error must cancel the attempt and show an appropriate host error; it must not call Complete with fabricated code/state.
4. Complete waits for a real token response and persists a canonical `CredentialOAuth`. It returns only the saved `CredentialId`, not access/refresh tokens. The temporary authorization transport is then closed. Runtime wrappers reject changed URL/OAuth configuration and dispose the attempt even if its server has since been removed.
5. Attempts have a ten-minute deadline, are single-completion, and implement `IAsyncDisposable`. Cancel/dispose closes the temporary SDK transport and does not create a credential. The runtime supplies its shutdown token. The host must cancel attempts when removing their UI/integration registration and close its callback listener on completion, failure, timeout, or cancellation.

Source `mcp/oauth.ts` invokes TS SDK `auth()` directly. The public .NET SDK exposes this flow through `HttpClientTransportOptions.OAuth`; this implementation uses a temporary, real MCP handshake to obtain the server's challenge. It does not invent a 401 response or hardcode discovered endpoints. No catalogs, tools, roots, or elicitation handlers are installed on this temporary client. If the endpoint connects anonymously or configured headers satisfy authentication without a challenge, Start fails with no authorization URL rather than manufacturing a login success. This is a deliberate public-SDK limitation versus unconditional TS `auth()`.

OAuth grant success is distinct from MCP discovery success: Complete can save a real granted token before the temporary MCP handshake completes. Subsequent runtime reconnect establishes actual `connected`/`needs_auth`/`failed` status and catalogs.

### Credential switch and local revoke

Enqueue `CredentialChangedAsync(integrationId)` for committed `CredentialNotification.Switched` events in every affected live Location. It rechecks the current definition, reconnects only matching OAuth-enabled servers, and leaves explicitly disconnected/disabled entries disabled. Value-only refresh updates follow the existing store behavior and do not fabricate switch events.

Complete and Revoke rely on this host event bridge rather than issuing a duplicate reconnect themselves. Revoke removes only the currently selected OAuth credential for the named integration, using existing store notifications and fallback selection. It does not delete other accounts, edit MCP config, or claim to revoke the token remotely through RFC 7009. Upstream MCP OAuth has no remote revocation operation. If another credential becomes selected, the event-driven reconnect uses that credential.

`McpServer.IntegrationId` is populated for OAuth-enabled remote servers when the OAuth service is injected. A missing host service fails explicit OAuth operations with `NotSupportedException`; it never claims persisted authentication. Missing servers use `McpServerNotFoundException`.

## Normal connection and token refresh

- Integration/method identity is `mcp_` plus the first 16 lowercase hexadecimal characters of SHA-1 of `serverName + NUL + originalConfiguredUrl`, exactly as TS. CodeMode query augmentation does not alter storage identity.
- `oauth: false` bypasses OAuth and credential reads. An HTTP auth rejection in that mode remains `failed`, rather than advertising an OAuth login requirement. Anonymous and configured-header transports still work without an OAuth service.
- Additional MCP headers remain unchanged and go through the official transport. No console token, default Authorization header, or credential from another integration is added. Explicit `client_id`/`client_secret` are passed to the SDK; a secret without a nonempty client ID is not used. Without a client ID the SDK performs DCR.
- Explicit nonempty configured scopes are selected through the SDK's public scope selector. When unspecified, discovery/challenge scope selection remains SDK-owned. No authorization/token endpoints or authorization-server identity are guessed by this code.
- The persistent `ITokenCache` uses the SDK's exact public signatures: `ValueTask<TokenContainer?> GetTokensAsync(CancellationToken)` and `ValueTask StoreTokensAsync(TokenContainer, CancellationToken)`.
- Each SDK token read reloads the originally selected credential row, not a cached token snapshot. Refresh results update that same row and survive restart. The host's credential-switch event reconnects to a newly selected account. Removed or mismatched rows do not receive new tokens silently.
- Stored values preserve source `methodID`, `access`, `refresh`, absolute `expires` (`0` means unknown/non-expiring), and metadata `serverUrl`, `tokenType`, optional `scope`, and `client`. SDK client ID, secret, token endpoint auth method, and authorization-server issuer binding are retained so DCR tokens can refresh after restart. The issuer is extra .NET SDK metadata, not a replacement for source server identity.
- Normal connection callbacks always throw `McpOAuthRequiredException` rather than invoking the SDK's default console input handler or launching a browser. Such a connection settles to `needs_auth`. Refresh itself is SDK-owned. There is no silent fallback to a console credential or an unrelated account.
- The SDK does not expose TS's scope-specific `invalidateCredentials` callback. The cache supports conditional removal if it receives a null-token invalidation, but exact SDK rejection/invalidation behavior has not been exercised. Explicit revoke and host credential removal are implemented. Cross-provider refresh serialization and atomic cross-process rotation remain host concerns, not claims made by this adapter.

## Source and public API references

- `packages/core/src/mcp/index.ts`: OAuth integration identity/registration (206-246), noninteractive token provider and persisted refresh (254-328), credential-switch reconnect (658-678).
- `packages/core/src/mcp/oauth.ts`: provider/store callbacks (11-96), credential/client metadata conversion (99-135), DCR/PKCE interactive attempt and callback behavior (137-239).
- `packages/core/src/mcp/client.ts`: remote transport auth, configured headers, auth-required status conversion.
- Installed SDK 2.2.0 `lib/net10.0/ModelContextProtocol.Core.xml`: `ClientOAuthOptions.AuthorizationCallbackHandler`, `AuthorizationResult`, `DynamicClientRegistrationOptions`, `ITokenCache`, `TokenContainer`, and `HttpClientTransportOptions.OAuth`. Discovery, issuer/resource checks, PKCE, DCR, and refresh are driven by these public SDK surfaces, not copied internal helpers.

## Verification boundary

Only full Core builds using the pinned repository-local .NET 11 SDK and isolated `C:\tmp\opencode\mcp-finish-pass` artifacts are permitted/run. An offline restore refreshed assets for already owner-added package references using `-p:RestoreSources=C:\tmp\opencode\mcp-finish-pass -p:NuGetAudit=false`; no dependency or project-reference change was made here.

No OAuth operation, network probe, MCP server, app, test, database/credential read or write, listener, browser, or process-control operation was run. Build success establishes API/type compatibility, not live OAuth interoperability. Server/Client endpoint and listener composition remains its owner's work.

Final full Core build: **succeeded, 0 warnings, 0 errors**, using `.\.dotnet\dotnet.exe build src\OpenCode.Core\OpenCode.Core.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -v:minimal`.
