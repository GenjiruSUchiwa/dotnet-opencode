# Multi-input admission and reconnect handoff

## Admission API

```csharp
SessionAdmissionAvailability CanAdmit(SessionId? session, SessionPromptInput? input = null);
SessionAdmissionSnapshot? ReadAdmission(SessionId session, MessageId item);
Task<SessionAdmissionSnapshot> AdmitPromptAsync(SessionId? session, SessionPromptInput input, CancellationToken ct = default);
Task<SessionAdmissionSnapshot> AdmitPromptAsync(SessionId? session, PromptInput input,
    InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default);
Task<SessionAdmissionSnapshot> RetryAdmissionAsync(SessionId session, MessageId item, CancellationToken ct = default);
```

`CanAdmit(...).Allowed` depends on the actual feed, authoritative hydration, deletion
and known form state, not model busy state or the existence of another input monitor.
The result includes all unconfirmed item IDs. Multiple indistinguishable uncertain
captures require an explicit ID rather than choosing an arbitrary old input.

The default mode is steer; deliberate queue is `Delivery = InboxDeliveryMode.Queue`.
`AdmitPromptAsync` returns on acknowledgement and does not hold the send semaphore
until model idle. Existing `PromptAsync` remains available as a compatibility
admission/observation stream. Both use the same sender and the same SSE receiver.

Each observed Session owns an item-ID-keyed admission ledger. Every entry preserves
its immutable original `SessionPromptInput`, current delivery mode, phase, admitted
fact, uncertainty, and error. Runtime queue/steer changes are separate from the
captured retry payload. Confirmation of one item never clears another item's
uncertainty. Later sends may proceed after a prior failed POST, as in the source
send chain; they do not overwrite its retained ID or payload.

Duplicate callers for an active item share its monitor with separate bounded
readers. A settled failed attempt can be retried without an old reader's cleanup
removing or changing the new attempt. These attempt identities are local lifecycle
bookkeeping, not distributed fencing or an exactly-once promise.

Inbox enqueue, delivery, cancellation and delivery-change events reconcile the
matching entry. A late POST response cannot resurrect a delivered/cancelled item
or reset its delivery mode. Cancellation before sending differs from a durable
server cancellation. If an acknowledged item disappears from authoritative pending
and visible history after a gap, `Consumed` reports that delivery is unconfirmed;
it does not fabricate successful execution or a cancellation event.

## Root guard and ownership

Bind the existing new parameter to the adapter:

```csharp
ReadAdmissionAvailability = (session, input) => adapter.CanAdmit(session, input);
```

Before clearing an ordinary prompt, capture it and call `CanSubmitPrompt(captured)`.
Replace the ordinary `_request != null` rejection with this check; retain the
configuration/form guards. `CanSubmitPrompt` reports an actual unavailable reason
without discarding the editor. The root now retains request ownership by a unique
request identity instead of replacing an older entry under the tab key. Item IDs
are attached by the first snapshot. Cleanup removes only that request.

The source-mode submit path uses the already implemented complete capture:

```csharp
var captured = CapturePromptAdmission(_input) with { Delivery = delivery };
if (!CanSubmitPrompt(captured)) return true;
// Existing root capture/clear timing stays here.
_request = new CancellationTokenSource();
_stream = StreamAsync(captured, _request.Token);
```

Failed restore remains conditional on an empty origin editor. Other requests,
another tab's draft, and an older uncertain item are not replaced by a later one.
The root owner controls this small key-handler change; no root clear handler was
edited in this pass.

## One-feed reconnect

`Feed` is a typed `SessionFeedSnapshot` with `Connecting`, `Live`, `Reconnecting`,
or `Disposed`, an epoch, and an error. Each `SessionObservationSnapshot` separately
exposes `Synchronization`: `Empty`, `Hydrating`, `Live`, `Stale`, `Failed`, or `Deleted`.
An open socket is not a claim that a Session has finished hydration.

The existing receiver reconnects sequentially with 250 ms–5 second backoff and a
connection handshake deadline. It never creates concurrent SSE subscriptions.
Feed loss keeps the last good transcript and editor and marks synchronization stale;
it does not publish an empty successful Session or interrupt server execution.

On a new connection, old-epoch HTTP completions cannot overwrite the new epoch.
Session events are buffered behind an authoritative Session/inbox/message/active
baseline. Buffered durable events are deduplicated within that batch, structural
starts are idempotent, and full projected text/tool-input values prevent replayed
old deltas from being appended twice. Subsequent full-value end events remain
authoritative. This does not recover live-only output emitted while disconnected
and does not claim atomic multi-endpoint snapshots or clustered execution ownership.

Compatibility per-input monitors finish acknowledged work on resynchronization
without inventing per-input model outcomes. The shared Session observer continues.
Admission remains unavailable until hydration completes; HTTP failures remain
typed failed/stale state rather than generic empty collections.

## Separately owned partials

Commands continue sharing `Observation.Admission`; its lifetime ends at POST
settlement, not execution completion. `NeedsReconciliation` now describes the
Session's synchronization requirement, not a single mutable uncertain-input slot.
Per-item uncertainty is available from `Admissions` and `ReadAdmission`.

Forms may call `NotifyPendingStateChanged()` under `_gate` after an HTTP-confirmed
cache mutation. It publishes the Session snapshots and wakes every item monitor.
The readonly `_pending` compatibility pointer is not an admission owner and must
not be used to replace or clear other items. The existing management invalidation
hook remains on the same receiver. Commands/Forms/Integrations files were not edited.

Verification is pinned .NET 11 isolated CLI compilation only. No tests, runtime,
SSE/HTTP requests, database/provider work, process adapters, or screenshots were run.
