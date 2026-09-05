# Interrupt continuation

`SessionExecutionEngine.InterruptAsync(sessionId, options, hostLifetime, ct)` adds
typed `SessionInterruptOptions.Continue`. The existing overload remains an
interrupt-only operation. Unknown Sessions raise `SessionMutationNotFoundException`;
idle or locally unowned Sessions return false without scheduling work.

After an accepted user interruption, continuation checks the next eligible inbox
item with input scope. Only a steer or a compaction/move control schedules a
non-forced, host-owned steer-scoped wake. A queued prompt at the head blocks a
control behind it. The continuation does not use `ResumeHostedAsync`.

The runner still inspects controls at entry/idle boundaries with input scope,
but a steer-scoped drain never promotes queued prompts. Controls can complete
without an unconditional model call. Eligibility is checked again when the drain
runs, so input already delivered by the interrupted execution does not force a call.

Coordinator wake scope is process-local scheduling state, not a durable delivery
mode. Coalescing preserves the widest requested scope: ordinary prompt wakes use
input; interrupt continuation uses steer. Wakes during cancellation cleanup wait
for settlement and retain their merged scope. Repeated interruption of an already
stopping execution does not erase these new admissions.

The existing cancellation chain, terminal settlement, user-versus-shutdown claim
handling, physical-attempt accounting, and logical-step budgeting remain in place.
No new execution loop or cluster ownership mechanism is introduced.

Server passes its execution shutdown token as the continuation owner. HTTP request
cancellation does not become the scheduled model execution lifetime. The existing
Client `continueExecution` flag and Protocol contract require no changes.

Verification is limited to source inspection and pinned .NET builds. No runtime,
coordinator, database, provider, or interruption operation was exercised.
