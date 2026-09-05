# Local Tool Services

Bare registry construction remains empty. The concrete Location factory installs
the supported builtins for hosts that own permission replies; it does not authorize
calls through catalog visibility. This Tools pass does not change the runner.

`ToolLocationFactory` now supplies concrete opt-in composition of the five supported
local filesystem builtins plus skill loading and foreground shell with live `AgentCatalog` rules and the authoritative
permission map. See `HOST-COMPOSITION.md`; legacy registry construction is still empty.

## Injectable API

- `Locations.LocalToolLocation(directory, projectWorktree, home, markers)` implements
  `IToolLocation`. All three base paths must be absolute. The optional marker list
  defaults to `.git` and `.hg`; plugin marker discovery is not implemented.
- `Permissions.PermissionService(projectId, rules, grants, hook)` implements
  `IToolPermission` and `IAsyncDisposable`. Its required `IPermissionRuleSource`
  supplies current Session/Agent rules; its required `IPermissionGrantStore`
  supplies project-scoped saved grants. Optional evaluation hooks cannot bypass a
  configured deny. Hooks and stores must not reenter the service while evaluating
  or replying.
- `LocalPermissionRules` is a concrete, explicit host-managed source. Register each
  Session with `AddSession`, and use `SetAgent` for its configured rules. Unknown
  Sessions fail; missing agents deny all. This does not read the existing Session
  store or keep itself synchronized with it.
- `MemoryPermissionGrantStore` is a concrete process-local grant store. It is
  intentionally named as memory-only. To preserve `always` approvals across
  restarts, inject a durable store. Core rejects `always` with nonempty save resources
  when the store's `Persistent` capability is false. No database or always-allow adapter is supplied.
- `ToolFilePolicy(location, permission)` composes leaf resolution and external
  approval. Read, grep, and glob take this policy. Grep/glob also require a
  `RipgrepProcess` with a host-resolved executable.
- `LocalFileMutation(formatter)` supplies process-wide cooperating locks, snapshots,
  UTF-8 BOM preservation, parent creation, local writes and unified diffs. The host
  factory now supplies `Formatting.LocalFormatter` by default; that service observes
  actual formatter configuration and creates a pre-write plan. Write/edit take both
  the mutation service and `ToolFilePolicy`.
- `LocalLiteralShellPolicy(location, permission, executable)` is an explicit,
  restricted local shell policy for PowerShell or POSIX-compatible shells. It
  supplies source shell arguments, cwd and directory approvals, shell command
  approval, and source command-prefix grant arities. Pass it to `ShellTool` only
  when the restricted syntax is acceptable. This legacy adapter has no implicit executable lookup.
- The normal factory instead uses `LocalShellPolicy` and `ShellProcessSource`.
  These supply native scanning, executable selection, leaf approval and .NET 11
  foreground execution with combined file-backed output. See `SHELL.md` for the
  exact grammar, output limits and remaining background/lifecycle gaps.

## Approval Ownership

The host dispatcher consumes `PermissionService.Notifications` (single reader).
`PermissionAsked` includes the immutable request and canonical tool source from
`ToolContext.MessageId` and `CallId`. The authenticated user boundary calls
`ReplyAsync(requestId, owningSessionId, reply, feedback)`. Do not expose this method
as an agent tool. The service checks the owning Session, but the host remains
responsible for authenticating the caller.

`AssertAsync` waits asynchronously. Caller cancellation removes its pending request
and propagates cancellation. `AskAsync` returns a decision without waiting; such
requests remain host-owned until replied to or the service is disposed. `GetAsync`
and `ListAsync` expose pending requests. Notifications include replies and cancelled
requests; they are not durable bus events.

Rules are last-match action/resource wildcards, with slash normalization, Windows
case-insensitivity, `*`, `?`, and the special optional trailing ` *` suffix. Missing
matches ask. Any configured deny blocks before saved allows or hooks are applied.
Saved grants are appended only after this deny check.

Replies support once, always, and reject. With a durable store, Always saves the request's save resources
and reevaluates pending requests in the Location. Reject declines other pending
requests in the same Session. Feedback on the selected rejection becomes
`PermissionCorrectedException`; bare decline is `PermissionDeclinedException`, a
control-flow cancellation exception that must not become model-visible output.
Location disposal settles all pending approvals. An admitted reply is settled
independently of client cancellation.

Configured policy denial is distinct from user rejection. File, shell, skill and
webfetch leaves turn `PermissionBlockedException` into explicit recoverable
`ToolExecutionException` detail. The service preserves the original policy error
for non-tool callers; bare decline and cancellation remain control flow.

## Paths and Mutations

