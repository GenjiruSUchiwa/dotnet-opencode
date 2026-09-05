# Session instruction entries

`SessionInstructionEntries(IDatabase)` provides the native domain API:

- `ListAsync(sessionId, ct)` returns Schema `InstructionEntryInfo` values in key
  order, excluding tombstones.
- `PutAsync(sessionId, key, value, ct)` validates the source key pattern and the
  UTF-8 byte length of compact JavaScript-style JSON serialization. The inclusive
  limit is 8,192 bytes. Raw JSON whitespace, ASCII escaping, and pretty formatting
  do not increase this length. Undefined/non-finite values are rejected as JSON
  errors. Object member order is retained except for JavaScript integer-index
  ordering; duplicate parsed members use the last value at the first position.
- `RemoveAsync(sessionId, key, ct)` marks an existing live entry removed and clears
  its value. Missing and already-removed keys are no-ops; no new tombstone is made.

Put uses serialized JSON equality, not deep sorted-hash equality. Repeating the
same live value does not update its timestamps. A changed value or resurrection
updates only `time_updated`; `time_created` remains stable. JSON null is a live
value stored as SQL NULL, distinct from the removed flag. Fork initialization uses
the same compact serialization so an unchanged inherited value remains a no-op.

## Persistence and instruction epochs

Source `packages/core/src/session/instruction-entry.ts` directly persists producer
rows. It defines no entry mutation event. The native implementation uses the
existing Session-serialized `EventStore.TransactAsync` transaction boundary for
these row mutations. It does not append fabricated put/remove events, reserve a
sequence, modify session recency, wake execution, or directly modify instruction
state and message projections.

The next existing instruction preparation boundary reads entries, including
tombstones, as `api/<key>` sources. `InstructionPersistence.PrepareAsync` retains
the source `session.instructions.updated.2` event: only changed key/hash facts and
optional frozen chronological text. Initial values establish the baseline without
a visible System message; later changes leave that baseline intact and enter
history through the existing event projector. Removing a key that was never
observed produces no instruction delta. Removing and replacing it between boundary
observations exposes only the latest observed value, as in the source.

No alternate instruction registry or immediate model execution is introduced.
Fork snapshots keep tombstones. Existing compaction/revert/epoch mechanics continue
to own their own boundaries.

## Server and SDK handoff

Register one `SessionInstructionEntries` using the host's existing `IDatabase`.
No endpoint changes are included in this pass. The source handlers are
`packages/server/src/handlers/session.ts`:

| Operation | Domain call | Source response |
| --- | --- | --- |
| `session.instructions.entry.list` | `ListAsync` | `{ data: entries }` |
| `session.instructions.entry.put` | `PutAsync` | 204 |
| `session.instructions.entry.remove` | `RemoveAsync` | 204 |

Map `InstructionEntryValueTooLargeException` to HTTP 413 with `actualBytes`,
`maxBytes`, and its message. The source wire error tag is
`InstructionEntryValueTooLargeError`. Invalid keys raise `ArgumentException` with
parameter `key`; invalid JSON raises `JsonException`. Source list/remove do not
invent a missing-session error: list returns empty, remove is a no-op. Put requires
an existing session through the foreign-key constraint. Do not map unexpected
storage failures to a successful response.

The SDK exposes `client.Instructions.ListAsync/PutAsync/RemoveAsync`. Its owned
constructor uses the same database as Sessions. An injected host passes
`instructions:` as the optional final constructor argument. Omission preserves
existing host callers, but accessing Instructions then throws an explicit host
composition error instead of silently using another database.

## Prompt admission mismatch

The added model-resolution preflight from the preceding pass has been removed.
Source `Session.prompt` in `session/session.ts` reconciles a reused ID, invokes
`SessionPrompt.prepare`, commits a staged revert after successful preparation,
admits input, and then requests an advisory wake unless `resume: false`.
`SessionPrompt.prepare` owns plugin prompt hooks and attachment/skill preparation;
it does not resolve the execution model or initialize the instruction epoch.

The subsequent prompt-preparation pass removed Core's inherited instruction-readiness
gate and added shared `AdmitPromptAsync`. The Server owner must call that boundary;
see `PROMPT-PREPARATION.md`. Execution resolves the model and validates tool-body
overrides before inbox promotion. Unsupported plugins still fail explicitly. The
real staged-revert commit callback is now available; see `REVERT-SNAPSHOTS.md`.

Verification is isolated compilation only. No tests or runtime/database operations.
