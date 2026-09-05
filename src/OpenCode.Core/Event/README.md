# Durable Session Projections

Durable log, explicit replay and replay-owner claim APIs are now implemented for
the supported definition/projector set; see `DURABLE-LOG.md`. This does not make
the volatile event feed replayable or imply complete plugin/distributed replay.

This is a partial native port, not a replacement for the upstream Bus or Session
domain. The daemon owns database readiness and canonical schema bootstrap. These
files contain no DDL, migration records, service startup, or database repair.

## Server Handoff

`SessionEvents.Subscribe(Action<OpenCodeEvent>)` returns a disposable subscription
to actual committed events and separately published live deltas. Durable events
notify only after their projection transaction commits, in per-aggregate order.
Observer failures are isolated after commit, following Bus.notify's durable path.
Ephemeral text/reasoning/tool-input deltas carry no durable envelope and are not
inserted into event storage. Observers must enqueue and return, not synchronously
republish on the same aggregate. No event is reconstructed from a response DTO.

`CreateSessionAsync` now appends canonical `session.created.1` and projects its row
atomically. Reused Session IDs adopt the existing session without a second event;
orphaned aggregate state blocks recreation. Fork creation still requires its own
domain operation. Project discovery/adoption and creation policy inheritance are
not completed by this storage projector.

`SessionExecutionEngine.WakeAsync(sessionId, lifetime)` is advisory and requires a
host-owned cancellable lifetime. It acknowledges scheduling, not completion.
`AwaitIdleAsync` includes scheduled successors and terminal settlement. Host
cancellation records shutdown interruption and preserves claims; InterruptAsync
records user interruption and releases them. Default supported local execution now
uses the instruction/config integration described in `../Instructions/README.md`.
Separate HTTP admit/wake callers must use shared AdmitPromptAsync preparation,
not execution-readiness preflight. See `../Session/PROMPT-PREPARATION.md`.
`ResumeHostedAsync(sessionId, lifetime)` supplies explicit run/join semantics with
that same host cancellation reason and awaits settlement instead of scheduling
an advisory wake. It reuses the coordinator; it does not create a second owner.

## Source Mapping

- `packages/core/src/event/sql.ts`: uses `event_sequence(aggregate_id, seq,
  owner_id)` and `event(id, aggregate_id, seq, created, type, data)` unchanged.
- `packages/core/src/bus.ts`, `latestSequence`, `reserveSequence`, and
  `commitDurableEvent`: `EventTransaction` reads missing sequences as `-1`,
  allocates `latest + 1`, runs the projector, reserves the maximum sequence,
  and inserts the event inside one immediate SQLite transaction. The reservation
  leaves `owner_id` unchanged. Event IDs must be unique; append is not replay.
- `packages/schema/src/event.ts`: uses the existing `OpenCodeEvent` and
  `DurableEnvelope` types. The row stores a versioned type, such as
  `session.inbox.enqueued.1`, and only encoded data, not a serialized envelope.
  This path does not supply location or envelope metadata.
- `packages/schema/src/session-event.ts`, `InboxEnqueued`: the canonical data
  shape is `{ sessionID, inboxID, item: { type, payload, delivery } }`, version 1,
  aggregated by `sessionID`. It now uses Schema's corrected
  `SessionInboxEnqueuedEventData` and `InboxItem`, not second Core contracts.
- `packages/core/src/session/inbox.ts`, `reconcile`, `promotedFromMessage`, and
  `projectAdmitted`: matching pending or delivered user/synthetic IDs return the
  first payload, without publishing another event. Session/type mismatches fail.
  Pending retries return stored delivery; delivered retries use requested delivery
  because the message does not retain it. No retained enqueue history is needed.
- `packages/core/src/session/projector.ts`, `InboxEnqueued`: writes
  `session_inbox.enqueued_seq` from the event sequence and `time_created` from the
  event timestamp, then updates session recency in that same transaction.
- `packages/core/src/session/inbox.ts`, `projectDelivered`, and
  `packages/core/src/session/projector.ts`, `InboxDelivered`/`insertMessage`:
  `session.inbox.delivered.1` contains only `{ sessionID, inboxID }`. Projection
  deletes the pending row with `RETURNING`, then inserts a user/synthetic message
  from its original payload. Message sequence and created time come from delivery,
  not admission. The row update timestamp is projection write time; completion is
  absent. Failure rolls back consumption, message insertion, sequence, and event.
