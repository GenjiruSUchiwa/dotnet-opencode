# Coordinated Job background store

`JobBackgroundStore(IDatabase)` implements Core/Jobs `IJobBackgroundStore`:

```csharp
services.AddSingleton<IJobBackgroundStore, JobBackgroundStore>();
```

Use the existing channel database. The host can then supply this store to its one
`JobRuntime`, and that runtime to the already implemented ShellToolJobs adapter.
No Job runtime, process registry, Shell runtime, model loop, or memory fallback is
created here.

## Storage and validation

- Records use the source `job.background/{notificationID}` KV prefix and existing
  `kv` table. They are recovery/notification records, not new durable events.
- List scans that prefix in key order and uses
  `JobJsonContext.Default.JobBackground` plus `JobBackground.Validate()`.
  Required fields, typed statuses, recovery discriminators/IDs and explicit-null
  optional output/error validation remain owned by those contracts.
- The actual key must equal the decoded notification identity. Malformed, unknown
  or mismatched records are skipped without deletion or rewriting. Private command
  output is not logged or copied into an event envelope.
- Save serializes before entering mutation and uses the recovery OwnerSessionId
  for Session admission/removal accounting and EventStore transaction coordination.
  Updates retain time_created and change only the marker value/time_updated.
- A retained notification identity cannot be silently reassigned to a different
  job/recovery descriptor. Such misuse or malformed existing data raises
  `JobBackgroundConflictException`; ordinary status/output/error updates retain the
  same identity and proceed normally.
- Remove is idempotent for an absent key. Otherwise it first validates the marker
  to select the owner, acquires that owner's coordinated boundary, rereads it inside
  the transaction and checks identity before deleting. A key/owner/descriptor change
  is a failure, not deletion under the wrong Session's reservation.
- No Session existence check removes stale markers from the list: recovery owners
  must still see valid records whose Session was deleted and complete their source
  cleanup rules. No Session row or event sequence is fabricated for KV mutations.

JobRuntime supplies non-cancellable persistence once its transition gate accepts a
handoff. EventStore completes projection-free KV transaction commit before returning
success. Failures propagate to that runtime's existing waiter/state handling.

## Existing subagent records

Subagent jobs now use the shared JobRuntime, which delegates storage to this generic
implementation. The specialized marker writer is removed; only recovery-local
projections remain. Old records
already have the same wire shape; JobJsonContext accepts out-of-order discriminator
properties, including the former trailing `kind` property. No migration rewrite or
new table is needed.

The subagent recovery reader still selects only builtin subagent jobs
whose job ID is their child Session ID. Generic shell/other valid records remain in
the shared store for their respective owners. Completion checks parent/child identity
and its stable notification before acknowledgment through the same JobRuntime.
No second global job registry is constructed.

Source: `core/job.ts`, the native `Jobs/JobContracts.cs` and `Jobs/HANDOFF.md`.
Host JobRuntime/ShellToolJobs wiring and remaining restart orchestration are separate
owner tasks. Only isolated pinned .NET builds/static checks were performed; no tests,
KV operations, jobs, models, tools, processes or network calls were executed.
