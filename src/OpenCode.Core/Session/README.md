# Restricted Native Execution

The engine now uses durable admission, promotion, projected history, one physical
structured stream, assistant projections, and execution lifecycle projections.
It is not a full agent runtime.

## Entry Points

- `AdmitAsync` records text input with optional caller message ID and queue/steer
  delivery. It does not execute a model.
- `PromptAsync` retains the existing first five arguments. Optional arguments after
  `ct` add `messageId`, `delivery`, and `resume`. `resume: false` is admission-only.
- `ResumeAsync` joins active work or drains eligible pending input. Without pending
  input, it explicitly forces another logical step from supported projected history.
   Advisory wakes do not force such a call. Surviving claims require the explicit
   startup recovery entry point below.
- `ResumeHostedAsync(sessionId, lifetime)` uses the same explicit run/join path
  with a required cancellable host lifetime. It waits for settlement, unlike
  advisory WakeAsync. If it owns execution, host cancellation records shutdown
  and preserves the claim; it never changes an existing owner's cancellation reason.
- `RecoverSuspendedAsync(lifetime, maxAttempts)` is the explicit managed-startup
  recovery entry point. See `RETRY-RECOVERY.md` for ownership preconditions,
   accounting, supported top-level recovery, and unsupported related domains.
- `RequestCompactionAsync` admits a manual control and schedules a host-owned wake.
  `SessionStore.AdmitCompactionAsync` is admission-only. See `COMPACTION.md` for
  control ordering, checkpoint projection, automatic compaction and overflow rebuild.
- Movement drain integration requires the host to pass its registered `SessionMovement`
  as the final `SessionExecutionEngine` constructor argument. The domain uses the same
  database/store/permission Location map, not a second coordinator. Pending moves call
  `TryDeliverAsync` at Input boundaries on entry/idle and Steer boundaries during
  continuation. A null result rechecks pending work. Delivery consumes the control
  without a prompt message; the moved event supplies canonical location history.
  The runner then reloads placement and destination model/tools/MCP/instructions.
  Existing instruction epochs survive; destination changes become chronological
  deltas. A move alone does not force a model call. Existing HTTP adapter streams
  dispose responses before the boundary; `CloseSourceHttpTransportAsync` documents
  that no Session channel remains. Persistent transport support must replace it
  with a real Session-channel close before movement is published.
- `InterruptAsync` rejects unknown sessions, returns false for idle/unowned work,
  and cancels only the active process-local owner. `IsActive` includes settlement.
- `ActiveSessionIds` is an immutable coordinator-owned snapshot. Protocol's
  SessionActive value is only `{type:"running"}` for each owned ID. Pending inbox
  items, durable claims, and not-yet-registered scheduled wakes are not inferred
  as active; stopping/cleanup/settlement owners remain included until released.
- `WithIdleMutationAsync` reserves the Session against new admissions and owner
  registrations, interrupts current work, waits for in-flight admissions and owner
  settlement, then executes the domain mutation callback. Reservation is released
  on cancellation/failure as well as success. It holds no inbox, publication, or
  SQLite semaphore while waiting. The mutation owner must keep its related-domain
  preflight before interruption and transactional recheck before delete/purge.
  This is a native same-process reservation around the source removal ordering,
  not clustered fencing or recursive child/job lifecycle support.
- `ReserveRemovalAsync(sessionId, ct)` exposes the same reservation as an
  `IAsyncDisposable` lease. It returns only after in-flight admission/registration,
  active execution settlement and scheduled wakes have finished. Hold it through
  the existing transactional removal callback, then dispose it. Acquisition failure
  or cancellation releases the reservation; after acquisition, the caller's
  `await using` owns release even if deletion fails. No inbox gate is held during
  interruption or settlement. Calls from the same Session execution/admission fail
  instead of awaiting themselves. This API does not check existence or delete data.
  The mutation owner must run related-domain preflight before acquisition and
  transactional existence/related-domain checks again while holding the lease.

