# Typed context, revert, and snapshot boundaries

## Server API handoff

`SessionQueries` now exposes:

- `MessageAsync(sessionId, messageId, ct)`: one canonical Schema message, or null
  for a missing/wrong-session ID. Row identity overrides JSON payload identity.
- `ContextAsync(sessionId, ct)`: verify the Session, then return typed messages
  starting at the latest completed compaction (inclusive), ordered by sequence.
  This is `SessionHistory.load`, not paginated transcript history, a settled-only
  preview, or assembled provider messages. Staging a revert does not delete or
  filter these rows. Invalid projections raise `SessionMessageReadException`.

Sources: `session/session.ts:Session.message`, `session.ts:Session.context`,
`session/history.ts:latestCompaction/messageEntries/load`. Missing Session context
uses existing `SessionMutationNotFoundException`; the Server maps it to 404.
SDK `MessageAsync`/`ContextAsync` use the same service. Injected SDK hosts pass
`queries:`; the owned constructor creates it from the existing database.

`SessionRevertOperations(database, sessions, execution, snapshots)` exposes:

- `StageAsync(sessionId, messageId, files: true, ct)` returns `SessionRevert`.
- `ClearAsync(sessionId, hostLifetime, ct)` clears and schedules the source advisory
  wake. Even a no-op clear wakes; a cancellable host execution lifetime is required.
- `CommitAsync(sessionId, ct)` commits the current staged boundary, or does nothing
  when none exists. It does not wake or restore files.

Active Sessions raise `SessionBusyException` without interruption. Operations share
the existing coordinator admission/removal accounting and inbox registration gate;
there is no second Session coordinator. Missing Sessions use the existing not-found
exception. A missing stage boundary raises `SessionRevertMessageNotFoundException`.
Snapshot errors expose their `Operation`. No Server endpoint changes are included.

## Source revert semantics

`session/revert.ts` plans from assistants strictly after the requested message,
in ascending sequence order. The first snapshot.start associated with each changed
path wins. No file names or prior contents are inferred from tool inputs, timestamps,
current Git HEAD, or textual patches.

Stage keeps the original pre-revert tree across repeated stages, first restores
previously reverted paths from that tree, then overlays the new plan unless
`files: false`. The displayed files are actual Git diffs against the original tree
for the requested plan's paths. Clear restores only the paths listed in the staged
revert. Files outside these maps are untouched. Historical assistants without
snapshot facts have no reconstructable file undo; conversation staging still works.

`SessionRevertPersistence` publishes the source version-1 facts:

- `session.revert.staged`: `{ sessionID, revert }`
- `session.revert.cleared`: `{ sessionID }`
- `session.revert.committed`: `{ sessionID, to }`

Stage/clear update the Session's revert field and recency in the event transaction.
Commit resolves the boundary sequence and deletes messages at or after it and inbox
rows enqueued at or after it. It clears revert and deletes `instruction_state` to
reset the epoch. Blobs, instruction entries/tombstones, event history and accumulated
usage are not purged or rewritten. Unsequenced legacy projections are rejected.

For prompt admission, construct `SessionPromptPreparation` with:

```csharp
commitRevert: (session, ct) => SessionRevertOperations.CommitPreparedAsync(database, session, ct)
```

That callback runs only after successful attachment/plugin preparation and before
new durable admission. Reconciled retries do not commit a revert. Without the
callback, the existing explicit staged-revert guard remains. The SDK's owned
constructor wires this callback; injected hosts must use their same database.

## Snapshot capture and Git storage

Register one `SessionSnapshotLocations` per host and pass it as `snapshots:` to the
engine and to SessionRevertOperations. Location services share per-repository index
locks. `SessionAttempt` captures before its one physical stream, stores snapshot.start
in Step.Started, then captures after tool settlement for Step.Ended/Failed and records
changed relative paths. Pre-output retries do not fabricate a terminal Step event.
No additional Tool callbacks or permission graph are needed.

Sources: `snapshot.ts`, `git.ts:repo.create/index.refresh/tree.capture/tree.files/
tree.diff/tree.restore`, `runner/step.ts`. Snapshots use an isolated Git directory
under `data/snapshot/<project>/<SHA1(worktree)>`, seeded from the source index and
shared object storage. Capturing never updates the user's Git index or HEAD.
Only the Location subtree is refreshed. Source ignore rules apply, and untracked
files above 2 MiB are excluded. IDs are real content-addressed trees.

Diffs use `--no-renames`, numstat and actual unified patches; binary patches are empty
with zero line counts. Restore checks tree membership, checks out listed paths from
their associated tree, and removes a mapped path when absent from that tree. The
complete map is checked for lexical project escape/root removal first. Restore is
not an atomic filesystem transaction; a Git/filesystem failure may leave partial
changes, as in source. No guessed rollback is attempted.

The pinned .NET process API owns/reaps only its Git child. Arguments are separate
ArgumentList entries; NUL path lists use file-backed stdin, not shell concatenation
or a detached writer. Capture is best effort and returns no snapshot when unavailable;
explicit diff/restore errors are not converted into fake success. `snapshots: false`
in the normalized configuration disables capture/restore. Plugin configuration still
requires the real plugin runtime and fails explicitly.

## Limits and verification

- Host composition is required for Server capture/revert. An omitted engine snapshot
  service does not synthesize snapshots; old history cannot acquire undo retroactively.
- Generic project/VCS activity eviction, plugin snapshot transforms, public Schema
  revert event inventory entries and HTTP adapters remain separately owned.
- Cross-Session concurrent workspace edits and process crashes do not have filesystem
  transaction/exactly-once guarantees. Only source per-repository Git index locking
  and Session ownership are supplied.
- No Git commands, snapshot/revert operations, tests, application launches, or runtime
  database/filesystem operations were executed during implementation. Verification
  is isolated pinned .NET compilation and static checks only.
