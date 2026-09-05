# Session tabs: root integration contract

Implementation lives in `Tabs`, `Sessions`, and `Components/ConversationTabs.razor`
plus its code-behind. The subsequent integration pass also owns `OpenCodeApp.Tabs.cs`
and `OpenCodeApp.TabActions.cs`; see `ROOT-INTEGRATION.md` for exact root-owner edits.
It does not change `InteractiveTui`, root markup/keybindings, the Session client
adapter, generic Blazor, or Core. Do not mark all interactions
mounted merely because the existing root renders the `ConversationTabs` component.

## ConversationTabs

Existing parameters `Tabs`, `Selected`, `Busy`, `Activity`, and `TerminalWidth` remain
compatible. Connect these callbacks to the root's real navigation/state operations:

| Parameter | Type | Root action |
| --- | --- | --- |
| `OnSelect` | `EventCallback<Guid>` | Existing async `SelectTab` |
| `OnClose` | `EventCallback<Guid>` | Existing async `CloseTab`; never delete the Session |
| `OnAdd` | `EventCallback` | Existing `NewTab` |
| `OnMove` | `EventCallback<SessionTabMove>` | Apply `_tabs.Move(move.Key, move.Index)`, persist before/after layouts |
| `OnPromote` | `EventCallback<Guid>` | Apply `_tabs.Promote(key)`; preview status is memory-only |
| `OnContextMenu` | `EventCallback<SessionTabContextRequest>` | Mount the root overlay described below |

`Orientation` uses `SessionTabOrientation.Horizontal/Vertical`. Map the real CLI
`tabs.layout` setting, and only place a vertical rail when
`AdaptiveSessionTabs.FitsVertically(totalWidth, railWidth)` permits it. Vertical
defaults: width 42, min 24, max 72, at least 44 content columns. `TerminalHeight` is
the available rail height. Parent owns the horizontal/vertical root arrangement
and sidebar resize gesture. `Numbers` maps `tabs.indicators == "numbers"`;
the default status mode is not a numbered-strip approximation. `Animations`,
`Spinner`, and `UnreadMarker` control their corresponding source state dimensions.

Supply the resolved `SessionTabsTheme` for the current theme. It uses semantic text,
surface, form-field, running, unread, permission, question and error roles. The same
theme contract is accepted by the picker/context menu. No ANSI painting is used.

### Layout and pointer behavior

The adaptive solver directly ports `context/session-tabs-model.ts`: active preferred
width 22, roomy cap 32, inactive minimum 8, previous-window retention, and both
`‹count ` / ` count›` overflow markers with digit-aware reserved widths. The idle
add affordance reserves 3 columns. A selected new-session slot replaces that
affordance. Vertical Session rows use 2 lines plus a 1-line gap.

Pointer events use generic `TerminalPointerEventArgs`, including native local and
screen coordinates and `Handled` before awaits. Implemented: select, hover close,
middle close, right-click request, 300 ms double-click preview promotion, drag
preview with one committed move on release, both overflow directions, vertical
scrolling, and the source's 5-second horizontal close-cell hold. Title metrics and
grapheme boundaries use native OpenTUI; marquee delay is 600 ms with 80 ms steps.
Source running/glow/completion/prompt pulse envelopes and pending-title shimmer
are represented by native styled runs.

### Context menu

`SessionTabMenu.Actions(request, close, add?, rename?, promote?)` builds the source
New tab / Keep open / Rename / Close ordering from actual callbacks. Mount
`SessionTabContextMenu` as a child of a **full-screen root overlay**, not inside the
one-line strip (ancestor clipping would hide it). It positions a 16-column menu
using `Request.X/Y`, clamped by `TerminalWidth/Height`. Supply `Actions`, `Theme`,
and `OnClose`. Route overlay keys to `HandleKey`, and close the overlay on outside
pointer-down. Root owns overlay focus/mode and restoring prompt focus.

## Live state and navigation

`SessionTabActivity` preserves `Busy`/`Title` and adds `Unread`, `Attention`,
`Renaming`, and `PromptPulse`. `From(root, family, running, pending, permissions,
forms, renaming, promptPulse)` derives source semantics from real snapshots:

- Root idle/viewed watermarks determine unread. Failed root outcome means error.
- Any family permission takes priority over a family question/form.
- Any family running state or pending inbox item means busy.
- A permission/question suppresses the running spinner, not the underlying busy fact.

Do not synthesize these values from the currently visible request alone. The host
adapter must feed current offscreen metadata and family state. On non-active user
inbox admission, increment that root's prompt pulse. On deletion, call
`SessionTabState.RemoveDeleted(rootID)` and invalidate cached views. This clears the
reopen stack for deleted Sessions, not only the visible tab.