Default execution uses the real local instruction composition described in
`../Instructions/README.md`. The instructionless constructor flag has been removed.
SDK `ExecutionCapabilities` and engine `Capabilities` report instruction-aware
text/media execution, with tools available only through host composition.
`CheckReadinessAsync(sessionId)` is an optional explicit inspection API, not an
admission gate. `AdmitPromptAsync` reconciles IDs, prepares local attachments and
skills, then admits durably before model/agent/instruction execution resolution.
HTTP callers should use that shared API and wake only after it returns; see
`PROMPT-PREPARATION.md` for source mapping and remaining limits.
Implicit-local config discovery uses the session
directory, not process cwd, through ConfigLoader.LoadDocument. Explicit workspaces
remain unsupported. A configured system-only agent can replace the source base
system prompt; richer selected-agent options require the full agent producer.

Tool execution now uses the paired, host-injected ToolLocationFactory and shared
PermissionLocationMap. The exposed legacy ToolRegistry remains compatibility-only.
Without host composition the SDK has no local tools. With it, readiness and every
physical attempt acquire the authoritative Location, advertise the captured
snapshot, and execute that same snapshot through its real leaf policy. See
`TOOL-EXECUTION.md` for callback wiring, settlement, bounds and remaining limits.

## Source Mapping

- Automatic title generation now uses `SessionTitleService` after visible input
  promotion, with source small-model selection, primary fallback, usage recording,
  and conditional rename. It does not delay admission or create an assistant step.
  See `TITLE-GENERATION.md` for host injection and empty-rename routing.

- `SessionStore.EnsureProjectAsync` now delegates to the shared
  `CatalogLocation.ResolveAsync` used by provider catalog resolution. Creation with
  no explicit project binding uses its canonical project ID, resolved Location
  directory, and project-relative subpath. Windows database paths normalize to
  forward slashes and session readers restore platform paths. No random project
  ID is generated. Existing Session IDs are still adopted without remapping their
  project binding. Historical random-ID adoption, worktree announcements, and
  project/worktree event orchestration remain separate work; no such event is
  fabricated here. The resolver was not invoked during verification.

- `session/run-coordinator.ts`: process-global Session-ID ownership, joins for
  concurrent resumes, coalesced prompt wakes, and independent sessions. Ownership
  includes settlement. Stopping/settling owners refuse new work; the new caller
  waits before owning a successor. The idle override check, admission, and ownership
  registration share a per-session gate, so rejecting an active model override
  leaves no executable input behind. Joiner cancellation does not cancel the owner;
  initiating caller cancellation/disposal does. No Task.Run or detached
  CancellationToken.None execution is used.
- Matching user IDs reconcile before new-admission preparation. Retry
  payload/model overrides are ignored; active retries join the original owner.
  Completed retries with no eligible work do not load current instruction config.
  Actual execution of still-pending work may still fail its execution requirements;
  this does not replace or re-admit the original payload.
- `session/execution.ts`: canonical started event and time_suspended claim commit
  atomically. Success/failure/user interruption advances idle time monotonically,
  releases the claim, resets resume accounting, and preserves session recency.
  The newest incomplete assistant's retry is cleared. Host lifetime cancellation
  on WakeAsync preserves claim/idle state as shutdown; explicit InterruptAsync
  releases the claim as user interruption. AwaitIdleAsync includes settlement.
- `session/store.ts`: claim/release preserve time_updated. Surviving claims are
   rejected by ordinary starts; explicit startup recovery accounts for them. Legacy projection
  sequence checks run before lifecycle append and inside the started transaction.
- `session/runner/llm.ts`: select provider and validate history before delivery,
  commit the instruction delta, promote at the idle boundary, then load stored
  instruction baseline and projected history in one transaction. The real system
  prompt and epoch baseline become separate SystemParts. A normal-stop step
  reevaluates durable pending work. Settled local tool calls may require a durable
  continuation step even without new input; queued input is not promoted mid-work.
  There is no in-memory conversation or generic tool loop.
