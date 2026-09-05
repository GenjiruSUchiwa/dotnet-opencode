# Persistence parity follow-up

This pass follows the accepted EF migration checkpoint. It fixes concrete source
differences; it does not repeat the migration review or claim full runtime parity.
Inspection and verification remain source/build-only. No tests were added, edited,
or run. No application, serializer, EF model, database, SQL statement, migration,
provider, native process, MCP, or PTY runtime was executed for verification.

## Diagnostic fixes

| File | Change | Preserved behavior |
| --- | --- | --- |
| Core/Forms/FormService.cs, AskAsync | Explicit ConfigureAwait(true) on completion.WaitAsync(ct) | The existing caller-context continuation, cancellation token, cancel-publication path, and rethrow remain unchanged |
| Core/Reference/ReferenceSources.cs, Observe | NonBacktracking for the existing `[/\s\x60,]` character-class match (the source spells the backtick literally) | Same empty-name check, Unicode whitespace matching, and rejected characters; no timeout or global exemption |

## Implemented source gaps

### 1. Session path codecs were missing from filters and some projections

Upstream `packages/core/src/database/path.ts` defines three distinct boundaries:

- `absoluteColumn`: validate portable absolute paths and encode Windows separators.
- `directoryColumn`: the same behavior, but retain an empty legacy Session directory.
- `pathColumn`: normalize separators on Windows without imposing absolute-path rules.

Upstream `session/store.ts:104–107` compares directory/subpath columns through those
Drizzle codecs. The .NET query had compared the raw caller strings instead. A
Windows directory such as `C:\repo` therefore did not match its `C:/repo` storage
representation. The same issue applied to a subpath such as `src\feature`.

The follow-up adds `ProjectPaths.DirectoryStorage`, `DirectoryPlatform`, and
`Relative`, and uses them consistently in:

- `SessionQueries.ListAsync` directory/subpath predicates.
- `SessionStore.ReadSession` directory/subpath output.
- `SessionCreation.ProjectAsync` persistence values.
- `MoveProjector.ProjectAsync` destination persistence and previous-location history.
- `ForkProjector.ProjectAsync` parent path copying.

Empty legacy directories remain empty. Empty subpaths remain empty in storage and
in previous-location snapshots; SessionInfo still omits its empty subpath. Nonempty
relative directories are rejected by the same absolute-path boundary already used
for Projects. No filesystem resolution or existence check was added to these codecs.

### 2. Move history exposed stored rather than platform paths

Upstream `session/projector.ts:270–295` selects the old location through the column
decoders and treats an empty workspace ID as absent. The .NET previous-location
snapshot used the raw stored directory/subpath and attempted to validate an empty
workspace ID as a real ID.

`MoveProjector` now decodes the previous location through the Session path codecs
and applies the same truthy workspace-ID rule. The visible move message still uses
the event's destination. Placement updates, the activity timestamp, and message
insertion still commit within the existing event transaction; instruction epochs
are not reset.

### 3. Direct Core queries imposed an extra default page limit

Upstream `session/store.ts:134–135,167–168` uses `query.all()` when a limit is omitted.
The native `SessionListQuery` had defaulted to 50, and the message-query method
required every argument.

The current Core query surface now defaults to an unbounded limit (`-1` in the
existing SQLite query helper). `MessagesAsync` also exposes the upstream defaults
of descending order and no anchor. Explicit limits are unchanged.

The HTTP boundary is not changed: upstream `server/handlers/session.ts:56–60` and
the native `SessionQueryParameters` supply explicit server limits. Existing legacy
`SessionStore.ListSessionsAsync` / `ListMessagesAsync` convenience defaults are not
changed by this pass. No SDK/Auth/ServerHost counterpart is required for these Core
query changes, and ServerHost was not edited.

### 4. Previous pages decoded messages in the reverse failure order

Upstream `session/store.ts:137,170–173` reverses the selected raw rows before mapping
Session information or decoding projected messages. The native query reversed its
already-decoded DTO list. Although successful output ordering matched, schema-invalid
messages could report a different first failing message on a previous page.

`PersistenceQuery.ReadPageAsync` now buffers and reverses only previous-page rows,
then lets the existing adapter decode them in requested order. Forward pages retain
streaming. Keyset predicates, time/ID ties, missing-anchor behavior, explicit limits,
and message decode exception types are unchanged.

