# Mounted activity, shell mode, and rich paste

## Activity composition

`OpenCodeApp.razor` mounts the supplied `SessionActivities` in the composer slot.
The root loads ancestors and paginated descendants through the existing Client,
passes the real `SessionActive` map, and requests child messages through the
shared Session observer. Shell rows are filtered by `metadata.sessionID` and keep
their execution Location from the response/event envelope. Names, titles, and
the list of all commands in a Location are not used to infer Session ownership.

`session.child.first` opens the subagent view; `session.shells` opens shell activity.
Child navigation opens the real Session. Closing its activity view returns to its
parent. Composer keymap mode and focus are restored when the panel closes.
The persistent terminal remains a separate capability-gated native pane.

`ObserveManagementEvent` forwards envelopes to the activity projection before its
management-only filter. This reuses the existing receiver, not a second SSE feed.
Shell-created/Session-shell events retain actual records; shell-exited events
request canonical status at the original Location. An open output pane receives
`RefreshOutputAsync` after shared shell events and uses its supplied byte-cursor
reader. Running-only list absence never creates a fabricated terminal status.
No Job projection or cancellation callback is supplied: foreground/background and
blocking labels remain absent until an authoritative transport is available.

## Explicit shell mode

Typing `!` at the start of the input enters shell mode without inserting `!`.
Pasted text does not toggle it. Escape, Backspace at the start, and Ctrl+C on an
empty shell draft exit the mode. Prompt history stores/restores the mode alongside
typed attachments, metadata, and mark snapshots. Shell mode suppresses mention and
slash completion and shows the source Shell label and selected-action color.

Submission captures and clears the origin draft before asynchronous work. The
adapter creates/observes a real Session when needed, exposes it to the origin tab
before a permission-producing POST, and calls `RunSessionShellAsync` once under
the existing admission semaphore. It never uses CreateShellAsync, a local process,
a fabricated prompt message, or an automatically retried POST. Shell input cannot
be queued. Failure restores an otherwise empty origin draft, not newer typing.
An uncertain POST invalidates the shared observation for reconciliation; a refresh
failure after an acknowledged POST does not restore a runnable command.

## Paste boundary

`OpenCodeApp.Clipboard.cs` invokes `OpenTuiHost.ReadClipboardAsync` only through the
explicit prompt action. The host copies returned bytes; the root handles each
result discriminator. PNG creates typed inline data with source image numbering
and `extmark.paste` styling. File URIs must match the selected server Location and
its authenticated file-list response. Text stays text, with no path inference,
local reads, upload fallback, or OSC52 query. Delayed reads cannot change another
draft. Existing copy transport is unchanged.

## Verification boundary

Only pinned repo-local .NET 11 full CLI builds with isolated artifacts and
`OpenApiGenerateDocuments=false` are permitted. No tests, application/native run,
shell execution, API/SSE probe, clipboard operation, database query, process
control, or screenshot is used. Compilation does not establish focus, visual,
shell-lifecycle, clipboard-platform, or terminal-protocol runtime parity.

The final full CLI dependency-graph build passed with **0 warnings and 0 errors**
using `C:\tmp\opencode\forms-compile-20260904-a`. Earlier concurrent SDK blockers
cleared without edits to SDK source in this batch.
