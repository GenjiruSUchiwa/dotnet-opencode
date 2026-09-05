# Source launcher and text-selection fixes

## Incremental source builds

- `run.ps1` now retains the ignored, checkout-local `artifacts/run/<configuration-key>`
  compiler/restore cache instead of allocating and deleting a fresh build tree on
  every invocation. SDK, architecture and PTY packaging choices select the cache.
- Compiler/build-server reuse is enabled. The source fingerprint checks still run
  before and after building; an unsuccessful or mixed-source build is never launched.
- A file lock serializes builds and runtime copying. Each running CLI loads a
  separate temporary runtime copy, so it cannot lock the mutable build cache.
- The launcher uses MSBuild's existing complete Server staging instead of copying
  that Server deployment a second time itself. Managed servers retain their own
  immutable deployments, independent of the TUI's lifetime.
- Cleanup removes only the invocation's exited runtime copy. It preserves the
  incremental cache and does not sweep old outputs or stop processes automatically.

Two build-only invocations with the same Debug/run artifact path passed with zero
warnings/errors. The cold build took 2m 36.46s; the unchanged second build took
9.15s, skipped all 11 CoreCompile targets, and retained the CLI DLL's timestamp.
These measurements exclude launching the application.

## Selection

- Plain `TuiText` is selectable by default, matching upstream OpenTUI 0.5.9
  `TextBufferRenderable`. Error/status text no longer needs an explicit opt-in.
- Home `Composer` forwards pointer down/move/up to the same existing prompt
  selection handlers as the Session composer.
- Windows console input enables mouse events and disables Quick Edit while owned,
  restoring the original console mode on disposal.

Native text extraction, clipboard actions, renderer coordinates, and editor
selection algorithms are not replaced. Physical drag/copy behavior has not been
runtime-verified in this pass.

## Startup HTTP 500

Authorized read-only diagnostics confirmed that the registered .NET process owned
its loopback listener, but `/api/health` returned an empty HTTP 500. A short
exception trace of that same process identified ASP.NET's error:

```text
Body was inferred but the method does not allow inferred body parameters.
projectID: Route; input: Body; worktrees: Services
```

The worktree DELETE handler now explicitly marks its existing JSON payload
`[FromBody]`. All endpoint bindings are materialized before the host starts, so an
invalid route fails through startup diagnostics rather than poisoning health after
the server announces ready. Invalid/empty health responses include their HTTP
status in the client error; instance verification and replacement guards remain.
The TUI no longer appends a lifecycle error's already-included recovery action twice.

No existing daemon was stopped, no database was inspected or modified, and no tests
were added or run. The diagnostic helper lives outside the repository. The running
old daemon needs an explicitly authorized restart to load the corrected code.
