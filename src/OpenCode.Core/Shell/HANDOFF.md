# Shell domain and Session lifecycle handoff

## CoreSession owner: exact durable boundary

`ISessionShellLifecycle` is declared in `Core/Shell/ShellResult.cs`:

```csharp
Task StartedAsync(SessionId sessionId, EventId? eventId, ShellInfo shell, CancellationToken ct);
Task EndedAsync(SessionId sessionId, ShellInfo shell, ShellOutput output, CancellationToken ct);
Task NotifyAsync(SessionId sessionId, ShellNotification notification, CancellationToken ct);
```

Implement this in the existing Session aggregate/event/admission boundary and register it with Server DI. No implementation, SQL, fake ShellMessage, assistant-message insertion, compaction publisher, or fallback event sink was added by this domain.

- Started publishes canonical `session.shell.started.1` data `{ sessionID, shell }`, forwarding the optional **EventId** from the request as the event ID. The process has actually started when this method is called, matching source. Do not substitute a model tool-call/message ID.
- Ended publishes canonical `session.shell.ended.1` data `{ sessionID, shell, output }`. `shell` is the existing Schema `ShellInfo`; `output` is the existing Schema **`ShellOutput`**, not a string: `{ output, cursor, size, truncated }`. Output is a first-page preview capped at 1 MiB, with absolute UTF-8 byte cursor/size. The normal page's `truncated` remains false as in the source shell output API; the separate bounded tail determines notification truncation.
- Notify admits a synthetic Session inbox item with `resume: false`. `ShellNotification` contains `Text`, `Description`, and JSON `Metadata`. Completed-command metadata contains the actual `shellID`, `source: shell`, state, truncation, and optional exit/timeout. Use that identity for the owner's stable Message ID reconciliation before `SessionStore.AdmitInboxAsync`; do not invent a second inbox writer or wake a model before commit. The Shell module does not choose a new synthetic-ID policy.
- A spawn failure has no real ShellInfo and emits only the source-shaped synthetic failure notification (`source: shell`, `state: error`); it does not manufacture a shell.started/ended pair. Its description is the attempted command.
- Resolve the Session's **current** Location when publishing/admitting. Execution stays on the original Location; durable events must not remain pinned there if the Session moved.
- A Session deleted before completion notification admission may raise the existing `SessionMutationNotFoundException`; the host ignores that final notification case as source does. Other durable publication/admission failures remain failures, not successful no-content placeholders.

`SessionShellHostService` refuses to spawn before this adapter is available. Its background operation owns the command result and these callbacks after the submitting HTTP client disconnects. There is no claim that a process can be recovered after host death; background-job KV recovery and dangling Session shell reconciliation remain separate owner work.

## Runtime API

One `ShellRuntime` belongs to the existing authoritative implicit-local tool Location:

```csharp
Task<ShellInfo> CreateAsync(ShellCreateInput input, CancellationToken ct = default);
Task<ShellInfo> CreateToolAsync(ShellCreateInput input, ToolContext context,
    IToolShellPolicy policy, CancellationToken ct = default);
Task<IReadOnlyList<ShellInfo>> ListAsync(CancellationToken ct = default);
Task<ShellInfo> GetAsync(ShellId id, CancellationToken ct = default);
Task<ShellInfo> WaitAsync(ShellId id, CancellationToken ct = default);
Task<ShellInfo> TimeoutAsync(ShellId id, double milliseconds, CancellationToken ct = default);
Task<ShellOutput> OutputAsync(ShellId id, ShellOutputInput? input = null, CancellationToken ct = default);
Task<ShellResult> ResultAsync(ShellInfo started, int maximumBytes = 51200,
    int maximumLines = 2000, CancellationToken ct = default);
Task RemoveAsync(ShellId id, CancellationToken ct = default);
```

- Create returns a registered real process ID/status/file immediately; callers can wait for foreground behavior or keep observing the process-local command. List includes running commands only. The newest 25 exited entries/captures remain readable; eviction removes their files and publishes deleted.
- Timeout replaces the deadline **from now**; zero clears it. This bounded native implementation explicitly supports integer milliseconds through `Int32.MaxValue`. It does not silently clamp larger source-valid durations.
- Output reads actual combined stdout/stderr capture bytes, defaulting to cursor 0 and 65,536 bytes. Cursor and size are absolute UTF-8 **byte** offsets, not character counts. Missing capture is an explicit error/absent `ShellResult.Capture`, not fake empty output. Per-page buffers must fit Int32; no unbounded whole-output read is used.
- Result waits, then returns the real terminal Info and a bounded tail. `(no output)` appears only for an actual empty capture. A known command removed before result lookup follows the source's killed/missing-capture result rule. Native lifecycle observation failure is unavailable, not a fabricated running or successful status.
- Remove terminates only its owned process tree, removes the registry/capture, fails pending waiters with ShellNotFound, and publishes deleted. The canonical HTTP interrupt operation is DELETE; no `/interrupt` or `/wait` route is invented.
- Runtime shutdown cancels preparation/waiters and stops owned processes without publishing a fake terminal command result. Captures left by shutdown remain on disk. Seven-day mtime orphan cleanup is now implemented by `ShellOutputRetention` and the host's opt-in maintenance loop; see the Server handoff for ownership requirements.

