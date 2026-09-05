# Restart recovery for compaction and movement

Recovery now admits the source-supported local control cases through the same
coordinator and normal runner. It does not run a separate control loop or replay
historical operations. Both top-level recovery and recovered subagent drains use
the same capability check and durable attempt accounting.

## Crash boundaries

| Durable state at restart | Recovery behavior |
| --- | --- |
| Compaction remains in `session_inbox` | Preserve queue/steer order. Normal control delivery atomically consumes the inbox row and publishes Compaction.Started with its input ID. |
| Compaction Started committed, but no Ended/Failed | The control is already consumed. Do not recreate it, resume its old stream, use its partial summary/recent text as a checkpoint, or invent a terminal event. |
| Compaction Ended committed | Reuse the completed checkpoint and its atomically advanced instruction epoch. Do not repeat the committed compaction. |
| Compaction Failed committed | Retain the failure. It is not a summary baseline and does not advance the instruction epoch. |
| Local move remains in the inbox | Require the host's real SessionMovement service. Use normal ordered delivery and transport closure, then commit InboxDelivered and Moved together. |
| Moved committed | The control is gone and stored placement is authoritative. Reload destination services; do not reconstruct a move from location-switched history. |

The TypeScript restart path publishes its restart synthetic and resumes the normal
runner. It does **not** synthesize `Compaction.Failed` merely because an old running
row exists. Accordingly, an interrupted row retains its last recorded status; it
must not be mistaken for a live owned summary stream by a UI. A later normal attempt
may compact afresh if the ordinary threshold requires it. That attempt has its own
Started fact, and the source projector settles the latest running compaction.

`SessionHistory` includes only completed compaction checkpoints in model content.
`CompactionPlan` excludes compaction rows from serialized conversation and selects
only a completed prior summary. `CompactionProjector` advances the epoch only on
Ended. Recovery leaves initial/current instruction hashes intact rather than
resetting or promoting unfinished summary state.

## Ordering and placement

No control is promoted during generic physical-attempt retry/continuation. At the
next normal boundary, existing queue/steer selection and the shared inbox gate
decide eligibility. A cancelled or changed control is re-read, not executed from
an earlier preview. Manual compaction and move delivery remain atomic with their
respective durable lifecycle facts.

The runner receives the injected `SessionMovement`; recovery does not construct a
new permission map, tool registry, provider loop, or Location service. A pending
local move can be processed before source-directory instruction/model resolution,
including when that old directory is no longer usable. Once moved, the runner
reloads stored placement, model/config, permissions/tools/MCP and instructions.
The instruction epoch survives; destination changes are chronological deltas.

The current HTTP adapters dispose responses inside each attempt. No per-Session
transport survives that boundary, so the existing closure callback is valid for
this supported transport set. Persistent transport support must supply real channel
closure before widening that contract; recovery does not invent WebSocket cleanup.

Source control-only behavior is preserved: a move or manual compaction clears the
forced-drain intent rather than forcing an extra model call. New pending input or
an actual logical-step continuation determines subsequent execution.

## Remaining guards and handoff

- Pending moves remain blocked **before resume accounting** if the engine has no
  SessionMovement composition. Current or destination workspace identities remain
  unsupported; they are not silently treated as local.
- Unknown compaction states, running shell history, unhandled shell/plugin job
  markers, unsequenced projections and unowned child recovery retain their guards.
- Do not infer shell completion from a restart or a control transition. Generic
  shell-job recovery still needs the source Job.Background descriptor (`id`, stable
  `notificationID`, `status`, optional output/error, and recovery `{ kind: "shell",
  sessionID, shellID, command }`) plus the Shell owner's source notification mapping.
  No Shell runtime or process reconciliation is duplicated by this pass.

Source mapping: `session/execution/restart.ts:prepareResume`,
`session/runner/llm.ts:advanceToStep`, `session/execution.ts:Moved`,
`session/compaction.ts:planContent/execute`, `session/message-updater.ts`, and
`session/projector.ts`.

No tests, Sessions, controls, database operations, filesystem/Git operations, models,
tools, processes or network calls were executed as verification. Verification is
isolated pinned .NET 11 SDK/Core compilation and static source checks.
