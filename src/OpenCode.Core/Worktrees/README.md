# Local worktree lifecycle

Source: `packages/core/src/worktree.ts`, `worktree/git.ts`, `worktree/directory.ts`,
`git.ts` worktree operations, `project.ts`, `session/projector.ts`, and
`packages/protocol/src/groups/worktree.ts` / `server/src/handlers/worktree.ts`.

The current source API has **list/create/remove/refresh**, not reset. No reset,
checkout-to-branch, or hard-reset endpoint is invented here.

## Create and setup

`WorktreeService` owns a per-host strategy registry with the actual Git strategy.
Source directories must be canonical existing directories already registered for
the requested Project. Missing/unknown sources are not replaced with process cwd.
Unknown strategies fail explicitly. Configured/discovered plugin hooks are not
silently skipped by this native slice.

The parent directory is created, then the source adjective/noun slug is used only
when name is omitted. Supplied names are neither trimmed nor sanitized. Name
collisions use -2 through -10 and then fail. Git is invoked with separate arguments:

`git worktree add --detach -- <directory> <ref-or-HEAD>`

No branch name or source ID is generated. The created repository is rediscovered
and its directory canonicalized before storage. Only then does the shared worktree
table upsert commit and emit ephemeral `worktree.updated` when the row changed.

After that commit/publication, the current Project commands.start is read and
trimmed. It runs with the source platform convention: Windows COMSPEC shell syntax
or `bash -lc` on Unix, inherited environment, ignored stdin, destination cwd, and
OPENCODE_WORKTREE_BASE/PATH. This is the authenticated user worktree lane, not an
agent tool approval shortcut. Git invocations never use shell interpolation.
Startup failure leaves the real created/stored worktree intact, as upstream does;
there is no guessed filesystem rollback or fake successful completion.

## Remove and refresh

Remove requires a stored strategy, so an unclassified primary/root directory is
not accepted. It discovers the actual repository and invokes Git remove from the
common Git directory. The supplied force flag is retained. A Git failure preserves
the row; success removes it and emits updated only after the database change.
The protocol error is `{name:"WorktreeError",data:{message,forceRequired?}}`.

Refresh reads stored rows, lists real strategy worktrees from existing unclassified
roots, canonicalizes discoveries, and transactionally upserts classifications and
removes missing-directory rows. Only changed rows trigger updated. Time-created is
preserved for existing rows. Windows persisted paths use source slash encoding and
are converted back on read. No migration or alternate project database is created.

The source remove operation does not directly interrupt Sessions, delete Session
data, or eagerly invalidate an active Location; this implementation does not add
those side effects. PermissionLocationMap now matches the source zero idle TTL:
when the final lease is released and an implicit-local directory is missing, it
closes the existing shared Location callbacks/resources. Borrowed Locations are
not closed early and explicit workspace paths are never probed locally.

## Project resolution and adoption

`ProjectDiscovery.ResolveAsync` wraps the existing CatalogLocation identity resolver.
It preserves clone canonical directories unless the old canonical directory is
gone, then publishes the real project update. New native repository directories
commit `worktree.resolved.1` and their worktree row atomically through the existing
EventStore; no new durable event family or project IDs are invented.

The same event transaction implements source Session adoption: eligible old/global/
markerless Project ownership changes project_id/subpath only. Session directory,
transcript, instructions and recency remain untouched. Explicit workspace Sessions
are not adopted. This is source projection, not Session movement or cluster fencing.

All public CatalogLocation calls now enter ProjectDiscovery; only its identity-only
helper is internal. Server, SDK and direct Core consumers therefore share the same
worktree facts/adoption boundary without a Core-to-Server dependency or recursion.
A Location-owned one-shot refresh runs after tool Location construction and is
cancelled/joined on that Location's close. The stored-source requirement is never
bypassed to make creation appear available.

## HTTP and Client

Mounted paths are GET/POST/DELETE `/api/worktree/{projectID}` and POST
`/api/worktree/{projectID}/refresh`. Lists and created Info are bare source shapes;
remove and refresh return 204. Client methods: `ListWorktreesAsync`,
`CreateWorktreeAsync`, `RemoveWorktreeAsync`, `RefreshWorktreesAsync`.
Schema fields and Protocol payloads preserve trimmed nonempty strategy/ref strings,
unmodified optional names, required force, and omitted optional fields.

No Git, startup script, worktree/branch creation, database operation, application,
native call, API request, or test was executed for verification. Only isolated
pinned .NET builds were run. Runtime behavior and unusual filesystem cases remain
unverified; no exactly-once or atomic filesystem guarantee is claimed.