## Permission and process ownership

Source distinguishes explicit authenticated-user shell requests from model tool execution. `CreateAsync` is the former, matching `shell.create` and `session.shell`: their source payloads have no ToolContext and do not invoke a fake agent permission approval. Server must mount these endpoints under its existing authentication middleware and supply real plugin readiness/configured shell selection.

Model tool callers must use `CreateToolAsync` with the actual existing Location `IToolShellPolicy` and `ToolContext`. It invokes that policy's full supported command scan and permission assertions before creating a process or capture file. No allow-all adapter, synthetic Session ID, second permission map, or model runner exists here. Tool caller cancellation of a foreground wait must remove its owned shell, as source ShellTool's interruption finalizer does.

The existing `LocalShellPolicy` remains the scanner/permission boundary; unsupported compound statements, assignments, script blocks, arrays, here-documents, backtick substitutions, background grammar, and unsupported shell grammars remain rejected. This module does not replace that scanner or claim full tree-sitter parity.

`ShellProcessSource.RunAsync` exposes only a completed foreground result, not a running handle/file or replaceable timeout. The new lifetime uses the same .NET 11 ownership pattern: explicit ProcessStartInfo, null stdin handle, one shared file handle for stdout/stderr, explicit inherited handles, KillOnParentExit where supported, owned-tree termination, and SafeProcessHandle wait/kill/reap. Handles are closed on failed starts and terminal cleanup. This is **not** ConPTY, a persistent terminal daemon, or a replacement for Core/Pty.

SessionEnvironment is the existing host singleton. A valid Session metadata ID selects its replacement snapshot; a present empty map clears inherited variables. Otherwise the host environment is captured before preparation/approval. TERM and OPENCODE_TERMINAL are applied to the child only. No environment values are added to metadata/events or written to process-global environment.

## Explicit remaining boundaries

- Model ShellTool now executes through the shared ShellRuntime when its owner supplies the runtime getter. No separate ShellProcessSource fallback remains. Foreground works without a Job adapter; explicit background/promotion requires the real durable host adapter. See [TOOL-JOBS.md](TOOL-JOBS.md) for constructor wiring, exact output/progress/cancellation behavior, and `IShellToolJobs`.
- Job start/block/background/cancel and completion-admission orchestration is implemented against that interface. No Job KV implementation, backgroundAll registry, or restart recovery was fabricated. The host must supply those real capabilities before SupportsBackground becomes true.
- Plugin readiness is mandatory host input. The `shell.create.before` transformation hook is not ported here; the readiness callback must reject unsupported configured hooks rather than pretend they ran.
- Result tail defaults are source defaults; host callers may supply configured limits, but this module does not independently load Config.tool_output.
- Native parent-exit/tree ownership follows the existing foreground adapter. It does not claim Unix detached process-group parity or control of descendants that have already escaped/exited their original root before observation.

## Source map

- `packages/core/src/shell.ts`: lifecycle, running-only list, wait, timeout replacement, byte paging, exited retention and events.
- `packages/core/src/shell/result.ts`: bounded result, missing capture, exit/timeout notices and user synthetic notification.
- `packages/core/src/shell/select.ts`: configured/manual argument conventions (cmd /c, PowerShell noninteractive arguments, other shells -c); executable selection is host-injected.
- `packages/core/src/tool/plugin/shell.ts`: real tool permission preparation and the explicitly deferred Job/background-notification boundary.
- `packages/core/src/job.ts` and `plugin/runtime.ts`: durable background markers and Session notification ownership, not replaced with a local fake.
- `packages/core/src/session/session.ts` lines 183–240: server-owned session.shell operation and durable Started/Ended plus admit-only synthetic completion.

Verification is build-only. No process, shell, app, API, network, database, Session event, or native runtime was executed to verify these changes. No tests or shared project files were changed.