- `session/history.ts` and `runner/to-llm-message.ts`: unpaginated sequence-ordered
  projected history lowers text user/synthetic/skill/system and settled assistant
  text/reasoning/tool state. Model/error rules govern provider metadata reuse.
  Local results become tool-role messages; hosted results stay in assistant content.
  Completed compaction checkpoints lower using the source historical-context wrapper.
  Unsupported attachment metadata, shell history and unresolved tools fail explicitly.
  Partial assistant text/reasoning is retained after stale-tool settlement.
- `session/runner/step.ts`: SessionAttempt contains exactly one explicit
  Client.StreamAsync(request, ct) call. It consumes the stream, flushes fragments,
  records streamed separately, settles unsupported tools, and publishes one step
  ended/failed event. Pre-output failures can return a retry decision before terminal
  projection; another physical attempt is owned by the same logical step.
- `runner/publish-llm-event.ts`: fragment identity/order checks, per-kind ordinals,
  provider-state merges, authoritative end replacement, and complete terminal
  content. Tool buffers preserve raw input and confirmed call identity; parsed
  input is copied only at ToolCall, never inferred from deltas. Non-object arguments
  are rejected, preserving raw input and a tool.input-json failure instead of
  inventing a value wrapper. Execution uses only the captured host-composed snapshot,
  never the legacy registry.
- `session/usage.ts`: finite/nonnegative noncached input, visible output, reasoning,
  and independent cache counters. Cost uses selected catalog pricing with the
  source's empty-cost-table fallback of zero, not measured billing. Finish values
  use an explicit Core/Llm-to-canonical string mapping.

The legacy string stream emits complete durably recorded text blocks, not token
deltas. Authoritative TextEnd replacements therefore cannot duplicate/retract
already-emitted text. Reasoning/tool facts remain in history. Joiners observe
blocks emitted after joining.

## Remaining Integration

- Prompt/Resume ownership is caller-scoped. WakeAsync schedules advisory non-forced
  work with a required host-owned cancellable lifetime, never CancellationToken.None.
  New input remains durable on owner failure/cancellation. Active model overrides
  fail; durable model switching is separate work.
- Publication and cleanup use bounded, owned 15-second tokens independent of work
  cancellation and are awaited. Persistence/cleanup errors propagate. There is no
  guaranteed terminal recording; crashes or failed terminal commits retain claims.
- Normal stop, length and tool-call finishes retain their actual canonical values.
  Local tool settlement controls continuation. Unknown/error and content-policy
  failures remain explicit. Agent step allowances, pre-output retry/backoff and
  one overflow compaction rebuild per logical step are implemented. Full-context
full-context transport recovery remains separate work. Host-composed filesystem
snapshots and revert operations are implemented in `REVERT-SNAPSHOTS.md`.
Explicit incomplete-output and Operation.Read transport failures now support bounded durable continuation;
see `RETRY-RECOVERY.md`.
- Provider identity and transport metadata namespace are separate. Live state and
  history use `ResolvedModel.ProviderMetadataKey`, while model reuse checks compare
  configured provider/model IDs. No namespace is guessed from an alias.
- Full guidance/plugin/MCP producer resolution, output retention scheduling,
  complete context lowering and background jobs remain incomplete. Startup recovery
  supports only the documented top-level subset. Manual compaction admission is
  supported; local attachment admission is supported through `AdmitPromptAsync`.
  See the movement domain and prompt preparation documents for their remaining limits.
- SDK exposes capabilities, per-session readiness, admission, IDs/delivery/resume
  options, and resume/interrupt. Default supported local text execution is enabled
  through instruction-aware execution and epoch assembly; there is no opt-in
  that bypasses instructions. Server endpoints remain separately owned and must
  use shared preparation/admission and supply host lifetime to advisory WakeAsync.

Verification is isolated Core build and static diff checks only. No tests,
application/provider execution, runtime database access, benchmarks, or commits.
