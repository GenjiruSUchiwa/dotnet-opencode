# Integration and MCP management screens

## Root mount contract

Only the new `CLI/Tui/Integrations` subtree was edited for these screens. Root/modal/command registration is intentionally left to its owner. Import:

```razor
@using OpenCode.Cli.Tui.Integrations
```

Mount **one** screen in the root's existing modal selection. Both screens own their Modal and reuse the existing DialogSelect, Input, and FormComposer components. Do not wrap them in another Modal.

```razor
<IntegrationManager @key="(client, location)" @ref="integrationManager"
    Client="client" Location="location" Theme="DialogColors" FormTheme="FormColors"
    TerminalWidth="width" TerminalHeight="height"
    OpenExternal="OpenFormLink" CopyExternal="CopyFormLink" ReadClipboard="ReadFormClipboard"
    ResolveCommand="ResolveDialogCommand"
    OnChanged="RefreshCatalogs" OnConnected="IntegrationConnected" OnClose="CloseDialog" />
```

```razor
<McpManager @key="(client, location)" @ref="mcpManager"
    Client="client" Location="location" Theme="DialogColors" FormTheme="FormColors"
    TerminalWidth="width" TerminalHeight="height"
    OpenExternal="OpenFormLink" CopyExternal="CopyFormLink" ReadClipboard="ReadFormClipboard"
    ResolveCommand="ResolveDialogCommand"
    OnChanged="RefreshCatalogs" OnConnected="IntegrationConnected" OnClose="CloseDialog" />
```

Here `client` is the existing authenticated `SessionHttpClient`; `location` is an explicit Schema `LocationRef`. The key must change when Client or Location changes. Components capture that identity so late replies/cancellation cannot be redirected to a different server/Location. They do not discover a server, read user config, access a credential database, or create a second client.

Required theme parameters are `DialogTheme Theme` and `TerminalFormTheme FormTheme`. All new colors come from their semantic text, input, action, error, and background roles. No new raw palette or unrelated feedback token is used.

### Callback signatures

```csharp
Func<string, CancellationToken, Task>? OpenExternal;
Func<string, CancellationToken, Task>? CopyExternal;
Func<CancellationToken, Task<string?>>? ReadClipboard;
Func<ConsoleKeyInfo, string?>? ResolveCommand;
EventCallback<IntegrationId> OnConnected;
EventCallback OnChanged;
EventCallback OnClose;
```

- `OpenExternal` is called **only** on the user's open action or explicit method-form external action. No browser is opened on mount, normal MCP connection, or receipt of an authorization URL. Root can reuse its existing Form callbacks.
- `OnChanged` follows successful account/key/MCP mutations. Refresh the root's integration, model, and provider catalogs here as appropriate.
- `OnConnected` runs only after a successful key connection or OAuth terminal `complete`. It reports the actual integration ID, not a guessed provider/model. Root can choose an existing provider whose integration identity matches, following the source selection logic.
- `OnClose` must remove the screen from the modal tree. Pending authorization is cancelled before an ordinary close; disposal also attempts bounded cancellation. No optimistic account or server state is substituted for a failed HTTP call.
- Missing browser/clipboard callbacks show an explicit unavailable message. They are not replaced with a shell invocation or fake success.

Root's existing event stream should invoke public `RefreshAsync()` on the mounted screen:

- IntegrationManager: `credential.updated`, `credential.switched`, `integration.updated`.
- McpManager: `mcp.status.changed` and relevant integration changes.

These screens do **not** open duplicate SSE connections. They reread after their own successful mutations and offer manual refresh; background changes require the root event hook above.

## Integration behavior

- Source ordering: MCP first, then the existing popular-integration priority, then name/ID. Only methods actually returned by the backend are used. OAuth precedes key methods.
- Existing credentials open account management. The selected account is taken from source connection ordering, while display rows sort by label/ID. Environment connections are summaries, never activate/rename/delete targets.
- Add account selects a real supported method. Method forms use the production FormComposer with local callbacks; they do not create a fake pending server form or call Form reply endpoints. Only the resulting canonical authentication payload is submitted.
- Key entry is masked and the editor's current/history state is cleared on disposal. Rename trims input and requires a nonempty label, matching the source UI. The server's label contract remains an unrestricted string.
- Delete requires the same action twice on the same account; movement/filtering resets confirmation. Account selection, rename, and removal call the canonical Client credential methods and reload actual server data.
- OAuth auto mode polls the real status endpoint every 500 ms until complete/failed/expired. Network errors remain visible and status polling can be retried. Code mode sends only the provided authorization code through the canonical complete endpoint.
- Closing/unmounting cancels pending OAuth attempts. A late start response is cancelled when its attempt ID is known. If the network prevents cancellation, the server-owned expiry remains the cleanup backstop; no cancellation success is fabricated.
- Optional `InitialIntegration` filters the catalog. `AutoConnect=true` enters that integration's account/method flow after loading; use this only after an explicit user sign-in action, as McpManager does.

Command authentication UI is not advertised as supported by this bounded port; the current backend also omits command methods. Plugin/wellknown/env registration parity remains separate work.

## MCP behavior

- Lists actual current configured runtime status: connected, pending, failed, needs-auth, disabled.
- Toggle disconnects connected servers, retries inactive ones, and routes needs-auth to IntegrationManager using the server-provided integration ID. Missing OAuth integration support is an explicit error, not a fake sign-in.
- Enter opens actual failure details or the sign-in flow. `InitialServer` and `Details` support opening a specific server's error view.
- Failed initial catalog loads have a dedicated unavailable/retry view, not a fake empty-server list.
- No new configuration-write/add-server UI is invented; this matches the upstream MCP management dialog's status/toggle scope.

## Keys and shared UI limitations

The source command IDs `dialog.integration.rename`, `dialog.integration.delete`, and `dialog.mcp.toggle` are used. Pass root's modal key resolver and matching `RenameShortcut`, `DeleteShortcut`, or `ToggleShortcut` captions to honor configured bindings. Standalone fallbacks are F2, Delete, and Space respectively. Ctrl+R refreshes; OAuth uses O=open, C=copy, R=retry polling, Escape=cancel. Browser details copy the source device-code pattern when available, otherwise the authorization URL.

The current shared DialogSelect focuses the current account on initial display and has no per-option feedback color parameter; status text/symbols are preserved rather than borrowing unrelated semantic colors or editing that owner's component. This differs from upstream's separate focusCurrent/colored-footer options.

## Source mapping and verification

The interaction source is `packages/tui/src/component/dialog-integration.tsx` (ordering, account management, two-step delete, method forms, OAuth polling/cancellation/open/copy) and `dialog-mcp.tsx` (status text, toggle, failure details, integration sign-in).

All verification is build-only with the repository-local .NET 11 SDK and isolated `C:\tmp\opencode\mcp-finish-pass` outputs. No tests, application/TUI run, screenshot, OAuth/browser/clipboard operation, network probe, credential/DB access, or process-control verification was performed. Root mount files, generic Dialogs, themes, and project files were not edited.

Final full CLI dependency-graph build succeeded with **0 warnings and 0 errors**, including the new Razor screens, Client, Server, and Core. Command: `.\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -v:minimal`. Earlier diagnostic/blocked builds were not treated as full verification; no runtime or narrow/wide terminal behavior was exercised.
