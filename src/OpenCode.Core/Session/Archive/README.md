# Projected session archives

Source: packages/schema/src/session-transfer.ts, protocol/src/groups/session.ts,
server/src/handlers/session.ts and core/src/session/transfer.ts in the current
TypeScript worktree. This is SessionTransfer.Data, not a database/event archive.

## Implemented

- SessionArchiveService.ExportAsync reads the stored SessionInfo and complete
  ascending projected messages through existing SessionStore/SessionQueries APIs.
  It does not execute direct SQL or read only the current compaction context.
- It omits assistants without time.completed and shell/compaction messages whose
  status is running, exactly as source isSettled does.
- SessionArchiveSanitizer implements source field-specific sanitization on a
  detached serialized copy. All other current SessionInfo/SessionMessage wire
  fields remain present according to their canonical optional-field rules.
- The existing Schema SessionTransferData contract is reused, not duplicated.
  Archive-specific Protocol supplies the import payload (info, messages, optional
  location) and export envelope. Client methods use the existing authenticated,
  non-retrying HTTP transport and preserve typed server errors.
- Server mappings implement GET /api/session/{sessionID}/export?sanitize=true|false
  and POST /api/session/import. They are separate opt-in mapping methods; no host
  or composition file was edited. Mount under the existing authentication pipeline.
- Import preparation detaches and validates canonical schema/IDs, filters settled
  messages, rejects duplicate settled message IDs, checks parent/existing Session,
  and clamps viewed to idle. It hands only the source-imported fields to a typed
  canonical persistence boundary. No direct SQL fallback or fake import exists.

## Core-owner persistence handoff (required before enabling import)

Implement and register:

```
ISessionArchivePersistence.ImportAsync(SessionArchiveImport input, CancellationToken ct)
```

The record deliberately excludes archive projectID, original location/subpath,
fork and revert state. Its Location is the explicitly requested destination.
The owner adapter must:

1. Recheck Session absence and parent presence under the canonical write boundary.
   Existing Session IDs always conflict, even for identical input; this source
   operation is not prompt-admission first-wins. Preserve Session/message IDs.
   Global message-ID collisions must fail the transaction, never overwrite/remap.
2. Resolve/upsert the destination project with the existing location/project
   resolver. Use current app version, a newly generated source-style slug and
   destination-relative slash-normalized subpath. Do not reuse archive placement.
3. Publish the canonical SessionEvent.Created through the Bus/projector boundary,
   using id, parent, title, agent, model and metadata from the prepared input.
4. In that same commit transaction, insert the settled projections in supplied
   order. Source projection seq is index+1. Serialize each canonical message and
   store id/type separately from its other fields; preserve message time.created.
   Reserve the aggregate sequence through created-event seq + message count.
   Imported projections are not fictional historical durable events.
5. In the same transaction, apply cost/token counters, created/updated/idle/viewed/
   archived times and idle outcome. Viewed/outcome have already been normalized.
   Do not import fork/revert/share state, pending inputs or execution claims.
6. Return the canonical stored SessionInfo after commit; map a duplicate creation
   race to SessionArchiveConflictException and missing parent to the existing
   SessionMutationNotFoundException. Never expose a partial success.

The service's read checks are advisory, not concurrency protection. They cannot
replace these transactional checks. No store adapter was written in this owned
subtree because Session/Event persistence belongs to the Core owner.

## Server/composition handoff

Register SessionArchiveService using the existing SessionStore and SessionQueries.
Call MapSessionArchiveExportEndpoint for read/export. Call
MapSessionArchiveImportEndpoint only after the persistence adapter is registered.
These methods do not register authorization or bypass the host's access control.
Do not mount duplicate routes. None were mounted by this change.

The import protocol's explicit payload location wins. If absent, source uses
server cwd, unlike requestRef-based Location routes. Therefore import intentionally
does not take query/header location or trust the archive's original directory.
RequestLocation.QueryValue is reused for exact query-key matching on export.
Project discovery/metadata must be reused by the persistence owner, not re-created
in this endpoint or inferred from an archive projectID.

## Privacy and portability limits

Sanitize matches upstream, not a stronger blanket secret scrubber. It redacts
session title/metadata/directory/revert patch paths; user text, inline file bytes
and attachment references/names/descriptions/mentions; synthetic/system/skill text;
shell command/output; assistant text/reasoning/tool inputs/content/state; and
completed compaction summary/recent text. Empty top-level metadata stays empty;
present content/provider/tool state uses the source redaction marker behavior.

Source leaves other fields untouched, including IDs, projectID, subpath, models,
snapshot identifiers, tool error structures, assistant-level provider state,
location-switched payloads and failed-compaction errors. Do not promise sanitized
archives contain no private data. Sanitization must be explicitly requested;
false/omitted preserves the source's unsanitized default.

No events, instruction blob tables, filesystem snapshot trees, credentials or
configuration files are added to the archive. Embedded assistant snapshot metadata
may survive as ordinary message fields, but no file undo is promised without the
actual trees. Import does not restore archive revert/fork state.

## Transport, memory and bounds

Client uses ResponseHeadersRead and streamed JSON deserialization through its
existing transport; server uses streamed JSON request parsing and JSON response
serialization, not temporary files or whole-body string parsing. The source
contract is one complete JSON object, not NDJSON or a paginated archive.

The current read API materializes up to int.MaxValue ascending projections, then
filters settled messages. As upstream does, this implementation materializes the
full transcript; it is not a bounded-memory export. Sanitization and import
validation also make detached copies. Host HTTP request-body limits remain in
force; no guessed archive quota or silent transcript truncation is introduced.
The archive layer does not currently provide its own byte/message cap. Any future
cap must fail explicitly, never return a partial archive as complete. Decode errors
fail export rather than silently dropping malformed messages.

## Verification and status

Pinned .NET 11 isolated Client/Protocol build passed with zero warnings/errors.
The Core/Server build was blocked by other-owned JintCodeModeEvaluator constructor/
ErrorMessage errors; no archive-source diagnostics were reported. Core/Server
success is not claimed until that blocker clears. All verification is compilation
only: no tests, DB/SQL queries, import/export execution, production-file reads,
native/app execution, filesystem/network probes, commits or delegation occurred.

Import persistence and composition remain required handoffs. This is not a claim
of a mounted, runtime-verified end-to-end import feature.

## Source license

Sanitization logic is adapted from OpenCode, MIT License, copyright (c) 2025 opencode.
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the Software), to deal in the
Software without restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions: the above copyright notice and this
permission notice shall be included in all copies or substantial portions of the
Software. THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND, EXPRESS
OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
