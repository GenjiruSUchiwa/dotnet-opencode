# Native UI Feature Surface

The CLI mounts Razor components through `OpenTui.Blazor` and the native OpenTUI
library. Interactive requests use `SessionHttpClient`; the UI does not execute
models or tools, open a database, launch Bun, or draw an ANSI canvas.

## Entry And Connection

- Default entry and `tui` mount `Components/OpenCodeApp.razor`.
- `--server <HTTP(S) origin>` selects exactly that endpoint. Failed explicit
  selection never falls back to managed startup or embedded execution.
- `OPENCODE_DOTNET_SERVER_PASSWORD` supplies optional explicit-endpoint Basic
  authentication. Managed endpoints use their discovered credential.
- The client waits for `server.connected` before session creation or prompt
  admission. Server capabilities and session-specific rejection responses remain
  authoritative; the UI does not opt into degraded execution.

## Reachable Controls

| Context | Control | Implemented action |
| --- | --- | --- |
| Composer | Enter | Snapshot and clear synchronously before asynchronous preparation. Restore on pre-admission failure only if the composer is empty; preserve newer typing. |
| Composer | Ctrl+J or modified Enter | Insert newline when the terminal reports the distinction. |
| Composer | Left/Right, Home/End, Backspace/Delete | Edit the draft on grapheme boundaries. |
| Composer | Ctrl+Left/Right, Alt+B/F | Move between word/symbol groups. |
| Composer | Ctrl+Backspace/Ctrl+W, Ctrl+Delete/Alt+D | Delete the previous/next word, or the selected range. |
| Composer | Shift+arrows, Shift+Home/End | Extend selection; typing or deletion replaces the selected range. |
| Composer | Ctrl+Minus / Ctrl+Period; Commands → Undo/Redo prompt edit | Restore text, cursor, and selection from tab-local edit history. New edits discard redo. |
| Composer | Ctrl+A/E, Ctrl+U/K | Logical-line home/end and deletion to line boundaries. |
| Composer | Up/Down | Move visual lines, then access submitted-prompt history at the boundary. |
| Composer | Ctrl+C | Clear nonempty draft; otherwise interrupt active work or exit when idle. |
| Composer | Escape | Interrupt active work or exit when idle. |
| App | Ctrl+P | Open commands available in the current state. |
| App, when idle | Ctrl+O | Open the server-backed session picker. |
| Tabs, when idle | Ctrl+Tab / Ctrl+Shift+Tab, Alt+Down / Alt+Up | Select the next/previous open tab. |
| Tabs, when idle | Ctrl+1 through Ctrl+9, Ctrl+0 | Select tab 1 through 10 when the terminal reports the chord. |
| Tabs, when idle | Ctrl+X, N | Open/select the new-session draft tab. |
| Tabs, when idle | Ctrl+X, W | Close the selected view without deleting its server session. |
| Tabs, when idle | Ctrl+Shift+T | Reopen the most recently closed view. |
| Transcript | PageUp/PageDown | Scroll measured rows without snapping to new output. |
| Commands | Follow latest output | Restore bottom stickiness after scrolling away. |
| Commands | Show/hide reasoning, tool details, timestamps, token usage | Change actual transcript presentation. |
| Commands | Expand or collapse transcript row | Filter/select a reasoning, exploration, or tool row by stable key; Enter toggles only that row. |
| Composer, when idle | Shift+Tab; Ctrl+T | Cycle selectable agents; cycle named model variants and the default selection. |
| Commands, when idle | Model, agent, variant | Load canonical catalogs and apply actual session changes. |
| Commands, when idle | New conversation, reload | Reset the local view for a new session or reconcile server state. |
| Session picker | Ctrl+R, Ctrl+L, Ctrl+A, Ctrl+N | Refresh, next page, scope, and real session creation. |
| Pickers | Arrows, Home/End, PageUp/PageDown, Enter | Filter/navigation and singleflight selection. |
| Modal | Escape; Ctrl+C | Close; clear nonempty filter before closing. |
| Permission composer | Selection keys and Enter | Send the user's actual once/reject decision. |

Model and agent changes are unavailable while another configuration operation or
prompt is active. Catalog-disabled models and unsupported native transport
families remain visibly unavailable. Variant selection preserves the complete
`provider/model#variant` reference before applying one change.

Model selection preserves a valid current variant for the same model, or a valid
variant remembered for that model during this client session, without forcing a
second picker. The variant picker remains available when no valid preference
exists. Cycle commands are also available in the command palette; reverse agent
cycling uses the configured `agent.cycle.reverse` binding.

