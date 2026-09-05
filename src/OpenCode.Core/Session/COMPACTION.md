# Compaction and Overflow Rebuild

## Core Entry Points

`SessionStore.AdmitCompactionAsync(sessionId, id, delivery, ct)` admits a manual
control without invoking a model. `SessionExecutionEngine.RequestCompactionAsync`
adds an advisory wake under a required host-owned lifetime. A returned inbox item
acknowledges admission, not summary completion. Server endpoint wiring is separately
owned; these Core APIs do not add a protocol operation.

Admission follows `session/inbox.ts`: reuse the same pending compaction ID, reject
an ID used by another Session/type or an already delivered message, and return the
existing pending compaction when a different ID is submitted. First delivery mode
wins. One pending compaction is admitted per Session under the inbox serialization
gate and SQLite transaction. Generic prompt promotion stops at controls.

The runner consumes an eligible manual control and appends
`session.compaction.started.1` in one transaction with `session.inbox.delivered.1`.
The checkpoint uses the inbox ID. Queue controls wait for an idle boundary; steers
run at safe step boundaries. Cancellation/failure after delivery leaves a failed
checkpoint rather than restoring the consumed inbox row. A crash can leave a running
checkpoint; restart integration for this case remains blocked.

## Summary Request

`CompactionPlan` ports the source anchored-summary template, previous-summary merge,
user-aligned recent-context selection, four-characters-per-token estimate, and
2,000-code-point tool-output truncation. Original messages are never deleted.
System updates are excluded from summary selection, as in the source; successful
compaction makes current instruction values the new epoch baseline instead.

`SessionCompaction` makes one explicit `StreamAsync` call with the source summary
prompt in a User message, no conversation system baseline, and no tools. Manual
model resolution occurs after planning and receives the actual Session ID. Text
deltas form the summary and emit ephemeral compaction deltas. This auxiliary request
does not create a normal assistant step or consume an agent-step allowance. Provider
errors or empty summaries publish failure without moving the history boundary.
The summary path rejects tool calls rather than executing them. Provider metadata
does not become checkpoint text.

Usage comes from step-finish events and the catalog cost tiers. It is recorded with
`session.usage.recorded.1`, source `compaction`, including on stream failure or
interruption when usage was received. Normal assistant attempts now use the same
cost calculation. Missing cost entries use the source zero-cost fallback; this is
not measured billing. Metadata is read through the provider owner's existing catalog
API; it is not yet part of the atomically resolved model value.

## Projection and Rebuild

Started/ended/failed project canonical compaction messages. Ended stores only reason,
summary text and recent context; the running checkpoint supplies identity and time.
The same transaction advances `instruction_state.epoch_start` and `through_seq` to
the ended-event sequence and copies current values to initial values. Failed and
running checkpoints do not advance the epoch or truncate visible history.

Context reads include the newest completed checkpoint and subsequent projected
messages. Lowering uses the exact source `<conversation-checkpoint>` wrapper. Full
message listing still includes the earlier history. This is an event-derived
boundary, not synthetic history erasure or an implemented replay service.

Automatic compaction uses the source auto/buffer/keep settings and latest assistant
token usage against context/input/output limits. Missing required model limits fail
explicitly. A pre-output context-overflow exception or provider-error event can
request one successful compaction rebuild per logical step. The retry object and
step allowance are retained; the assistant ID changes, and model/tools/instructions,
stored baseline and history reload without inbox promotion. A second overflow fails
normally. Failed compaction is not a generic retry or an unbounded rebuild loop.

## Owner Handoffs and Limits

Core uses the canonical Schema `SessionCompaction*EventData` DTOs and binds its
projectors to `SessionEventDefinitions.Compaction` type/version/aggregate/codecs:

- `session.compaction.started.1`: `sessionID`, reason `auto | manual`, `recent`, optional `inputID`.
- `session.compaction.ended.1`: `sessionID`, reason `auto | manual`, `text`, `recent`.
- `session.compaction.failed.1`: `sessionID`, reason `auto | manual`, canonical `error`, optional `inputID`.
- Ephemeral `session.compaction.delta`: `sessionID`, `text`, no durable envelope.

Core validates projected messages using the canonical Schema converter. Delta uses
the shared ephemeral definition. The shared-only `session.compacted` contract is
not emitted by this path. No extra epoch fields are persisted in event data.

Plugin compaction transforms and model-request hooks remain explicit unsupported
configuration. The auxiliary request uses the current provider adapter rather than
a complete native port of SessionModelRequest. Post-output incomplete-stream
continuation, retry-full/rotate-and-retry-full transport rejection, WebSocket channel
checkpoints/rotation and transport delivery evidence remain unimplemented.

Pending compaction and host-composed local move controls now use the normal runner
during startup recovery. Running/failed compaction history is not a summary baseline
and does not cause a consumed manual control to be re-enqueued. No terminal event is
invented for a process-death gap. See `CONTROL-RECOVERY.md` for crash boundaries and
the remaining workspace/shell/plugin barriers, and `Subagents/RECOVERY.md` for child recovery.
Completed checkpoints and location-switched history are accepted because native
history lowering already supports them; their controls are not executed again.

Verification is isolated Core/Server compilation only. No tests, model/tool calls,
application execution, database operations, or runtime recovery were performed.