- `projectCancelled` and `projectDeliveryChanged`: cancellation consumes a pending
  queue/steer item; mode changes require the opposite current mode. These publish
  `session.inbox.cancelled.1` and `session.inbox.delivery.changed.1` respectively.
  Repeated cancellation or same-mode changes conflict, as upstream specifies.
- `serialized`, `pendingSteers`, `promote`, and `publish`: a process-local,
  per-session gate serializes promotion and pending mutations, but not admission.
  Each item gets a separate transaction. Deliver the initial ordered steer prefix
  before the first control. Only `Input` with no steers may deliver one queued
  item, then re-read and deliver newly arrived steers up to the first control.
  A previously delivered selection reconciles through its message without another
  event; a cancelled selection or mismatched identity raises a lifecycle conflict.
- `list` and `nextPromotable`: pending rows sort by `enqueued_seq`; preview prefers
  steers, then one queue item only in `Input` scope. Preview includes controls and
  is not a reservation. Message-list order still uses `session_message.seq`.

The native implementation holds the immediate SQLite writer transaction during
reconciliation as well as append. There is no notification path requiring the
upstream process-local publication lock. Commit is not cancellable after it starts;
earlier cancellation or projection failure rolls back on transaction disposal.

Unlike the default upstream `Bus.configured()` setting (`persist: false`), this
foundation always retains event rows, matching its `persist: true` path. This is
an explicit retention choice, not a claim that upstream always retains events.

## Integration Boundary

`SessionStore.AdmitInboxAsync(sessionId, id, payload, delivery, ct)` accepts
prepared canonical `UserInboxPayload` or `SyntheticInboxPayload`. Public URI-input
callers should use `SessionExecutionEngine.AdmitPromptAsync`; see
`../Session/PROMPT-PREPARATION.md`. The low-level store requires an
existing session and returns Schema's canonical `SessionInboxItem` only after
commit (top-level type, epoch-millisecond time, discriminator-free payload).

The public Core operations are `ReconcileInboxAsync`, `AdmitInboxAsync`,
`ListInboxAsync`, `NextPromotableInboxAsync`, `CancelInboxAsync`,
`ChangeInboxDeliveryAsync`, and `PromoteInboxAsync`. `InboxPromotable.Steer` is a
safe step boundary; `InboxPromotable.Input` is an idle input boundary. Invalid
identity or lifecycle transitions raise `InboxLifecycleConflictException` with the
item ID. Admission retries ignore new payload/metadata and do not mutate delivery.
Cancelled items have no retained admission identity and may be admitted again,
matching upstream's pending-only storage behavior.

The endpoint/domain owner must preserve caller message IDs across retries, call
reconciliation before preparing retried input, prepare new input before admission,
and schedule execution only after commit when
`resume != false`. No endpoint was changed. The runner must not
replace admission with a direct visible-message insert or report queued work as
completed.

The restricted native runner now uses this boundary and execution lifecycle
projections. Recovery remains unsupported. The runner must promote only at safe boundaries, reload projected
history afterward, reset agent step allowance when new input is promoted, and
handle control previews through their domains. It must not call `AddMessageAsync`
for durable sessions. A promotion call can commit a prefix before cancellation or
a later failure, just as the upstream per-item publication loop can.

Admission rejects unsequenced direct-SQL history; it does not fabricate an event
prefix or silently reserve legacy positions. All direct session mutation APIs now
check durable sequence state inside the same immediate transaction as the write.

## Assistant Event Family

`SessionStore.AppendAssistantEventAsync(type, data, ct, eventId)` accepts an
unversioned canonical type and its encoded data object, not an event envelope.
The data includes `sessionID` and `assistantMessageID`. The Core definition owns
the durable version. The return value is the existing Schema `OpenCodeEvent`,
returned only after commit. There is no dependency on the provider adapter API.

Implemented definitions from `packages/schema/src/session-event.ts`:

- `Step.Started`, `Step.Streamed`, `Step.Ended`, `Step.Failed` (version 1).
- `Text.Started`, `Text.Ended` (version 1).
- `Reasoning.Started`, `Reasoning.Ended` (version 1).
- `Tool.Input.Started`, `Tool.Input.Ended`, `Tool.Called` (version 1).
- `Tool.Success`, `Tool.Failed` (version 2).

`AssistantEventData` now delegates decode and encode to Schema's completed event
DTOs. Schema owns required fields, optional values, identifiers, finish reasons,
usage, ordinals, structured errors, and canonical terminal tool content. Core owns
only selected event registration/version and projection, not a duplicate validator
or public event union. The encoded object contains canonical data fields.

`AssistantProjector` maps `packages/core/src/session/message-updater.ts`:

