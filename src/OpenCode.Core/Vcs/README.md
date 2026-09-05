# Native VCS read adapter

Sources: `packages/core/src/plugin/vcs/git.ts`, `vcs.ts`, `vcs/patch.ts`, and
`packages/server/src/handlers/vcs.ts`. `LocalVcs` receives the real Location from
`CatalogLocation`/`RequestLocation`; it does not invent a project or fall back to
the daemon's cwd when a requested Location fails.

Implemented HTTP operations: `vcs.get`, `vcs.base`, `vcs.status`, `vcs.branches`, and `vcs.diff`.
Responses retain the canonical Location envelope and existing Schema values.
Client methods are `GetVcsAsync`, `VcsBaseAsync`, `VcsStatusAsync`, `VcsBranchesAsync`, `VcsDiffAsync`.
Schema `VcsBase` and strict-string `VcsDiffMode` own the new contracts. Base's
Location response preserves `data: null`, not an omitted property.

## Git behavior

- All process calls use .NET 11 `Process.RunAndCaptureTextAsync`, separate
  ArgumentList entries, ignored stdin, inherited environment, and explicit UTF-8
  output. No shell, Git config mutation, checkout, fetch, index staging, or worktree
  creation is performed by this adapter.
- Flags match the source read adapter: no optional locks; autocrlf/fsmonitor false;
  longpaths/symlinks true; quotepath false. Listing pathspecs are scoped to the
  requested directory. Per-file calls use the worktree root because Git lists
  root-relative paths.
- Current/default branch, remote precedence, configured default branch, main/master
  fallback, sorted branch refs, literal escaped search, and the 100-result HTTP cap
  follow the Git provider. Detached/unborn branch metadata is not fabricated.
- Review-base inference checks up to 256 reflog entries, rejects renamed/ambiguous
  origins and non-branch revision expressions, and verifies both ancestry relations.
  Default-branch inference is allowed only while on that branch. Ambiguity returns
  the exact choose-base error, not a guessed main/master comparison. No HEAD or no
  provider returns null. Base/diff wait for the supported Location initialization
  boundary with the source's five-second timeout.
- Status uses NUL-delimited porcelain and numstat, no renames, source added/deleted/
  modified classification, and actual no-index untracked-file statistics.
- Diff implements working, branch, and committed comparisons, unborn HEAD behavior,
  explicit base resolution through --end-of-options, merge-base, untracked additions,
  numstat, native binary patch output, batched patches, and per-file fallback.
- Default context is int.MaxValue. The source's 10,000,000-byte per-patch/total
  output budgets and empty-patch fallback are preserved. Trailing partial batch
  chunks are dropped. No patch or line count is inferred from tool history.

## Honest limitations

No Git marker is an authoritative no-provider result, so info has an empty branch
object and arrays are empty. Missing Git in an actual repository, failed status,
or failed diff commands are errors, not successful empty catalogs. This is stricter
than upstream's defensive fallback for some provider failures, intentionally avoiding
false empty native support. Configured/discovered plugin providers and Mercurial
remain explicit unsupported cases. Advanced workspace placement is not guessed.

The current .NET capture API collects process output before the adapter applies
the wire byte budget; this does not yet match upstream's bounded capture memory.
Sort order uses the host's current culture and has not been cross-checked against
every Node ICU locale. Context/counts are constrained by the existing native int
schema. No filesystem watcher or fabricated vcs.branch.updated event is added.

Worktree mutations are deliberately not advertised through these read routes.
No Git command, live database read, application, process operation, or test was run
for verification; only pinned SDK builds were performed.