The source LocationMutation boundary is **lexical**, not a realpath sandbox. Paths
inside the Location or its non-root project worktree are internal. Other paths
require `external_directory` approval, using the target directory and nearest
ancestor project marker for save resources. Leading `~` and Windows shell drive
spellings are normalized. There is no source hardcoded protected-path deny list;
protected resources must be expressed in permission rules.

Following the source, final symlinks are followed for content I/O, but authorization
resources remain lexical. A link inside a Location can refer outside it without
becoming an external lexical resource. Hosts that need a symlink sandbox must not
treat this service as one. Mutation lock keys resolve existing link components;
missing targets use lexical keys, matching FSUtil's missing-path fallback.

Write/edit hold a transaction lock while reading the snapshot, building the preview,
and awaiting `edit` permission. Commit compares current bytes and resolved target
with that snapshot before writing, refusing stale approvals. This snapshot check
is an explicit local safety extension, not a claim of atomicity with external
writers. External writes can still race after validation. Started writes and BOM
restoration settle before the lock is released. These are not undo snapshots,
crash recovery, filesystem transactions, or exactly-once writes.

Edit implements canonical empty/identical-string validation, newline normalization,
non-overlapping exact matches, Unicode fallback, trailing-space line fallback, and
unique-match enforcement. Local diff generation uses a shortest-edit line sequence
and four-line-context unified hunks. The presentation/tie-breaking has not been
verified byte-for-byte against jsdiff. Its trace has an explicit complexity limit
and fails rather than allocate unbounded quadratic memory.

## Process Adapters

No adapter was executed during development verification.

Shell now captures both native standard streams to one file, then returns a bounded
50 KiB/2000-line tail. Full output is retained on disk; truncation includes its path.
Foreground timeout defaults to 120000 ms; zero disables it. Caller cancellation
remains distinct from timeout. Cleanup owns only the spawned process/tree and awaits
the .NET 11 managed root wait. No pipe reader can delay this capture's settlement.
Retention and disk quotas remain absent. See `SHELL.md`.

Search uses source ripgrep arguments, include globs, `.git` exclusions and ignore
behavior, not .NET regex or substring path exclusions. Both searches use a 30-second
timeout and limit-plus-one truncation detection. JSON/filename records are assembled
from fixed-size chunks with a 1,048,576 UTF-16-character cap; oversized records fail
explicitly and are never silently skipped. Canonical match text remains complete
so submatch UTF-8 byte offsets remain valid. Only human previews are shortened to
2000 UTF-16 characters, without cutting a surrogate pair. These record/preview
choices are intentional safety corrections to the original prototype.

The legacy literal policy still rejects substitutions, redirection and pipelines.
The normal factory does not use it: the native scanner supports command lists,
pipelines, basic redirects and recursively scanned `$(...)` substitutions, with
explicit failure for unsupported grammar. It is not full tree-sitter/portable parity.
Create hooks, hook-driven timeout changes, background jobs, shell IDs, progress,
background promotion, durable notifications, recovery, and PluginRuntime remain absent.

## Safe Composition and Remaining Gaps

After connecting the user approval dispatcher and current rules, a host may
explicitly register policy-backed `ReadTool`, `GrepTool`, `GlobTool`, `WriteTool`,
and `EditTool`. The Location factory also registers `ShellTool` with the native
scanner and process source. No parameterless builtin has
an authorization fallback. Webfetch now has a bounded, permission-backed implementation
and is registered only when an explicit `WebFetchTransport` is supplied; see `WEBFETCH.md`.

The canonical non-tool request API, authoritative permission Location map, current
Session/Agent rule adapter, and Server adapter requirements are documented in
`../Permissions/SERVER-ADAPTER.md`. The map retains idle Location services and owns
one notification dispatcher per service; request leases do not erase pending state.

The registry now implements ordered scoped transforms/disposal, override restoration,
reload, request snapshots, namespaces, and an explicit runtime codec contract.
See `REGISTRY.md` for API migration, supported schemas and the remaining CodeMode,
image normalization, and plugin/MCP integration gaps. Permission notifications need Server/UI wiring,
the host must opt into the concrete tool factory, and grants need a durable implementation
before claiming source lifecycle parity. Read now supports bounded media and canonical
structured paging. Read-triggered discovery is now implemented and requires the
Session-owned loader callback described in `READ-INSTRUCTIONS.md`. Missing-path
suggestions remain absent.
Hosted Environment drivers, watchers, undo/snapshots and LSP are not implemented.

Verification is build-only with `--artifacts-path` outside the repository. No
application, tool, child-process adapter, database, tests or benchmarks were run.
