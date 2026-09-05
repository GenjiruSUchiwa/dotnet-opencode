# Session Transfer

## Endpoint integration

Register `OpenCode.Core.Session.Transfer.SessionTransfer` with the existing `IDatabase`.
Call `ForkAsync(new SessionForkRequest(sessionId, boundary), cancellationToken)` and return
its canonical `Schema.SessionInfo`. `boundary` is the existing `Schema.ForkRequestBoundary`.
No Bus, notification bridge, store, engine, or public event registry is added here.

Map `SessionMutationNotFoundException`, `SessionForkMessageNotFoundException`, and
`SessionForkEmptyException` to the corresponding protocol errors. Their IDs are available
as properties. Cancellation and projection failures propagate; the existing EventStore
rolls back before notifying observers.

Register `SessionMovement` with the same `IDatabase`, `SessionStore`, and authoritative
`PermissionLocationMap` used by the host. After the two Core integrations below, call
`RequestAsync(request, executionEngine, hostLifetime, requestCancellation)` for movement.
`AdmitAsync` is the admission-only API. `PrepareAsync` resolves and validates the destination
without admitting a control (project resolution can persist project identity).

Explicit workspace source/destination placement fails before admission. The current source
`Session.move` contract has only directory, workspaceID, and delivery: it has no file-change,
snapshot-transfer, or copy option. This operation does not copy or revert files. An endpoint
must reject unsupported options rather than silently ignore a transfer request.

## Fork mapping

- `core/src/session.ts:322-361`: validate the parent; `before` selects the exact parent
  message, while request `through` resolves the latest message by sequence. A missing
  `before` boundary and an empty `through` source have different errors.
- `schema/src/session-event.ts:167-180`: append one `session.forked`, version 2, on the new
  session aggregate with the resolved canonical `ForkBoundary`, current instruction hash
  map, and instruction-entry snapshot. Do not synthesize `session.created` or copied events.
- `core/src/session/projector.ts:106-225`: project a top-level session (`parent_id = NULL`)
  with separate fork provenance. Copy placement, title with incremented fork suffix, agent,
  model, metadata, and version. Generate a fresh adjective/noun slug. Usage, lifecycle state,
  permissions, share state, revert, and pending work are not inherited.
- Copy only settled assistant, shell, and compaction rows. Preserve sequence numbers,
  payload JSON (including metadata and embedded references), and both row timestamps.
  Row IDs are `msg_<fork-event-suffix>_<source-seq>`, not the parent's message IDs.
- Reserve the maximum selected source sequence BEFORE settling filters, including a tail
  containing only unsettled rows. The fork event retains its own initial aggregate sequence.
  Later appends use the reserved high-water mark.
- `instruction-state.ts:119-137,176-182` and `instruction-entry.ts:24-67`: freeze newest
  parent values even for an earlier history boundary. Initial/current hashes are equal in
  the fork, with epoch/through sequence set to the fork event sequence. Preserve removed
  entries and initialize entry timestamps at fork creation. Shared blobs stay shared.
- A staged canonical `SessionRevert` on the parent is neither committed nor copied.
  History selection follows the source projector, not the UI's reverted-history view.

The native implementation keeps selection, instruction snapshot, append, projection, and
result read in one existing EventStore transaction. Upstream snapshots instructions in a
separate read transaction before publication. The native transaction avoids opening a
second SQLite writer connection. The copy uses one ordered INSERT SELECT rather than
500-row application batches; it is strictly inside the durable event projector.

## Movement implementation

Implemented from these source operations:

1. `session.ts:461-490`: resolve trimmed/tilde/relative destination against current Location;
   validate existence, directory type, project identity/subpath, and destination Location
   service availability. Preserve cancellation rather than converting it to unavailable.
2. `session.ts:491-514`: under the existing inbox serialization lock, reload source placement.
   If its directory is gone or not a directory, cancel pending move controls in enqueue order
   and append `session.moved` atomically. Otherwise admit a fresh move control with canonical
   `LocationRef` and default steer delivery. Wake execution only after commit.
3. `MoveInbox.cs` extends the existing partial `SessionAdmission`, reusing its private event
   definitions, reconciliation, and inbox lock. Pending same-session/type IDs return the
   original payload and delivery; cross-session/type and visible-message ID reuse fail.
   Consumed move IDs are not retained as an idempotency ledger, matching source controls.
   New requests with the same destination are not coalesced. The existing generic promotion
   already stops steer batches before controls and refuses to consume queued controls.
