# Root-owner changes for the integrated tab actions

`OpenCodeApp.Tabs.cs` and `OpenCodeApp.TabActions.cs` now operate on the existing
`_tabs`, `_tabViews`, configuration-operation ownership and callback properties.
There is no second tab state, adapter, Session registry or HTTP client. Existing
`SelectTab`, `NewTab`, `CloseTab`, `ReopenTab`, `RegisterCurrentTab`, and
`ReadGlobalTabActivity` names remain. Existing `MoveTab` and `PromoteTab` in
`OpenCodeApp.SessionActions.cs` are reused and were not edited.

## Minimal markup changes (root owner)

1. Add `OnContextMenu="OpenTabMenu"` to the existing `ConversationTabs` mount.
2. Update the existing picker mount; keep its normal visibility condition:

```razor
<SessionPicker LoadPage="LoadTabSessionPage" SelectSession="ChooseTabSession"
    CreateSession="@(CreateSession is null ? null : CreateTabSessionFromPicker)"
    RenameSession="@(RenameSession is null ? null : RenameTabSession)"
    DeleteSession="DeleteSession" OnDeleted="PruneDeletedTabSession"
    ResolveCommand="ResolveDialogCommand" Theme="TabColors"
    CachedSessions="@(ReadSessionCache?.Invoke() ?? [])"
    CurrentDirectory="@CurrentDirectory" Current="_sessionId"
    ActiveSessionIds="_activeSessions" TerminalHeight="_height"
    AllProjects="_tabPickerAllProjects" AllProjectsChanged="SetTabPickerScope"
    TabsEnabled="SessionTabsEnabled"
    RenameShortcut="@Shortcut("session.rename")"
    DeleteShortcut="@Shortcut("session.delete")"
    PinShortcut="@Shortcut("session.pin.toggle")"
    OnClose="CloseDialog" />
```

3. Inside the full-height root Box, add the menu overlay. Do not nest it in the
   one-row ConversationTabs component (the renderer clips to ancestors):

```razor
@if (_tabMenuState is { } menu)
{
    <Box Position="TuiPosition.Absolute" Left="0" Right="0" Top="0" Bottom="0"
         ZIndex="2500" OnPointerDown="DismissTabMenu">
        <SessionTabContextMenu @ref="_tabMenu" Request="menu" Actions="_tabMenuActions"
            TerminalWidth="_width" TerminalHeight="_height" Theme="TabColors"
            OnClose="CloseTabMenu" />
    </Box>
}
```

4. Alongside the other modal mounts, mount the context-menu rename editor. It
   reuses the existing picker editor; it does not load or synthesize a Session:

```razor
@if (_tabRename is { SessionId: { } renameID } renameTab)
{
    <SessionPicker @key="renameID" LoadPage="LoadTabSessionPage"
        RenameTarget="renameID" InitialTitle="@renameTab.Title"
        RenameSession="RenameTabSession" Theme="TabColors" Size="ModalSize.Medium"
        OnClose="CloseTabRename" />
}
```

Add `@using OpenCode.Cli.Tui.Tabs` to the root markup for the context component.
All handlers and fields above already exist in the two owned partial files.

## Keymap and lifecycle changes (root owner)

- At the beginning of `DispatchKeymap`, call `NoteTabKeyboardFocus()`. Then, when
  `HandleTabMenuKey(key)` is true, return a handled `KeymapDispatchResult` before
  sidebar, editor and command handling. That method captures menu keys and queues
  actual action tasks in the existing `_keyTasks` list.
- Use `TabKey(...)` rather than `IdleKey(...)` for Session tab/new/reopen actions.
  Use `TabNavigationReady` rather than `NavigationReady` in those commands' conditions
  and for opening the Session picker. Keep the existing model/config guards intact.
- `CycleTab(direction)` can delegate to `TabKey(() => SelectAdjacentTab(direction))`;
  unread cycling uses `SelectAdjacentTab(direction, unread: true)`. Direct-index
  selection should use `_tabs.SelectIndex(index)` to exclude the new-session slot.
- `CloseDialog` should also close tab menu/rename state when dismissing all dialogs.
- `OnTerminalFocusChanged(bool)` is already implemented by TabActions and used by
  the existing terminal host. Do not add a second implementation.
- After root shutdown has cancelled/joined its configuration and stream tasks,
  await `StopTabActionsAsync()` before disposing `_tabLifetime`. This stops view
  retries, closes menu mode, and joins existing tab writes/navigation.

`ReadGlobalTabActivity()` is already called from `OnFrame`; no second frame hook is
needed. It normalizes root identities, folds family activity, enriches attention
from actual permission/form snapshots, reads root idle/viewed watermarks, persists
title changes, prunes reported deletions, and updates the focus-aware view tracker.

## Host callback changes (adapter/root owner)

- Bind `ReportSessionViewed` to the existing authenticated client:
  `async (id, idle, ct) => await (await RequireApi(ct)).ViewAsync(id, idle, ct)`.
- Bind `ReadDeletedTabSessions` to authoritative observed deleted IDs, not IDs absent
  from a partial list/cache. Picker deletion already calls `PruneDeletedTabSession`.
- Keep `ReadSessionCache` live, including root idle/viewed timestamps, parent IDs and
  titles. Keep `ReadTabActivity` live for all open families, including pending inbox,
  permission/form attention, title-renaming state and non-active prompt pulses.
  These are observation facts; do not derive unread from a completion event alone.
- Existing real `LoadSessions`, `OpenSession`, `CreateSession`, `RenameSession` and
  `DeleteSession` callbacks are used; the new wrappers do not return fake success.
  `PreviewSessionTabs` enables the existing preview replacement state. `TabScope`
  initializes picker scope and must match the host's `SessionTabStorage` instance.

## Background-observation implementation

The subsequent observation pass implements the adapter and origin-routing changes
below together. See `OBSERVATION.md` for the actual shared APIs, typed ordinary
prompt callback, disposal contract and root bindings. The notes below record the
old failure modes, not requirements to remove guards without replacement ownership.

Tab actions no longer call the root's `RunConfigurationAction`, whose `_request`
check silently ignored navigation. They share its configuration task ownership but
do not gate on model busy state and never cancel `_request` to navigate.

The prior adapter rejected navigation while `_submitting != 0` in
`OpenSessionAsync` and `NewConversation`. Those navigation guards have now been
replaced by Session-bound admission and observation ownership:

1. Adapter observation must retain the original Session-ID-bound subscription and
   pending response when the active view changes. It must support simultaneous
   observations; leaving a view must not run `PromptAsync`'s interruption finalizer.
2. Root `StreamAsync` currently writes every response into `_sessionId`, `_responses`,
   `_promptTexts`, composer selections and status. Route those updates to their
   originating tab/view instead. Background completion must not replace the visible
   Session, restore a failed prompt into another draft, or clear another request.
3. Active-view selection must not own the global permission/form/event cache.
4. `ForgetDeletedSession` currently throws while a local submission is active even
   after DELETE succeeds. Separate committed deletion from observer settlement so
   the picker does not report a successful server deletion as a failed operation.

These changes are now implemented in the explicitly extended adapter/stream scope.
No detach/cancel workaround or fabricated background-observation capability was added.

## Verification

Only pinned .NET 11 isolated full-CLI builds are used. No TUI, API, process adapter,
database, screenshot or test execution is permitted. Wiring is not marked complete
until the root-owner mounts and lifecycle edits above are present.