### 5. Fork parent validation and path copying bypassed source behavior

Upstream `session/projector.ts:110–129` reads the parent before checking the boundary.
Its parent directory/subpath pass through read/write codecs before insertion at
lines 149–171. The native projector checked the boundary first and copied path
columns directly with INSERT SELECT.

`ForkProjector` now reads and validates the parent first and supplies encoded path
values to the existing named `ForkSessionAsync` intrinsic. Remaining parent fields
still copy through INSERT SELECT. The Session insert, inherited instruction entries,
settled message copy, high-water reservation, instruction state, and retained fork
event remain in their original transaction/projection order. No parent events or
unfinished messages are fabricated, and the selected high-water mark remains reserved
even when its boundary message is not copied.

## Reviewed, unchanged boundaries

- `worktree.ts:243–355` and `git.ts:638–681`: native create/remove/refresh SQL and
  notification ordering already follow the reviewed source shape. Git listing uses
  the source's non-NUL porcelain format and trimming; this pass does not replace it
  with a different parser or run Git.
- Schema/bootstrap ownership, the 46-entry source journal, explicit MigrationTarget,
  blocked destructive/legacy transitions, claim ownership, replay, and recovery policy
  are unchanged.
- The documented archive outcome serialization discrepancy and unversioned statistics
  usage query are not changed. Those are separate review decisions.

## Concrete handoff outside this slot

Upstream `packages/core/src/session/projector.ts:75–103` defines
`publishSessionUsage`, which emits the ephemeral `SessionEvent.UsageUpdated`
(`session.usage.updated`) after reading current usage. A source search finds no
matching C# event definition/type in the current `src` tree.

This requires a Schema/event-manifest and serializer-contract counterpart before
the persistence owner can add the corresponding post-commit publication. The event
feed/client consumers must recognize that contract. Do not synthesize a new durable
event, publish from a SaveChanges interceptor, or emit an unregistered envelope to
work around the missing contract. Schema and runtime foundations are outside this
pass, so no such changes were made.

## Verification

Use the repository SDK `11.0.100-preview.7.26381.103` to build the entire CLI graph,
with isolated artifacts under `C:\tmp\opencode\persistence-parity-20260905-f914-01`,
`OpenApiGenerateDocuments=false`, and `OpenCodePackagePersistentPty=false`.
The final handoff reports exact build diagnostics. Compilation does not establish
runtime query translation, path behavior, event ordering, or database parity.

## Next pass: usage publication and embedded WebSearch

The upstream usage subscription is explicit in `session/projector.ts:758–767`:
Step.Ended, Step.Failed with both cost and tokens, and UsageRecorded trigger a read
of current usage through `publishSessionUsage` (`:75–103`). It is not a generic
reaction to every write of a usage column. Archive restoration and fork/creation
therefore must not acquire new usage notifications merely because they write totals.
Silent replay and duplicate replay likewise must not publish a follow-up.

The publication integration waits for the Schema owner's exact public contract.
It must read after native commit, use the ephemeral definition, retain primary-event
notification order, and isolate post-commit notification failures from the already
committed operation. It must not add durable history or run from an EF interceptor.

The embedded SDK now calls the existing public `AddNativeWebSearch` registration.
Its `LocalToolOptions` consumes the registered `INativePluginSource` definitions,
publishes typed plugin-added/plugin-updated events to the host's existing feed, and
binds `WebSearchPluginSource.ReadyAsync` directly. The authoritative tool factory
activates provider plugins before invoking this readiness callback and refreshes
the binding before model snapshots.

This does not allocate a second WebSearch runtime, provider registry, HTTP transport,
or Location map. WebSearch's own update events use the same `IEventFeedService` via
the shared source. The callback deliberately does not call
`CommandHostService.AcquireAsync` or `IWebSearchLocationSource.AcquireAsync`: either
would reenter the Location whose tool factory is currently establishing readiness.

The SDK's custom lifetime is unchanged. This wiring adds no WebApplication,
ConsoleLifetime, listener, election, managed-daemon connection, or startup recovery.
Detached managed servers remain independent of embedded SDK disposal. ServerHost
remains outside this slot and is not edited.
