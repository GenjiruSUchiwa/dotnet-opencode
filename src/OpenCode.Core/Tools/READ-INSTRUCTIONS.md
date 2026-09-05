# Read-Triggered Instructions

`ReadInstructionDiscovery` implements the discovery block in
`core/tool/plugin/read.ts`. `ReadTool` invokes it after a successful filesystem read
and before returning file/text/media/list content. Permission assertions still occur
before reading. A missing discovery/loader dependency refuses the read before I/O.

Discovery skips lexical external-directory targets. For internal reads it resolves
the target and Location paths through existing symlinks, starts at the directory
itself for listings or the file's parent for files, and walks upward for `AGENTS.md`.
The resolved Location-root file is excluded because baseline discovery already owns
it. Candidate order is nearest-to-farthest. Like source FSUtil.up, if the resolved
Location stop is not an ancestor (for example an internal project-worktree sibling),
the walk ends at the filesystem root rather than inventing a different boundary.

The required boundary is:

```csharp
public delegate Task LoadReadInstructions(
    SessionId session,
    IReadOnlyList<string> paths,
    CancellationToken ct);
```

Pass the real Session implementation as the required third argument of
`LocalToolOptions(home, ripgrepExecutable, loadReadInstructions, ...)`. The factory
constructs the Location-scoped discovery object. Direct `ReadTool` producers must
also supply it. No default/no-op callback or instruction registry is installed.

## Session Owner Requirement

The Session owner has now supplied `Session.ReadInstructionLoader`, reached through
SessionStore's `LoadReadInstructionsAsync`, and the daemon binding wires the required
callback. Catalog/epoch APIs remain separate. `AdmitInboxAsync` is not a substitute:
it enqueues rather than directly committing the synthetic into current history.

Hosts must use that real Session callback with these source semantics:

- Claim in-flight Session/path pairs for parallel reads; release claims on every exit.
- Query current model-visible history, not a permanent process cache or the whole
  historical aggregate. Only synthetic `metadata.instruction.paths` is the lasting
  deduplication ledger, so compaction/revert can make a path eligible again.
- Read claimed, not-already-visible paths. Skip missing/unreadable files as in
  `readFileStringSafe`. Do not transform these into epoch/API instruction entries.
- Atomically commit a direct durable synthetic with text blocks
  `Instructions from: <absolute path>\n<content>`, joined by blank lines.
- Use `Loaded <paths>` as description, making paths relative to the resolved project
  root when contained; retain absolute paths outside it.
- Commit `metadata: { instruction: { paths: readableAbsolutePaths } }` with that
  same synthetic. Return only after event/projection settlement, then release claims.

The discovery hook supplies candidates to the mandatory Session-owned callback without
adding a second history ledger. The Session implementation and daemon wiring are not
owned by Tools; other hosts must provide the same real boundary before read execution.

Source discovery/load failures are best effort after successful reads. This adapter
logs such failures through Trace and still returns the read result. Interruption is
not swallowed. Missing wiring is a separate construction/execution readiness error,
not a swallowed best-effort failure. No runtime instruction discovery or callback was
invoked during verification.
