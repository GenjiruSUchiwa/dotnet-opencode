# Generic Job runtime and durable marker handoff

## Implemented service

`JobRuntime` is the source-shaped host-wide job registry, shared by domain adapters. It accepts execution delegates; it does not contain another Session/model runner, process registry, or subagent implementation.

```csharp
var jobs = new JobRuntime(realBackgroundStore, hostLifetime);
var shellJobs = new OpenCode.Core.Shell.Jobs.ShellToolJobs(
    jobs, existingSessionStore, existingExecutionEngine, hostLifetime);
// Supply shellJobs to the existing ShellTool constructor's jobs parameter.
```

Use one JobRuntime per host/channel, not one per Location or tool call. The lifetime must be cancellable and owned by the host. Keep stores/Locations alive while it cancels and joins Run delegates. Run delegates must honor their job cancellation token and own/release their actual domain resources.

Public operations:

```csharp
Task<JobInfo?> GetAsync(string id, CancellationToken ct = default);
Task<JobInfo> StartAsync(JobStartInput input, CancellationToken ct = default);
Task<JobWaitResult> WaitAsync(string id, int? timeoutMilliseconds = null, CancellationToken ct = default);
Task<JobBlockResult?> BlockAsync(string id, SessionId sessionId, CancellationToken ct = default);
Task<JobInfo?> BackgroundAsync(string id, CancellationToken ct = default);
Task<IReadOnlyList<JobInfo>> BackgroundAllAsync(SessionId sessionId, string? type = null, CancellationToken ct = default);
Task<JobInfo?> CancelAsync(string id, CancellationToken ct = default);
Task<IReadOnlyList<JobBackground>> PendingBackgroundAsync(CancellationToken ct = default);
Task CompleteBackgroundAsync(MessageId notificationId, CancellationToken ct = default);
```

- Start joins an existing running ID without running another delegate. A terminal ID can be reused; entry identity prevents an older completion from overwriting the replacement.
- Block counts each waiting Session dependency and races actual completion against committed background promotion. Interrupted waiters decrement only their own entry/count. BackgroundAll selects running, unpromoted jobs blocked by that Session, optionally filtered by type.
- Foreground job state is process-local. A recoverable background handoff creates a stable MessageId and persists the source marker **before** setting backgrounded/resolving blockers/returning acceptance.
- Completed/error state is persisted before Done resolves. Explicit cancellation persists cancelled before cancelling/joining the delegate. Shutdown interruption does not replace a preceding durable running marker with a fictitious terminal outcome.
- Persistence errors fail waiters and retain the previous marker; no completed/backgrounded state is fabricated. BackgroundAll uses separate marker writes as source does, not an invented atomic multi-marker transaction; partial writes can remain recoverable if a later write fails.
- No in-memory persistence implementation or standalone fallback database is supplied.

## Core/Event owner: required concrete persistence

`IJobBackgroundStore`, `JobBackground`, and `JobJsonContext` are in `JobContracts.cs`. Implement the following over the **existing channel database and coordinated KV boundary**:

```csharp
Task<IReadOnlyList<JobBackground>> ListAsync(CancellationToken ct);
Task SaveAsync(JobBackground value, CancellationToken ct);
Task RemoveAsync(MessageId notificationId, CancellationToken ct);
```

The source key is `job.background/{notificationID}`. These are KV recovery/notification records, **not new durable events**. Use `JobJsonContext.Default.JobBackground` and `JobBackground.Validate()`; no new schema/table is required.

The source record shape is:

```text
id: string
notificationID: MessageId
recovery:
  { kind: "shell", sessionID: SessionId, shellID: ShellId, command: string }
  | { kind: "subagent", parentSessionID: SessionId, childSessionID: SessionId,
      agent: string, description: string }
status: "running" | "completed" | "error" | "cancelled"
output?: string
error?: string
```

`JobRecovery.OwnerSessionId` is **not serialized**. It is the shell Session or subagent child, for the EventStore/Session mutation coordination the existing specialized subagent store already uses. Save/remove must preserve those barriers. Remove may need to read the marker to select its owner before coordinated deletion; do not delete a different key/owner on an ID mismatch.

List should scan the existing prefix, validate each descriptor plus key/notification identity, and skip malformed/unknown rows without deleting them. Shell IDs are domain-validated, status decoding rejects numeric/unknown enum values, required fields and explicit-null optional output/error are checked. Persisted output is private command data; it is not an event or public credential payload.