- `updateOwnedAssistant` associates each event with its explicit assistant ID and
  session. An absent or wrong-session assistant produces no message mutation, as
  upstream specifies. A new start colliding with another message fails insertion
  and rolls back the event.
- `session.step.started` reuses an existing assistant without changing created
  time, content, or sequence; clears retry/error/finish/provider completion state;
  and applies agent/model and an optional starting snapshot. A new assistant
  closes only the newest incomplete assistant and uses the new event sequence.
- `session.step.streamed` records the response-body boundary independently from
  terminal completion. End/failure applies finish, provider state, usage, snapshots,
  and structured error according to the upstream branches.
- Text end selects the latest text; reasoning end selects the latest incomplete
  reasoning. Ordinals are event facts, not content-array indices. Reasoning timing
  and provider state follow the upstream optional-field behavior.
- Tool events target the latest matching tool ID inside assistant content. Raw
  input starts/ends in streaming state, called records parsed input and running
  time, and success/failure records structured content/error and completion.
  Executed flags accumulate with logical OR at terminal boundaries. Terminal
  tool metadata comes from that event, never from live progress history. There
  are no separate tool messages or inferred tool results.

SQL mapping follows `session/projector.ts`'s `getAssistant`,
`getCurrentAssistant`, `updateMessage`, `insertMessage`, and `applyUsage`:
message updates retain sequence and original creation time; inserts use event
sequence; update timestamps are projection write time. Step end adds usage to the
session. Step failure adds it only when both cost and tokens exist. Usage projection
is independent of whether the target assistant was found, and does not change
session recency. Event append, message changes, and usage share one transaction.

The JSON projection preserves provider state, timestamps, snapshot fields, and
structured terminal tool content in stored history. Event input now uses canonical
DTO validation; Schema has also completed assistant/tool DTOs for typed consumers.

This API appends new facts; it is not idempotent event replay. Reusing an event ID
fails. Supplying a fresh ID for a repeated terminal event adds usage again, just
as a new upstream publication does. The runner must maintain physical-attempt
boundaries and publish each terminal fact once; do not wrap it in blind retries.

Unsupported by this assistant API: all deltas and tool progress (live-only), retry
scheduling, content replacement, execution start/terminal outcomes and claims,
standalone usage events, compaction, shell, instructions, revert, and other Session
event families. These fail explicitly, not through a no-op fallback. No live
notifications, envelope location/metadata input, replay, tool execution, or provider
orchestration is implemented by this API. The restricted runner described in
`../Session/README.md` now assembles full text/reasoning/raw-tool
input at ended boundaries, records streamed separately, and publishes terminal
step events after settlement. No endpoint was changed.

## Explicit Requirements

- Schema now supplies corrected creation, enqueue, and selected assistant data
  contracts. Creation orchestration and its projector are still separate work.
- Schema's prompt and UserMessage attachment contracts are now canonical. Prepared
  files/agents/skills and metadata survive admission, reconciliation and promotion.
  Shared local URI preparation is implemented; unsupported execution metadata and
  placement remain explicit. See `../Session/PROMPT-PREPARATION.md`.
- Domain: move payloads now use canonical `LocationRef` and controls can be listed,
  previewed, cancelled, or switched between queue/steer. Admission and delivery of
  controls remain unsupported until their operation-specific lifecycle is ported.
  Steer promotion stops before either control; a queued control throws without
  consuming it. This intentionally does not reproduce generic upstream `publish`'s
  move consumption without the missing move operation. Compaction is also never
  treated as user input. No control is silently skipped to deliver later input.
- Domain: staged revert commit must follow successful prompt preparation and precede
  new admission. The real callback and version-1 event projections now exist; the
  low-level admission guard still rejects bypassing the operation. See
  `../Session/REVERT-SNAPSHOTS.md`.
- Domain: creation is now event-projected. Deletion, rename, agent/model selection,
  forks, and other Session mutations still require their durable domain operation.
  Deletion, rename, agent/model changes, and direct message insertion
  reject aggregates with durable sequence state. They do not assume session
  deletion cascades into event tables or attach old event history to a new row.
- Bus: no replay/divergence checks, replay ownership claims, public event manifest,
  log/follow stream, or Location-aware live routing are implemented. Process-local
  post-commit subscriptions and selected ephemeral publications are available.
  Execution claim lifecycle is implemented separately in ExecutionProjector.
  Multiple internal appends can share a transaction, but there is no
  public `publishAll` contract or cross-aggregate batching guarantee.

No tests or runtime database access were performed for this pass. Verification is
limited to an isolated Core build and static diff checks.