State helpers include `Normalize` (child-to-root identity), `Open` (optional preview
replacement), `Promote`, `Move`, `Cycle`, `CycleUnread`, `SelectIndex`,
`NavigateHistory`, and `Reopen`. Reopen restores the original slot and consumes
already-open entries; source bounds are 10 closed tabs and 100 history entries.
Use `Reopen()` rather than the root's previous append-only reopen implementation.
Root still owns transcript scroll/view caches, draft state, loading and navigation
commit ordering. It must not replace those with a second tab-owned Session registry.

`SessionTabViewTracker` takes `Func<SessionId,double,CancellationToken,Task>` bound
to `SessionHttpClient.ViewAsync`. Call `Update(rootSession, focused)` for route,
focus and watermark changes, **even when tabs are disabled**. It reports the actual
observed idle timestamp and retries 250 ms to 5 seconds while focused. Dispose with
the TUI. Do not report current time or child completion as the root watermark.

## Persistence and picker

`SessionTabStorage(directory, scope)` supports `SessionTabScope.Cwd/Global` and the
same LoadAsync/SaveAsync callbacks. It preserves concurrent unrelated tabs while
applying removals, title updates and ordering. It keeps unread empty for rollback
compatibility; unread itself remains server-owned. Source file shape is
`state/opencode/<dotnet-channel>/tui/tabs.json` with global/cwd scopes.

`Sessions/README.md` documents typed HTTP picker callbacks, rename/delete confirmation,
and persisted `tui/session-list.json` scope. Root must bind those new callbacks;
their mere implementation does not mount them.

## Animation completion pass

The previously reported four animation gaps now have source-derived implementations:

- `SessionTabMotion`: horizontal widths, selection, and activity use the source critically
  damped spring (`visualDuration=0.1`, frequency `2π/(duration*1.2)`, rest delta/speed
  `0.002`, elapsed step capped at 50ms). New visible members start at zero width, retained
  members keep their current values, and a fully replaced window jumps. Same-shape
  retargeting retains velocity; membership seeding resets it as upstream's `jump` does.
  Identity-stable total resizes jump except when releasing the close hold. Held widths
  snap to the held geometry. The existing five-second close hold captures painted widths.
- Widths round with `floor(value+0.5)`. Only rounding slack no larger than the visible
  member count is absorbed by the selected tab; membership growth retains a real gap.
  Native box widths stay at least one cell. Title paint is bounded by the remaining
  cells, including widths too narrow for a title, rather than painting a split wide glyph.
  The adaptive 22/32/8 thresholds and overflow solver are unchanged.
- `SessionTabSeparator`: independent inner/outer pulse voices use the source upper/lower
  tint stops, 8/5-cell glow tails, 8-cell flash tails, 10-column indicator region, latched
  feedback hues, and 200ms selected-attention dimming. Upper gaps use `▄` with current-tab
  foreground and previous-tab background; the last lower gap uses `▀`. These paint in
  actual gap rows because the native renderer clips children to their ancestor boxes.
  The two-line tab plus one-line gap geometry and hit targets remain unchanged.
- `SessionTabIndicatorMotion`: unread jumps on, pending work cancels the dissolve, and
  clearing unread fades over 180ms with the source cubic smoothstep. Its old hue is latched,
  with the first-fifth flash at opacity 0.8. Number mode does not display the fading marker.
- `SessionTabTitleMotion`: pending shimmer uses the source 1200ms sweep and 240ms blend.
  Only a title change following pending rename starts the 450ms arrival wipe. The source
  coast curve, four-cell feather, and rounded cut are applied to snapshots of the last
  composed native-measured styled glyphs. New-title and old-title slices cannot split
  wide glyphs. New pending work cancels the old arrival; unchanged-title completion fades
  the shimmer out; disabling animation clears the arrival immediately. Snapshot colors
  and attributes are retained, while the current tab background remains live.

These visual states have no Session, preference, or unread-watermark side effects. Horizontal
states retire when they leave the visible window; vertical states follow rail membership.
Orientation changes remount their visual state. The existing component-owned animation loop
now ticks at the source animation scheduler's 16ms cadence; spinner/marquee frame intervals
remain 80/120ms and 80ms. No second timer or renderer was added. Component disposal cancels
the loop, releases snapshots/state, and disposes the native text-measurement view.

## Verification

Use only the repository-pinned .NET 11 full isolated CLI build with
`-p:OpenApiGenerateDocuments=false`. See the task report for its latest result; builds may
be blocked by concurrently edited packages. No TUI, pointer event, HTTP request, storage
operation, test, screenshot, or native execution was used for verification. Styled-glyph
composition uses the existing native text path, not a framebuffer clone. Exact framebuffer
parity and performance improvements are not claimed without runtime measurements.
