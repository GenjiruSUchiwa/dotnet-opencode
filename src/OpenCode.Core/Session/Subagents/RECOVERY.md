# Subagent startup recovery

Call `SessionSubagents.RecoverSuspendedAsync(maxAttempts: 10)` once during managed
startup, **instead of invoking only the engine's top-level sweep**. It uses the
service's cancellable host lifetime and returns `SessionRecoveryReport` including
child and parent IDs. The caller must own managed-server registration, know the
previous process is dead, and run before admitting ordinary traffic. No constructor
or tool invocation calls it automatically. The SDK exposes it through its injected
`Subagents` service.

Source: `core/session/execution/restart.ts`, `core/session/store.ts`, `core/job.ts`,
and `core/session/subagent-completion.ts`.

## Recovery sequence

1. Read the source-compatible background notification markers. This specialization
   handles builtin subagent jobs whose job ID is their child Session ID. Shell,
   unknown/plugin job shapes and malformed records are not authorized by the scope.
2. Snapshot suspended top-level Sessions plus running recoverable child IDs. Early
   completion notices are admitted without waking these Sessions, so attempt
   accounting and exhaustion cannot be bypassed.
3. Release abandoned foreground-child claims and reset their resume counters without
   changing recency or fabricating terminal events. Keep recoverable background child
   claims and active local owners. Unsupported shell/unknown background domains retain
   their recovery guard.
4. Verify each record's child, parent, and exact parent/child relationship. Delete
   stale notification markers for missing/mismatched families without starting work.
   Replay completed/error/cancelled notices using their stored notification IDs; this
   does not resolve a model or resume a child.
5. Skip current local owners/jobs. For an idle running recoverable child, acquire the
   existing coordinator's recovery-only ownership, durably count its resume, and
   publish the exact restart synthetic before model execution. Count applies even when
   a running job marker exists but the old process died before creating an execution
   claim. Exceeding the bound publishes the source execution failure and an error
   notification, not a new model attempt.
6. Hand the **same recovered drain Task** to the existing subagent job service.
   Do not call Resume a second time after registration: a fast child may already have
   completed, and another explicit Resume could force a duplicate step. Read the final
   successful assistant from source context selection, not a raw transcript substitute.
7. Reattach the original notification ID and observer. Completion persists its actual
   result, admits the source synthetic idempotently, and removes the marker only after
   successful delivery. Early parent wakes remain suppressed until root accounting is
   finished; later completions wake normally.
8. Invoke the existing engine's root recovery sweep with a scope containing only the
   handled notification IDs and owned/active recoverable child IDs. Existing guards
   remain for unsupported records, placements and controls. A finite dependency pass
   retries blocked ancestor preparations only after another child/notice was handled;
   it does not sleep or poll running model/tool work. Blocked preflight does not consume
   a resume attempt.

## Ownership and durability

There is no new global Job store or second Session coordinator. Recovery attaches
to the same host-owned SessionSubagents registry, engine and durable inbox. Claims
are recovery markers, not clustered leases. Admission still uses first-wins identity
for pending or already-delivered synthetic notices. A repeated startup notice cannot
rewrite a previously admitted payload or insert a duplicate visible message.

Stale streaming/running tools are settled with aborted facts before a recovered
model request; they are not executed from their stored inputs. When a stored subagent
tool includes child metadata, its interruption message preserves the source child-ID
suffix. The runner still has one explicit stream per physical attempt and reloads
durable history for continuation. Recovery is at least once: a new model decision may
repeat an external action after a crash, so no exactly-once side-effect guarantee is
claimed.

Shutdown preserves surviving claims and markers. Cancellation during registration
observes the actual recovered owner before disposing its token source. Once attached,
the existing job/notification tasks remain host-owned. Persistence/delivery failures
are not replaced with empty successful completions.

## Remaining limits

- Shell-job recovery and running shell messages remain guarded. The shell owner must
  supply real recovery/notification handling; no shell process is replayed or assumed
  to have an exit code here.
- Explicit workspaces, uncomposed local moves, unknown compaction states and
  unsequenced legacy projections retain guards. Pending compaction and supported
  local moves now run through their existing ordered domains; see `../CONTROL-RECOVERY.md`.
- Arbitrary plugin Job IDs/shapes, generic Job HTTP APIs and background-all UI controls
  remain outside this specialized service.
- The plain engine `RecoverSuspendedAsync` retains its conservative defaults when no
  subagent recovery scope is supplied. Hosts must wire the combined startup entrypoint.
- No tests or runtime recovery, database access, providers, tools, processes, or network
  calls were used as verification. Validation is isolated pinned .NET compilation.
