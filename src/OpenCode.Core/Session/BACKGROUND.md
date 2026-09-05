# Session.background and shared job ownership

The Core API is:

```csharp
new SessionBackgroundService(sessions, sharedJobs, execution, hostLifetime)
    .BackgroundAsync(sessionId, ct);
```

The owned SDK registers it and exposes `client.BackgroundAsync(sessionId, ct)`.
The Server owner can replace its existing stub for the actual source route
`POST /api/session/{sessionID}/background`. No generic Job HTTP routes were invented.

Source `core/session.ts:Session.background` behavior is preserved:

1. Load the known Session, raising the existing Session not-found error otherwise.
2. Call the shared `JobRuntime.BackgroundAllAsync(sessionId)` without a type filter.
   Only running, non-backgrounded jobs actually blocked by this Session are selected.
3. If the returned list is empty, do nothing: no synthetic input, wake, claim or event.
4. Otherwise admit the exact source “User requested that active blocking work be
   moved to the background” synthetic, including type/title-or-ID lines and the
   warning that this work is unfinished. Wake only after durable admission and only
   when no staged revert suppresses it.

## One job registry

`SessionSubagents` now requires the SAME JobRuntime as shell jobs, supplied as its
final constructor argument. Omitting it raises an explicit composition error before
subagent work; it does not create a fallback registry.

The old `_runs` state machine and specialized marker writer are removed. Live
subagents use JobRuntime.StartAsync, BlockAsync, BackgroundAsync, CancelAsync,
PendingBackgroundAsync and CompleteBackgroundAsync. Actual child execution still
uses the existing Session engine/coordinator, not another model loop. A foreground
subagent therefore registers the real counted dependency observed by BackgroundAll.

JobRuntime remains responsible for persisting background markers before exposing
promotion, resolving blockers, or returning acceptance. Failure leaves the existing
state/marker semantics intact. Source permits separate marker writes during bulk
promotion; this layer does not pretend those writes form a new atomic transaction.

## Notifications, cancellation and restart

Subagent completion observers are still per generation, not another job registry.
They use the committed notification ID, admit the actual terminal result before
acknowledging/removing the marker, and keep failures recoverable. If an observer
attaches after an ID has been replaced by a later generation, it uses the prior
terminal marker rather than publishing later output under an older notification ID.
An absent or still-running prior outcome is never replaced with fake completion.

Foreground cancellation still interrupts/cancels the child through existing APIs.
Job cancellation is distinguished from host shutdown for Session claims, including
recovered child drains. Backgrounded results are reported as running, never complete.
Question/permission dismissal remains control flow, not a fabricated successful output.

Recovery uses the shared job registry too. It passes the SAME already-registered
recovered drain task into JobRuntime's execution delegate, retaining attempt counts,
parent/child checks, stable notification identities and early parent-wake suppression.
It does not call Resume a second time or create another marker-persistence path.

The host owns JobRuntime disposal. SessionSubagents owns only its completion observers
and domain cancellation lifetime; do not create one JobRuntime per Location or tool.
The SDK composition is updated. Main Server composition must pass its existing
singleton JobRuntime to SessionSubagents and register SessionBackgroundService.

Plugin jobs are covered only when they are real jobs in that shared runtime. This
does not implement the configured plugin runtime or claim that unsupported plugin
producers have executed.

Verification is isolated pinned .NET SDK/Core compilation with
`OpenApiGenerateDocuments=false` and static checks only. No tests, jobs, background
operations, recovery, database access, model/tool execution or process/network
operations were run.