4. `TryDeliverAsync` (`runner/llm.ts:63-99`): under the same inbox lock, close the source model
   transport BEFORE atomically appending `session.inbox.delivered` and `session.moved`.
   Delivery consumes the pending row without inserting a user/synthetic message.
5. `MoveProjector` (`projector.ts:464-479`, `message-updater.ts:103-116`): append the canonical
   location-switched message using event-derived ID, metadata, and previous placement read
   BEFORE updating placement. Update directory, workspace (including clearing it), subpath,
   optional project, and recency. Do not clear or advance instruction state.

## Required Core integration

The batch compiles independently, but queued move delivery is not enabled until its owners
make these changes. Do not route the endpoint to RequestAsync before wiring the drain.

1. In `Event/SessionInboxOperations.cs`, `ProjectDeliveredAsync`, change the control branch
   `if (type == "compaction")` to `if (type is "compaction" or "move")`. Its existing
   `DecodePayload` already supports `MoveInboxPayload`. This is the only shared admission
   projector change needed; do not define another delivered/enqueued event.
2. Inject the existing `SessionMovement` into `SessionExecutionEngine` and replace the pending
   move rejection with this public hook:

```csharp
Task<SessionMoveDelivery?> SessionMovement.TryDeliverAsync(
    SessionId sessionId,
    InboxPromotable scope,
    Func<CancellationToken, Task> closeSourceTransport,
    CancellationToken ct = default);
```

Call at a safe boundary, before instruction preparation or generic prompt promotion. Use
`Input` at Location entry or idle, otherwise `Steer`. The hook reselects under the shared
inbox lock, so a null result means re-evaluate pending work, not that the earlier peek still
applies. It cannot jump a preceding prompt or compaction control. The callback must close
the session's source model transport before publication; do not invalidate shared Location
services or substitute an unverified no-op. Failure or cancellation leaves the item pending.

After a non-null result, reload placement and re-enter Location-scoped dependencies, matching
`execution.ts:82-103`. Preserve step continuation only for `!entering && continuing`; otherwise
reset step to 1. Do not force a model call merely because movement completed. Return to the
boundary loop so controls can run consecutively. Destination instruction observation must
use the retained source epoch; ordinary instruction preparation writes chronological deltas.
No new coordinator, idle reservation, or interruption is needed for queued movement.

Endpoint API signatures:

```csharp
Task SessionMovement.RequestAsync(SessionMoveRequest request,
    SessionExecutionEngine execution, CancellationToken lifetime, CancellationToken ct = default);
Task<SessionMoveAdmission> SessionMovement.AdmitAsync(SessionMoveRequest request,
    MessageId? id = null, CancellationToken ct = default);
Task<MoveInboxPayload> SessionMovement.PrepareAsync(SessionMoveRequest request,
    CancellationToken ct = default);
```

`SessionMoveAdmission.Moved` identifies missing-source immediate recovery; otherwise `Item`
contains the durable control. `RequestAsync` wakes after either path commits. Map
`SessionMoveDestinationException.Failure` to destination-not-found, not-directory, or
unavailable protocol errors. `SessionMutationNotFoundException` and cancellation propagate.
The optional admission ID is for domain callers; the source endpoint creates a fresh ID.

Project resolution reuses `CatalogLocation.ResolveAsync` and checks the host Location map
against that identity. Its existing native limitations still apply: it does not publish the
TypeScript Worktree resolution/adoption events. Transfer does not create a second project
resolver or silently claim to implement that separate domain.

The existing `EventStore` calls the supplied projector directly; fresh forks need no extra
projector registration. If its owner adds canonical replay dispatch, register
`ForkProjector.Forked`/`ProjectAsync` and `MoveProjector.Moved`/`ProjectAsync` in that existing
dispatch. Fork replay still requires the
parent projection and boundary, just as the TypeScript projector does. Do not add a second
Bus or registry here. Public schema event-manifest integration belongs to its owner.

Export/import from `core/src/session/transfer.ts` is a distinct domain and is not implemented
here. Its settled-history import must not be used as a substitute for forking.
