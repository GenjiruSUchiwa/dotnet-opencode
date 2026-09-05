# Inbox and management feed integration

## Existing receiver hook

The receiver owner now calls `ObserveManagementEvent(item)` from the existing
`SessionClientAdapter.PumpAsync`, before filtering by Session ID. The implementation
is in `SessionClientAdapter.Integrations.cs`. It observes only
`credential.updated`, `credential.switched`, `integration.updated`, and
`mcp.status.changed`, retaining a global or location-scoped invalidation revision.
It does not store credential values, infer account state, or create a second SSE
subscription.

The root's `ReadManagementRevision` callback already reads that helper. While a
manager is mounted, changed revisions coalesce into calls to its real public
`RefreshAsync`, followed by a catalog refresh. Client/location identity is checked
across asynchronous work. Manager-owned mutation and manual refresh paths remain
functional independently. This is source wiring, not runtime event-feed verification.

## Mounted inbox controls

`OpenCodeApp.Inbox.cs` and `Layout/InboxDock.razor` consume the selected Session's
actual `SessionObservationSnapshot.Inbox`:

- The source-style dock shows only queued user inputs, their actual count, and
  the first queued text. It is hidden during permission/form prompts.
- `session.queued_prompts` opens the queued list. Enter steers; the configured
  `queued_prompt.delete` action deletes the selected queued input.
- The pending-input view also exposes existing steers. Selecting one opens the
  source actions **Move to queue** and **Delete**.
- All three mutations call the existing typed Client methods with captured
  Session and inbox IDs. Per-item single-flight prevents duplicate submissions.
- Mutations do not optimistically remove or fabricate rows. The shared observer
  handles events and refreshes. A failed refresh is distinct from the accepted
  HTTP mutation and does not falsely report it as rolled back.
- Empty submission can promote an existing queued item. Ordinary admission now
  uses `CanSubmitPrompt`/`CanAdmit` on the complete capture before clearing the editor,
  instead of rejecting merely because another input still has a monitor.
- `prompt.queue` is registered for typed ordinary prompts and server commands.
  It sets `Delivery = Queue` on the captured input. Availability uses the actual
  connected admission reader, not a standalone removal of a guard.
- Autocomplete uses the configured source command bindings and yields while a
  leader sequence is pending, so `<leader>return` is not stolen by completion.

Sources: original `routes/session/index.tsx:566–613,1265–1271,2350–2473,2477–2507`
and `component/prompt/autocomplete.tsx:731–798`.

## Fork callback readiness

The root exposes `ForkBeforeMessage` and a real open-fork consumer. After a typed
fork succeeds, it hydrates through `OpenTabSession`, verifies the returned Session
identity, then creates/selects the tab and restores the original complete prompt.
No placeholder tab is inserted before hydration, and failed opening is reported
as a successful server fork whose view could not be opened.

The required boundary is `ForkRequestBoundaryBefore(selectedMessageId)`, not a
through-message boundary. The host now binds the supplied typed `Client.ForkAsync`
method. Fork appears only with that callback and its hydrated open-fork consumer.
No direct Core call or duplicate raw HTTP request has been added.

## Terminal readiness

Persistent terminal capability must come from
`SessionHttpClient.PersistentPtyCapabilitiesAsync().Data`; `CanAttempt` is only
permission to try a real attachment/create operation, not a connected-state claim.
Use `Reason` when unavailable. The root now mounts the real native-backed
`TerminalPane` only with an actual selected persistent PTY ID. It never substitutes
an ordinary PTY or plaintext terminal. List/create, confirmed removal, focus handoff,
rich-key leader deferral, and split-pane resize are connected.

Only isolated pinned .NET 11 CLI builds are permitted. No runtime, native,
clipboard, API, database, or test execution was performed.
