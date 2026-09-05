# Retry and Restart Recovery

## Pre-Output Retry

`SessionRetry` follows `session/runner/retry.ts`: four recurrences, a 2-second
exponential base (2, 4, 8, 16 seconds), 0.8-1.2 jitter, and ceiling to milliseconds.
Rate-limit/internal failures honor retry-after-ms, retry-after seconds, or an HTTP
date as a minimum, capped at 15 minutes. The captured HTTP headers are consulted
case-insensitively; exact x-should-retry true/false overrides category policy.

Eligible categories are rate limit, provider internal, unknown provider, transport
with unspecified or NotSent delivery, and incomplete-stream invalid output. Auth, quota,
content policy, invalid request, unsupported input, and all non-LLM exceptions do
not retry by default. Context overflow never enters generic retries, even with a
header override, because its source path requires compaction recovery. Payload-too-
large has no shrinking fallback; ordinary invalid-request policy applies.

The native Transport reason now exposes operation/delivery/recovery fields. Absent
delivery uses the source's undefined-delivery rule, not fabricated not-sent evidence. This
is not an exactly-once guarantee for an unknown remote outcome. No body sniffing
or retry-all-Exception path is used. Provider-error events are distinct from a
thrown LLM failure and do not enter this retry path.

`SessionAttempt` marks output started at text/reasoning/tool input start, malformed
tool input, or confirmed tool call. Once marked, generic retry is prohibited even
if the fragment is empty. Tool side effects and permission/control failures cannot
be retried through this family. A retry returns before step.failed/step.ended and
usage projection; it retains the same assistant ID and original creation/sequence.

`session.retry.scheduled.1` stores only sessionID, assistantMessageID, next attempt,
absolute at timestamp, and canonical error. The projector updates assistant.retry.
The wait subtracts publication time and observes execution cancellation. A later
step.started for the same assistant clears retry and streamed/completed state.

The runner reloads Session, Agent, model, captured tools, instruction observations,
stored baseline and projected history before another attempt, but does not deliver
new inbox input or increment the logical step allowance. Each attempt has exactly
one explicit StreamAsync call. Empty retry placeholders add no model-visible content.
Only a completed logical step increments the agent step counter.

## Incomplete-Stream Continuation

`SessionAttempt` also follows `runner/step.ts`'s Continue outcome for explicitly
classified `InvalidProviderOutput(IncompleteStream: true)` failures after output.
The same path handles Transport.Operation == Read, including HttpRequestException
and IOException read failures classified by the shared provider boundary. Inner
exception types and error text are not used to guess the operation. An unclassified
Transport error is not enough. As in source, this post-output continuation proposal
is separate from generic pre-output delivery eligibility; x-should-retry still wins.
It joins confirmed local calls rather than cancelling them merely because the
provider body failed. Any tool/control failure or execution cancellation prevents
continuation. The failed physical attempt and tool results settle durably first.

`SessionExecutionEngine` follows `runner/llm.ts`: wait on the same bounded retry
policy, publish the exact direct `session.synthetic` continuation instruction,
allocate a new assistant ID, and reload projected context. It does not promote
pending inbox work or increment/reset the logical step allowance or retry budget.
It does not replay the old tool calls. Each physical attempt still has one stream.

The existing generic `SessionStore.PublishCompactionAsync<T>` transaction seam
publishes the synthetic through `SessionSyntheticProjector.Definition`; despite
its historical method name, it does not append a compaction event. A future storage
API cleanup can rename that generic seam without changing the durable contract.

## Bounded Startup Recovery

`RecoverSuspendedAsync(lifetime, maxAttempts: 10)` is an explicit startup API. The
managed host must first establish that the previous process is dead and hold its
registration ownership. Embedders sharing a database must not run competing sweeps.
No constructor or ordinary request silently invokes recovery.

The scan selects top-level sessions with time_suspended. Already-owned sessions
are skipped, including an atomic recheck inside the coordinator registration gate.
Supported claims durably increment resume_attempts before execution and publish
the source synthetic:

> The server restarted while you were working. Continue from where you left off without repeating completed work.

The counter and synthetic commit in one transaction. A surviving claim is never
cleared just to start recovery. Exceeding the configured bound publishes the source
execution.failed exhaustion error and releases/resets the claim atomically. Normal
terminal success/failure/user interruption releases it; shutdown preserves it.

Recovery schedules owned work under the supplied host cancellation lifetime and
returns a report of Scheduled, Exhausted, Skipped and Blocked IDs. Scheduled means
scheduled, not a claim of successful model completion. AwaitIdleAsync remains the
settlement boundary. Runtime model/config failures use normal terminal handling.

The source entry cleanup now settles visible streaming/running tools with durable
tool.failed aborted facts before a fresh drain. It never re-executes an old call.
Model history retains partial assistant text/reasoning and settled results; a new
step closes the newest old incomplete assistant as the source projector specifies.
Context reads honor the latest completed compaction boundary. Normal execution now
supports checkpoint execution/lowering; restart-specific compaction guards below
remain in place. See `COMPACTION.md`.

The plain engine entrypoint still guards workspace claims, claimed children,
unhandled background jobs, uncomposed/workspace moves, running shell history and
unsequenced projections. Pending compaction and supported local movement now use
normal ordered control delivery; unfinished compaction records are not fabricated
as complete or re-enqueued. See `CONTROL-RECOVERY.md`. The combined
`SessionSubagents.RecoverSuspendedAsync` now handles builtin subagent claims and
stable-ID notices before calling that engine sweep with an explicit handled scope.
It releases abandoned foreground-child claims without inventing terminal events;
see `Subagents/RECOVERY.md`. Shell/plugin job recovery remains unported.
Recovery is at least once: a process
may die after a side effect and before its success event; the restart instruction
does not prove an external action was not already performed.

Completed compaction checkpoints and location-switched messages no longer block
recovery. They already have native context lowering; recovery uses the saved
current placement and stored instruction epoch, not a replay of the old controls.
Pending local controls now use their real domains. An interrupted compaction's old
stream is not resumed; its uncompleted row is excluded from the checkpoint baseline.

## Remaining Families

Transport errors without concrete read evidence, retry-full/rotate-and-retry-full
transport rejection recovery and plugin retry hooks
remain unsupported. They are not replaced by repeating a failed SDK tool loop.
The presence of transport recovery enums does not implement WebSocket/full-context
recovery. Explicit incomplete-output and Operation.Read continuation are supported
as described above; no recovery directives are invented for HTTP failures.
Pre-output overflow supports one successful compaction rebuild per logical step;
it never enters the generic retry schedule.
Provider resolution receives the actual Session ID on every physical attempt,
including retries and recovery, for session-bound authentication/cache behavior.

Verification is isolated compilation and static source checks only. No tests,
runtime recovery, model/tool/provider calls, database operations, or processes were
executed to validate behavior.
