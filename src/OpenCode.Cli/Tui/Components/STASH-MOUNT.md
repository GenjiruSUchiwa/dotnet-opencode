# Mounted structured prompt stash

The root owns one supplied `PromptStashStore`, loads it once, and enables the source
`prompt.stash`, `prompt.stash.pop`, and `prompt.stash.list` commands only when loaded.
There are no new slash aliases or default bindings. `DialogStash` uses the existing
theme, configured `stash.delete` resolver/shortcut, and real structured restore callback.

## Admission boundary

`StashAccess` checks the retained retry object before calling the ordinary prompt
capture helper, which can deliberately clear an ID after edits. Retry-bound drafts,
in-flight origins/commands, unresolved shell/command outcomes, unavailable Session
admission state, unconfirmed admissions, and matching pending input are rejected.
Existing IDs are passed to the store when available; an unknown ID does not make
an uncertain input editable. Stash code never removes retry identities, submits,
reconciles, retries admission, or manufactures a replacement request ID.

## Capture and restore

Push captures the existing complete editor snapshot: typed file/agent/skill arrays,
URI/data payloads and mentions, metadata, virtual-mark identities/bindings/next ID,
and shell/normal mode. Only a returned memory-accepted Entry permits synchronous
composer reset. Persistence is awaited separately and cannot clear later typing.

The current root stores text pastes literally and PNG/file pastes as typed file
attachments. It creates no collapsed pasted-text descriptors. Capture therefore
passes an explicit empty descriptor list; restoration rejects any entry containing
such descriptors rather than sending a displayed placeholder in place of its data.
Unsupported source fields, admission envelopes, modes, metadata versions, and
inconsistent virtual marks leave the entry intact.

Restore prepares all typed data and mark projection before changing the editor.
Admission checks, undo capture, full restore, mode restoration, and cursor-to-end
movement are synchronous on the renderer dispatcher. Pop consumes the expected
entry only after successful restore; the supplied dialog follows the same ordering.
No submitted history item is added. The existing undo/redo snapshot also restores
mode when reversing a stash replacement.

## Memory and disk outcomes

Store notifications are dispatched to the root even after the dialog closes.
Action/capture errors and persistence errors have separate feedback. Dirty state
means changes are in memory, not saved; older writes do not certify a newer revision.
An explicit pointer action retries only the stash save. The store remains alive
after read/write failure, including memory-only recovery when an unread file cannot
be safely overwritten. Shutdown stops mutations, drains root callbacks, and awaits
the store's queued writes.

Verification is build-only: pinned repo-local .NET 11, full CLI dependency graph,
isolated artifacts, and `OpenApiGenerateDocuments=false`. No tests, stash/preferences
I/O, application/native execution, API/DB/clipboard/process operations, or screenshots
are performed for verification. Runtime and visual parity remain unverified.

The initial full CLI build including the stash and focused model-action mounts
passed with 0 warnings/errors. The follow-up build after the mode-undo and separate
persistence-feedback refinements is blocked by concurrent
`OpenTui.Blazor/Code/TreeSitterWasm.cs:78` (`CS1503`, ulong to uint). The isolated
offline restore was refreshed for Wasmtime; no TreeSitter or project source was edited.