Only the home composer is capped at 75 cells. Session composers and permission
requests use the available pane width. Session hydration supplies the selected
session's location for the composer directory and session-picker scope labels.
Permission PageUp/PageDown now moves the generic text viewport from its leading
edge; tail-relative scrolling keeps its existing behavior.

Per-row expansion and collapse are tab-local overrides of the global detail
defaults. The keyboard row picker operates on `TranscriptRows` keys and passes
those overrides to the mounted `SessionTranscript`, rather than changing the
global reasoning/tool switches.

Source references for these interactions: `routes/home.tsx:89` (home width),
`component/dialog-model.tsx:122-134` and `context/local.tsx:261-272` (variant
preservation), `model-preference.ts:46-53` (variant cycle order), and
`routes/session/index.tsx` (row-local reasoning/exploration expansion). Mouse
activation of transcript rows is not part of this keyboard interaction pass.

Paste normalizes CRLF and CR to LF, preserves indentation and tab characters in
the submitted text, and uses two-cell tabs only for display. Empty paste does not
erase a selection. Bracketed paste is limited to 1,048,576 UTF-16 code units; an
oversized paste is rejected as a whole and drained through its closing marker,
never reinterpreted as shortcuts. This limit is a native-host safety policy, not
an upstream attachment-size claim. File/image paste attachment preparation is
not implemented. Submission history records the prepared snapshot, not text typed
later while the network request is in flight.

Undo/redo retains up to 200 edit snapshots per tab, including replacement of a
selection, paste, deletion, clear, and history recall. Submission clears
the edit history with the captured draft before asynchronous preparation. Closing and reopening a
retained tab preserves its in-memory edit history; history is not written to disk.
The configured `prompt.submit` command is registered in addition to `input.submit`.
Ctrl+Minus and Ctrl+Period are resolved from terminal key identities, including
events that have no printable character.

Source references: upstream `packages/tui/src/context/keymap.tsx` registers
`input.undo` and `input.redo`; `config/keybind.ts` defines their default chords.
`component/prompt/index.tsx:1194-1210` snapshots and clears submitted text before
the first await, then restores it only when the composer is empty. History is
appended once the target session is established, before admission, matching
upstream's `history.append(entry)` at line 1276; subsequent rejection does not
remove that entry. Existing adapter reconciliation retains session/prompt IDs
when admission is uncertain. This does not claim native undo coalescing or
complete prompt feature parity.

Failed pre-admission submissions keep the home draft and wordmark. A server-side
empty session may exist after creation, but it is not enough to promote a new
draft tab: admission/delivery or explicit session navigation establishes the
conversation view. Diagnostics wrap below the composer instead of replacing its
single-line model metadata. Missing model metadata offers a finite loading/select
state, not a fabricated model identity.

The composer shadow has a one-cell terminator and an explicit remaining-width
strip. Literal one-row text uses intrinsic terminal-cell measurement and direct
native buffer drawing; wrapped/rich content retains native text views. This avoids
using a text viewport's width or padding as the intrinsic width of key hints.

## Transcript rendering

- `TuiText.WrapMode` exposes native none/character/word wrapping. Markdown prose
  and tool prose use word wrapping; literal code and unified patches keep
  character wrapping. Changes to wrapping invalidate the retained viewport.
- Markdown tables share measured column tracks across header/body rows, rather
  than independently allocating each row. Short columns retain their natural
  widths when possible; long cells wrap. Markdown center/right column alignment
  is applied through generic box cross-axis alignment.
- Shell tools render the captured command/workdir and output in a block. The
  collapsed preview uses upstream's ten-line/width-based character budget;
  expanding the existing row exposes the full captured output.
- Read rows show the path and loaded instruction paths. Glob/grep rows show
  their pattern, path, and canonical match counts. Edit rows render canonical
  `metadata.files[].patch` as a unified patch, with caller-supplied diff colors.
- Exploration groups are collapsed by default and retain their tab-local row
  expansion overrides. Tools awaiting permission remain visible outside the
  collapsed group, matching upstream's pending-permission partition.

References: `routes/session/index.tsx` (`SessionGroupView`, `ShellDisplay`, `Read`,
`Glob`, `Grep`, `Edit`, and Markdown grid-table options), plus
`routes/session/rows.ts:74-79` for pending permission sources. Split diffs, syntax
highlighting, and background-shell output fetching are not implemented by this
rendering pass. No extra packages or native bindings were added.

## Tab State

Tabs are real local view identities backed by canonical session IDs after session
creation. The default working-directory scope is persisted in
`$XDG_STATE_HOME/opencode/dotnet/tui/tabs.json`, falling back to
`~/.local/state/opencode/dotnet/tui/tabs.json`. The `global`/`cwd`/`tabs`/`unread`
layout follows upstream `context/storage.tsx` and `context/session-tabs.tsx`.
Writes use an atomic rename and a bounded native lock, merge this client's tab
changes, and preserve unrelated scopes. There is no live file watcher yet.