JobRuntime uses non-cancellable persistence after its operation gate has admitted a transition, so a committed marker cannot be reported as a cancelled handoff before memory/blockers reflect it. The concrete store must finish its committed transaction before returning success. There is no SQL implementation in Core/Jobs or Core/Shell; do not enable the adapter with a no-op/memory store while this boundary is pending.

The existing `SubagentBackgroundPersistence` is internal and subagent-only. It was not reused for shell data or edited. The existing SessionSubagents runner was not duplicated or migrated in this pass. Its owner can adopt this generic runtime/delegate boundary later, sharing one host service rather than creating another child runner. Shell recovery filters shell descriptors and leaves subagent markers to their owner.

## Concrete Shell adapter

`Core/Shell/Jobs/ShellToolJobs.cs` implements **all** IShellToolJobs operations over JobRuntime and the actual SessionStore/SessionExecutionEngine APIs. It maps ShellJobStart to source job identity/type/title/metadata/recovery. It also exposes `BackgroundAllAsync(sessionId)` for source shell-only foreground promotion.

Live notification admission checks terminal job, stable notification ID, Session ownership, and shell identity. It calls the existing public ReconcileInboxAsync and AdmitInboxAsync with the supplied stable MessageId. It then reloads current Session state and schedules WakeAsync only after commit and only when no staged revert suppresses it. CompleteBackgroundAsync runs afterward from the ShellTool notification observer. An admission/wake failure leaves the marker intact for retry/recovery.

This differs intentionally from the already implemented **user** SessionShellLifecycle, which remains admit-only/no automatic wake. That adapter was not edited or reused as a fake background completion service.

`SupportsBackground` becomes true only for a live adapter constructed with a real JobRuntime (whose constructor requires IJobBackgroundStore), existing Session services, and a host lifetime. Store failures still reject the actual handoff. Main owner must not register/pass this adapter until the concrete coordinated marker store is installed.

## Source shell restart recovery

The adapter exposes:

```csharp
Task<IReadOnlyList<MessageId>> RecoverAfterConfirmedRestartAsync(
    IReadOnlySet<SessionId> suspended, CancellationToken ct = default);
```

Call explicitly only after confirming predecessor death and owning the managed registration lock, matching the existing Session restart API's authority contract. Construction and normal tool registration never recover. Unregistered hosts sharing a database must not sweep.

Call shell recovery before subagent recovery or top-level Session resume. Supply the restart owner's full suspended set; those Sessions receive durable admission without an early wake so resume accounting happens first. Current-process running Job entries are authoritative and skipped.

For a validated orphaned shell marker:

- `running` becomes a cancellation notification with exact source text **“Command cancelled because the server restarted”**.
- `completed` uses stored output, or source fallback “Command completed”.
- `error` uses stored error, or “Command failed”.
- `cancelled` uses “Command cancelled”.

No PID, capture file, or fabricated ShellInfo/exit code is used to infer process success. A missing receiving Session is ignored and its marker removed as source specifies. All other failures leave the marker. Stable synthetic identity makes a crash after admission but before deletion replay-safe: first admission wins.

## Source map and verification

- `packages/core/src/job.ts`: state, start/join, counted blocking, promotion, KV marker ordering, cancellation, wait, and completion acknowledgment.
- `packages/core/src/tool/plugin/shell.ts`: shell adapter start/block/background/progress/result/notification semantics.
- `packages/core/src/session/execution/restart.ts` lines 100–134 and 196–235: shell recovery notices, live-owner skip, suspended wake suppression, shell-before-child ordering.
- Existing .NET SessionStore public ReconcileInboxAsync/AdmitInboxAsync and SessionExecutionEngine.WakeAsync are used without direct Session SQL or new events.

Only pinned local .NET 11 Core/Server builds with isolated `C:\tmp\opencode\mcp-finish-pass` artifacts are allowed/run. No tests, job/model/shell execution, persistence/admission, restart recovery, filesystem cleanup, API, network, or process/native verification is performed. No shared project files or commits were changed.

Final full Core and Server builds both succeeded with **0 warnings and 0 errors**. The coordinated IJobBackgroundStore implementation remains the Core/Event owner's required dependency; these builds do not claim persistence/recovery has been exercised or that an unconfigured adapter is ready for production wiring.
