# Session shell publication

The shell runtime calls these public methods on its existing SessionStore:

```csharp
Task<OpenCodeEvent> PublishShellStartedAsync(
    SessionId sessionId, ShellInfo shell, CancellationToken ct = default,
    EventId? eventId = null, IReadOnlyDictionary<string, JsonElement>? metadata = null);

Task<OpenCodeEvent> PublishShellEndedAsync(
    SessionId sessionId, ShellInfo shell, ShellOutput output,
    CancellationToken ct = default, EventId? eventId = null,
    IReadOnlyDictionary<string, JsonElement>? metadata = null);
```

These methods publish `session.shell.started.1` (`sessionID`, `shell`) and
`session.shell.ended.1` (`sessionID`, `shell`, `output`). They return the actual
committed event. They do not run a shell, claim model execution, wait for a process,
admit completion input, or wake a Session.

`SessionShellLifecycle(SessionStore)` now implements Core/Shell's exact
`ISessionShellLifecycle` contract. The main Server owner can register:

```csharp
services.AddSingleton<ISessionShellLifecycle, SessionShellLifecycle>();
```

The adapter uses these publishers and the existing synthetic admission boundary.
It does not pin events to the process's original cwd; publication is Session-ID
based, and notification admission loads the current Session. Keep the shell host's
pre-spawn missing-adapter guard in place.

Completion callback identity uses the actual `metadata.shellID` plus Session ID:
`msg_shell_` followed by SHA-256 of UTF-8 `sessionID + NUL + shellID`. This is the
explicit native correlation policy for an interface without a MessageId argument,
not a claim that upstream's random synthetic-ID allocation uses this format.
Reconciliation precedes notification payload copying/admission, preserving first
admission wins across retries and restarts. It always admits with resume false.
Spawn-failure notifications have no shell ID and get a fresh message ID; identical
command text or timestamps are not used to guess that two attempts are the same.

## Source projection and conflict rules

Source: `schema/session-event.ts:Shell`, `session/message-updater.ts`, and
`session/projector.ts:getShell/insertMessage/updateMessage`.

- Started inserts a shell message whose ID derives from the **event ID**, not the
  shell ID. Its sequence and created timestamp come from the event. It contains
  shellID, command and status; no process PID/path/cwd fields are invented in the
  message projection.
- Projected metadata is a copy of event metadata. Only shell.metadata.background
  being exactly true adds/overrides the projected background flag. Other shell
  metadata stays in the event's ShellInfo and is not copied into the message.
  Inputs are captured as read-only metadata dictionaries before publication.
- Ended selects the latest matching shell message **within that Session**, ordered
  by sequence. It replaces status, optional exit and output, and sets completed
  from the event timestamp. Message identity, sequence, created time and metadata
  remain unchanged. Process ShellTime is not used as a substitute timestamp.
- There is no shell-ID idempotence invented here. Repeated shell IDs with distinct
  started event IDs create distinct source messages. Reused event IDs conflict in
  the retained event store. Repeated ends with fresh event IDs update the latest
  match; an end with no remaining start still records its event without fabricating
  a message. It never updates another Session's shell row.
- Projection and event insertion use the existing canonical EventStore transaction.
  Publication is covered by Session admission/removal accounting, rejects missing
  Sessions/unsequenced legacy projections, and notifies only after commit. Session
  recency and model execution state are not changed by these shell projections.

## Completion and model context

The runtime actor owns its real started/terminal ShellInfo and bounded ShellOutput.
For completion input, call the existing `SessionStore.AdmitInboxAsync` with a
prepared SyntheticInboxPayload (and stable notification ID where the source uses
one). Reconcile before regenerating a retried payload. The actor decides whether
to wake; user shell completion in source admits with resume false.

SessionHistory now lowers non-background shell messages with the exact source
user-command/output wording. Background shell rows are omitted from model context
because their result enters through the completion synthetic. Typed message/context
read APIs still return the actual shell rows. Non-background metadata now passes
through the generic LLM message metadata contract without fabricated prompt text.

Completed shell history no longer blocks restart by itself. Running shell history
and shell background recovery markers remain guarded until their actual runtime
recovery is supplied; this publication API does not declare an unknown process dead
or fabricate an exit code.

No shell/process, database or model operation was executed for verification. Only
the isolated repository .NET 11 SDK/Core build and static source checks were used.
