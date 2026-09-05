# Status and theme dialogs

The original dialog batch changed only the new `CLI/Tui/SystemDialogs` subtree. Root markup, command registration, generic Dialogs, the theme resolver/catalog/state, and shared preference writer were not edited. The subsequent prompt-stash batch is documented separately in `../Stash/HANDOFF.md`.

## Root commands and mounts

The source commands in `packages/tui/src/app.tsx` are:

- `opencode.status` — “View status”, slash `/status`
- `theme.switch` — “Switch theme”, slash `/themes`

Mount in the existing root modal selection; each component owns its Modal/production DialogSelect, so do not wrap it in another Modal.

```razor
@using OpenCode.Cli.Tui.SystemDialogs

<DialogStatus Theme="ElevatedColors" Backdrop="@DialogColors.Backdrop"
    Mcp="sharedMcpSnapshot" Loading="mcpLoading" Error="@mcpError"
    TerminalHeight="height" OnClose="CloseDialog" />
```

```razor
<DialogThemeList @key="(themeCatalog, Themes)"
    Catalog="themeCatalog" State="Themes" Backdrop="@DialogColors.Backdrop"
    TerminalHeight="height" ResolveCommand="ResolveDialogCommand"
    FlushPersistence="FlushExistingThemeWrites"
    PersistenceError="@themeError" ReportError="ReportThemeError"
    OnClose="CloseDialog" />
```

`ThemeCatalog` and `ThemeState` must be the same existing application-owned instances. Example hook signatures:

```csharp
Func<Task> FlushExistingThemeWrites; // existing ThemeSettingsPersistence.FlushAsync or legacy root write queue
Action<Exception> ReportThemeError;
EventCallback OnClose;
```

`FlushPersistence` and `ReportError` are optional integration hooks. **Do not create a second ThemeSettingsPersistence, CliSettingsStore, settings registration, or file writer for the dialog.** The root's already connected ThemeState.PersistenceRequested subscription remains the only persistence path. Root should keep that subscription/state alive until dialog cancellation/disposal has restored the initial theme and its queued write has flushed.

`OnConfirmed` is an optional `EventCallback<string>` reporting the exact selected registry name. It is not needed to apply the theme: ThemeState.Set already updates the live application and requests persistence.

## Status behavior

Input is `IReadOnlyList<McpServer>?`, supplied from the root's existing authoritative Location snapshot/feed. The component makes no Client/API/SSE request and does not manufacture LSP, plugin, service, or provider health sections.

- Null/unloaded/error is an explicit unavailable/loading state, not “No MCP servers”.
- A successful empty list shows source “No MCP servers”.
- Nonempty lists show the source count and ordered rows. Names are bold; statuses use semantic success/error/warning/subdued bullets and source labels: Connected, the actual failure message, Disabled in configuration, Needs authentication, or `pending`.
- Scroll support bounds long lists/details to the terminal; it changes no server state. Close is the only system action.

Root continues to own refresh and default Location selection. Passing a stale snapshot with a known source error displays the error rather than presenting it as healthy.

## Theme preview and cleanup behavior

- Names and supported modes come from the actual ThemeCatalog.List(), including the 33 embedded built-ins and current installed/plugin/custom/generated-system entries. There is no hardcoded replacement catalog or assumed two-mode support. Catalog owns source base-sensitivity sorting and precedence.
- The exact initial `ThemeState.Settings.Name` is captured once when opening. DialogSelect's current marker stays on that initial choice rather than following transient previews.
- Keyboard/pointer movement previews through **ThemeState.Set**. Filtering previews the actual first filtered option, using the same production DialogSearch scorer/order as DialogSelect. Clearing the query restores the initial name; no match leaves the current preview unchanged.
- Confirmation calls Set on the selected real name, marks confirmation immediately after successful local application, and closes. An async shared-writer failure is reported, not turned into an unconfirmed preview rollback. No “saved successfully” claim is made by a new writer.
- Escape/backdrop close and unconfirmed disposal restore the initial name through the **same Set/persistence path**. Successful cancellation plus disposal does not queue duplicate restoration. If external state changed again, disposal restores the initial name again rather than trusting a stale restored flag.
- Only the name is restored. The dialog never changes/replays the mode preference or terminal mode; ThemeState resolves single-mode fallbacks and current light/dark/system preference. Footer mode labels are actual catalog/current values.
- The dialog subscribes only for UI/catalog/error updates. It never owns or disposes the application's ThemeState, ThemeCatalog, or persistence adapter. Calls/mutations follow those objects' existing renderer-dispatcher ownership contract.
- A removed/unresolvable theme is an explicit error; no nearest/default theme is substituted. If the original theme disappears before cancel, the root error hook receives the failed restoration rather than a fake success.

The existing root ThemeState.Changed handling still owns native renderer colors and application rerendering. The dialog's own colors are read from the current elevated token view so live preview applies to the chooser as well. Backdrop is passed from the root's existing source scrim; no new raw palette is introduced.

## Source mapping and verification

- `packages/tui/src/component/dialog-status.tsx`: MCP-only status content, labels, semantic colors.
- `packages/tui/src/component/dialog-theme-list.tsx`: actual names, initial selection, move/filter preview, confirmation, cancellation cleanup.
- Existing native ThemeCatalog/ThemeState and ThemeSettingsPersistence: real assets/custom modes and the shared queued writer.

Verification is build-only using the repository-local .NET 11 SDK and isolated `C:\tmp\opencode\mcp-finish-pass` artifacts. No tests, runtime theme evaluation, preference reads/writes, MCP/API/DB/native/process interaction, UI run, or screenshot was performed. The original follow-up build was blocked by concurrent SDK references to `SessionBackgroundService`; a later full CLI build during the stash batch succeeded with **0 warnings and 0 errors**, including these dialogs. Those SDK files were not changed by this owner.
