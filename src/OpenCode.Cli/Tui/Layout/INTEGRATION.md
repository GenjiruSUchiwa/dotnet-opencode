# Mounted session frame

`Components/OpenCodeApp.razor` now chooses separate Home and Session layouts. An
actual selected session, even one without messages, uses `Layout/SessionFrame`.

## Source-backed geometry and state

- `packages/tui/src/component/session-frame.tsx`: one growing session pane, 42-cell
  sidebar, automatic sidebar only when available width is **greater than 120**,
  no sidebar for child sessions, and an absolute right-side overlay when opened
  on a narrower terminal. The narrow overlay does not resize the transcript.
- `routes/session/index.tsx:1358–1505`: side padding 1 below terminal width 44,
  otherwise 2; bottom padding 1; growing production transcript; one-row jump-to-
  latest area; non-growing permission/form/composer area. No Home 75-cell cap and
  no Home three-row version footer in a session.
- `component/prompt/index.tsx`: input grows to `max(6, terminalHeight / 3)`;
  left border, metadata below input, lower half-block border, metadata breakpoints
  28/50/70, directory/running status and context/cost/agent/command footer hints.
- `routes/session/sidebar.tsx` and `feature-plugins/sidebar/*`: elevated sidebar,
  title/workspace, context tokens/percentage, spent cost, actual MCP states, and
  working-directory footer. Sidebar scrolling starts at the top, not at the end.

The sidebar toggle has a registered command callback. Escape/Ctrl+C closes a
narrow overlay; page/up/down keys scroll it; clicking its backdrop closes it.
Forms and permissions take priority over a narrow sidebar. Click-to-latest and
transcript row toggles use the actual production transcript and existing keyed
expansion state. Session input pointer events update its controlled cursor and
selection; they do not mutate or replace a later draft.

`SessionPresentation` comes from typed server catalog/MCP reads for the **selected
location**, not the launch directory. Context usage uses the last assistant with
usage after the last completed compaction and before revert. Cost combines the
hydrated Session cost with subsequent visible assistant cost increments, without
double-counting the hydrated messages. No fabricated token counts or model limits
are supplied. Actual agent metadata/categorical theme colors are used.

## Root integration shared with other owners

- `CommandPaletteDialog` now receives `PaletteCommands`, the captured pre-modal
  context, all configured shortcuts, resolved dialog colors, and actual dispatcher
  callbacks. The root no longer maintains a separate switch-based palette command
  list. Registered command conditions control availability. Default keybinding
  data is used only for labels/chords of callbacks that actually exist.
- Model/agent/variant dialogs, session picker, tabs, transcript, permission forms,
  form composer, frame, and root background receive the theme owner's semantic
  tokens. The actual production `opencode` asset is the default, not the generic
  blue fallback token document. Configured/custom themes load through
  `CliThemeSettings`, `ThemeDiscovery`, `ThemeCatalog`, and `ThemeState`.
- Root subscribes to theme changes/errors and unsubscribes on stop. The host passes
  `CreateThemePersistence` with the shared store, settings controller, and catalog.
  Exactly one `ThemeSettingsPersistence` instance owns atomic JSONC name/mode edits
  and is disposed by the theme partial. Home Composer and Wordmark receive their
  resolved component theme records as well.
- Tab selection/close/add/reorder/promotion, the root-level tab context menu and
  rename dialog, and session-picker rename/delete call real root/client methods.
  Root mounts the tab owner's existing action partials, keyboard interception,
  focus-aware viewed callback, and shutdown join. The picker owns scope persistence. Transcript
  `OnToggleRow` shares keyboard row-picker state. No owned Transcript/Dialogs/
  Sessions/Tabs implementation files were edited by this mounting owner.
- Permission “Always allow” is gated by the real
  `PermissionCapabilitiesAsync().Data.PersistentGrants` response and pending
  `Save` patterns. Capability failure/disconnection disables it. Replies preserve
  the selected reply and use the actual owning session, including descendants.

## Explicit remaining services and parity limits

- The persistent terminal selector and real native `TerminalPane` are mounted,
  with source split-width clamping, pointer resize, focus handoff and rich-key
  leader deferral. The server's `CanAttempt`/`Reason` response gates list/create;
  only a real returned PTY ID can attach. There is no ordinary-PTY substitution or
  plaintext terminal. Shell/subagent management remains separate work.
- The observer owner now provides Session-ID-scoped observation and origin-bound
  prompt reader ownership. Root polls `ReadSessionObservation` each frame and sends
  explicit interruption through `InterruptObservedSession`; disposing a reader is
  not substituted for interrupting execution. Runtime background-tab behavior has
  not been exercised.
- Slash autocomplete and command admission are mounted. The canonical command
  endpoint returns HTTP 204 without an inbox identity, so the command partial uses
  the existing Session observer and admission semaphore rather than guessing an
  enqueue or inventing another stream loop. A new Session is made visible before
  interpolation can request permission. See `../Commands/INTEGRATION.md`.
- The actual `DialogConfig` is mounted. Palette settings search replaces the palette
  with the selected setting. One controller registers real theme, mouse, scroll,
  leader-timeout, thinking, Markdown, and sidebar consumers, loads preferences on the UI
  dispatcher, and uses the shared writer. Grouping is omitted until the production
  transcript exposes that consumer. The sidebar command's
  temporary explicit-open state remains distinct from the persisted auto/hide preference.
- Message Actions, whole-code/message copying, prompt-selection copying, and the
  host clear/cursor/selection colors are mounted. Revert stages through the typed
   client and refreshes the shared observer. Fork uses the actual typed Client
   before-message callback. Styled/word-wrapped and cross-block selection is supplied by the
  generic owner's native-backed selection implementation; root copying uses its
  `Text`/`HasText` contract, not native display indexes as UTF-16 offsets.
- Integration/MCP managers are mounted from their real authenticated Client and
  Location, including the model picker's connect action. Their own mutations
  refresh actual catalogs. External management events reach `RefreshAsync` through
  the shared receiver's management revision hook; no duplicate SSE is started.
- Normal typed-prompt attachment admission and queueing are connected. File/directory
  `@` completion uses the server filesystem API; editing preserves structured
  references and native-display mention offsets. No local existence probes occur.
- Terminal system-palette detection and clipboard reading remain separate integration work.
  No full visual-parity claim is made.

Verification is source review and isolated local .NET 11 CLI builds only. No app,
native renderer, API, database, process-control, screenshots, or tests were run.