Session IDs and titles persist. Selected route, closed-tab history (20 entries),
drafts, cursor positions, prompt history, presentation toggles, and scroll anchors
are in-process state; draft text is not written into the tab file. Startup opens a
new-session view and makes restored session tabs available for selection.
Closing/reopening never calls server deletion. The tab strip consumes actual
global execution/rename events, not inferred timestamp activity.

This first navigation pass deliberately blocks selection and closing while a
prompt or configuration operation is active. It keeps that stream attached to
its original view; background stream transfer/multiplexing is not implemented.
Per-tab scroll state detaches render-node references and restores by stable row
identity after remounting. Leader combinations use the upstream Ctrl+X default
and two-second timeout unless overridden in the CLI configuration.

## Configured Keymap

The host routes press events through one dispatcher, layer registry and mode
owner. Configuration comes from `$XDG_CONFIG_HOME/opencode/cli.json` or
`~/.config/opencode/cli.json`, not project `opencode.json`. Keybindings and
`leader.timeout` are read at startup. The shipped native Ctrl+O session-list alias
is retained only when `session.list` has no explicit override.

Implemented app and composer commands use named callbacks, including word/line
editing, selection, submission, history, scrolling, tab navigation and supported
palette actions. The old hardcoded app/editor shortcut switches are no longer
on the dispatch path. Unmatched character input remains the text-insertion
fallback. Bindings honor prevent-default separately from handled results; async
work is tracked by the app owner, and layer leases are disposed at shutdown.

Modal mode disables base app bindings, and permission focus does not activate
app-exit bindings. Dialog-local single-line selection/clear/close mappings remain
in their existing components; those mappings are not yet fully migrated to the
configurable dispatcher. The generic table contains 233 source definitions, not
233 implemented actions. Undo/redo and other unsupported commands are not
registered as successful no-ops. Unknown configured command IDs fail explicitly;
plugin bindings and live configuration reload are not implemented.

The current ConsoleKeyInfo input fallback supplies press events and available
Ctrl/Shift/Alt modifiers. Release, Super/Hyper, repeat and base-layout metadata
cannot be reconstructed when that transport does not provide them. Full native
input-protocol parity is not claimed.

## Explicit Authentication

`auth` and `console` commands route to the native authentication command handler
before TUI startup. No login is started automatically. See `Auth/README.md` for
the supported Console device and OpenAI browser/headless flows. Credential
notification descriptors are not published through a fabricated session bus.

## Rendering And Source Mapping

- Home: `routes/home.tsx:75-96`, including centered flexible space, shrinkable
  gaps, adaptive side padding, and the 75-column composer cap.
- Branding: upstream `logo.ts` and logo width breakpoints, with one subdued
  dotnet label. Styled rows use retained native UTF-8 runs.
- Transcript: `routes/session/rows.ts` and session message/part renderers.
  `Transcript/SessionTranscript.razor` receives canonical keyed messages rather
  than a flattened conversation string.
- Reasoning/exploration: consecutive parts retain group identity; reasoning is
  collapsed by default. Read/glob/grep groups retain real tool states and errors.
- Markdown: Markdig 0.41.3 supplies the AST. Headings, lists, code, quotes,
  emphasis, links and pipe tables use native components and retained runs.
- Permissions: actual asked/replied events plus pending-list hydration bind the
  replacing composer to session/request/tool identities. Persistent Always allow
  stays disabled without a declared durable persistence capability.

## State And Recovery

Text and reasoning ends replace provisional fragments. Tool input ends replace
input deltas; called/progress/success/failed events drive state, timestamps,
provider-executed identity, result content and metadata. A settled tool cannot be
reverted to streaming by a late provisional update. Execution outcomes, not a
single tool or step end, finish observation of a request.

Session and prompt IDs survive acknowledgement uncertainty. Reconciliation reads
canonical history/inbox state and correlates buffered events with the submitted
input and aggregate sequence. It does not automatically re-post prompts.
Cancellation/failure before observed settlement uses the server interrupt API.

## Remaining Boundaries

This is not full upstream parity. Language-specific highlighting, decoded images,
advanced table alignment, full timeline/revert and pending-inbox interactions,
background streaming across tab switches, and broader terminal mouse/selection behavior remain
incomplete. Backend MCP/plugin, compaction, retry and other producer restrictions
must be reported, not bypassed. Build verification is not runtime, visual, tool,
permission, or provider verification.
