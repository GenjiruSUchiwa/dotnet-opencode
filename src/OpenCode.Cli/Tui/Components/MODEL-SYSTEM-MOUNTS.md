# Model preferences, system dialogs, and recovery mounts

## Model acceptance and preferences

`InteractiveTui` owns one `ModelPreferenceService` for the client lifetime and
loads it during application initialization. Load errors remain visible. The same
service is passed to the supplied `ModelPicker` and the root's
`ModelSelectionController`. The picker receives the actual integration catalog,
configured keybindings, shortcuts, and the existing real integration action.
Integration-load errors are not treated as a healthy empty catalog.

`AcceptExactModel` propagates failure, checks the actual returned model/variant,
and does not close the picker or write preferences. The picker records recents
after acceptance. The root controller records accepted cycling/standalone variant
choices. Hover, catalog refresh, and Session hydration do not write preferences.
Persistence failures report that selection succeeded; no automatic reselection
or fallback is performed.

The five source cycling commands are registered: recent forward/reverse, favorite
forward/reverse, and variant cycle. Recent/variant cycling requires a current
selection; favorite cycling can start without one. Empty favorites show the source
informational message. Stored variants and the default variant slot use the
supplied controller rather than a second root preference algorithm.

Provider connection uses the existing integration dialog. Successful connection
refreshes the actual catalog and remounts the model picker with the related provider
filter. There is no second ConnectProvider flow or preference writer.

### Focused picker actions

Configured single-stroke Provider/Favorite bindings use `ResolveDialogCommand`.
`RegisterModelActionDispatcher` now consumes the supplied selector's
`RegisterActionDispatcher` hook. It registers only configured leader/multi-stroke
sequences in the existing modal keymap and tracks invocation tasks in the root's
existing task collection. The selector owns the registration lease and removes it
when leaving the model-list stage. Its actual focused option and current actions
govern invocation; the root does not use the Current marker, synthesize keyboard
events, write preferences, or interpret dispatch as model acceptance.

## System dialogs

`opencode.status` and `/status` mount `SystemDialogs.DialogStatus` with the actual
Location-scoped MCP snapshot. Shared management revisions invalidate its typed
read; errors and unloaded data stay distinct from a successful empty list.
No LSP/plugin/provider health is invented.

`theme.switch` and `/themes` mount `DialogThemeList` with the existing application
`ThemeCatalog` and `ThemeState`. Preview, confirmation, and cancellation use the
existing `ThemeSettingsPersistence`. Shutdown disposes/restores the dialog before
disconnecting that writer. No duplicate theme store, settings registration, or
save callback is installed.

## Recovery directory picker

The unavailable-Location card now opens `DirectoryMoveDialog`. Its actual
`RecoveryDirectoryClient` loads worktrees/directories through the authenticated
Client, and submission calls the same observer's `MoveSessionAsync`. Closing all
dialogs also closes the picker. HTTP admission does not change CurrentDirectory
or clear recovery evidence; only authoritative observation can do that.

## Verification boundary

Only pinned repo-local .NET 11 full CLI builds, isolated artifacts, and
`OpenApiGenerateDocuments=false` are used. No tests, application/native execution,
preference reads/writes, API calls, filesystem probes, DB/clipboard operations, or
screenshots are performed for verification. Compilation does not establish
runtime behavior or visual parity.

Final full CLI build: **0 warnings, 0 errors**, with isolated artifacts at
`C:\tmp\opencode\forms-compile-20260904-a`. Earlier concurrent Run/OpenAPI build
blockers cleared without changes to those owner files in this batch.
