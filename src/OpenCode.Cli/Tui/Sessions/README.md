# Session picker root handoff

This subtree implements `SessionPicker`; root mounting/delegate wiring remains
owned by the main UI. No root file was changed in this pass.

## Typed HTTP composition

`SessionPickerClient` accepts:

```csharp
Func<CancellationToken, Task<SessionHttpClient>> client
Func<CancellationToken, Task<LocationInfo>> location
```

These must return the real authenticated client and current authoritative Location.
Bind its `LoadAsync`, `RenameAsync`, and `DeleteAsync` methods to the picker's
`LoadPage`, `RenameSession`, and `DeleteSession` parameters. This adapter uses the
canonical `OpenCode.Protocol.Groups.SessionListQuery` and `SessionOrder`, not a
Core store query or a duplicate wire DTO. It calls the existing typed client APIs.
Queries use descending order, limit 50, root-only Sessions, search and opaque cursor.
Current-location scope uses exact directory for project `global`, otherwise project
ID and slash-normalized relative subpath, as upstream does. All-project scope omits
these filters. The Location callback must not infer project identity from a label.

Keep existing `SelectSession(SessionInfo, ct)` and optional `CreateSession(ct)`
callbacks for real root navigation. Selection closes only after success. No fake
Session or optimistic rename/delete response is inserted.

## Additional component parameters

- `RenameSession`: `Func<SessionId,string,CancellationToken,Task>`. Opens a native
  title editor; Enter submits a nonempty trimmed title, Escape cancels. HTTP failure
  stays visible. Success closes the dialog, matching source rename replacement.
- `DeleteSession`: `Func<SessionId,CancellationToken,Task>`. The first action changes
  that row to a confirmation prompt; the second deletes. Moving, hovering another
  row, changing search or changing scope cancels the confirmation. Failure clears
  confirmation and reports the error. Removed rows cannot be restored by a stale
  in-flight page.
- `OnDeleted`: `EventCallback<SessionId>` after successful deletion. Root removes the
  Session family from tabs/cache and navigates away when required.
- `Preferences`: actual `SessionPickerStorage`, supplied by default. Loads and saves
  `allProjects` in dotnet-channel `tui/session-list.json`, preserving other fields
  under a file lock and atomic replacement. `AllProjects` supplies the initial
  fallback; root should initialize it from `tabs.scope != "cwd"`. The persisted
  preference wins. `AllProjectsChanged` keeps the root's current flag synchronized.
- `Theme`: resolved `SessionTabsTheme` roles. No hardcoded numbered-tab palette is
  embedded in the Razor layout.
- `CachedSessions` and `CurrentDirectory`: real cached metadata for local fallback
  after transport failure. Do not fabricate cached records.
- `TabsEnabled`, `PinnedSessions`, `QuickSwitchSlots`, and `TogglePin`: source pinned
  list behavior when tabs are disabled. Pinning appears only with a real callback.
- `ResolveCommand(ConsoleKeyInfo)`: optional root keymap resolver. A supplied
  resolver returning null does not reactivate default mutation bindings.
  `RenameShortcut`, `DeleteShortcut`, `PinShortcut`, and `QuickSwitchHint` supply
  matching user-configured footer labels. Defaults are Ctrl+R, Ctrl+D, Ctrl+F.

Existing `Current`, `ActiveSessionIds`, `LocationLabel`, `OnClose`, `Size`, and
`TerminalHeight` remain compatible. Active IDs must include roots with running
children; activity is not inferred from timestamps.

## UI and lifecycle

Rows are root-only, newest-first within local-date categories, with pinned order
first only when tabs are disabled. Current Session is bold, running rows use the
source dot spinner, and rows support pointer hover/selection through generic Blazor
events. Native title editing/paste retains grapheme boundaries.

Search debounces at 150 ms, retains the previous page while loading, coalesces
in-flight queries, discards stale scope/query results, and falls back to supplied
cache on failure. Ctrl+A changes/persists scope. Ctrl+G retries; Ctrl+L retains the
existing next-page extension. Ctrl+N is shown only for a real creation callback.
Rename/delete are hidden when their callbacks are absent. HTTP callbacks receive
the picker lifetime token. Closing cancels outstanding work, but cannot undo a
server operation that already committed.

Verification is pinned .NET 11 full CLI compilation with isolated artifacts only.
No picker, HTTP API, storage operation, test or application was executed. See
`../Tabs/HANDOFF.md` for the tab component, root overlay and live-status contracts.
