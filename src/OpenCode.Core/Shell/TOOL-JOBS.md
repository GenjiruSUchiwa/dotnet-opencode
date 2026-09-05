# Model ShellTool and Job handoff

## Main owner: share the real Location runtime

`Core/Tools/Builtins/ShellTool.cs` now routes execution through `ShellRuntime.CreateToolAsync` and `ResultAsync`. It never invokes `ShellProcessSource.RunAsync`, creates an independent capture store, or reads a second environment snapshot.

Wire the existing factory registration with:

```csharp
new ShellTool(policy,
    runtime: () => shellLocations.ForLocation(location),
    jobs: hostJobAdapter)
```

The getter should resolve the existing `ShellLocationServices` runtime for the captured authoritative Location. It is lazy so tool registration does not reenter Location construction. `hostJobAdapter` is optional for foreground operation.

The previous `process` and `environment` constructor arguments remain only to keep current factory call sites compiling while their owner supplies this hook. They are not executed. A missing runtime fails explicitly; it does **not** fall back to the old foreground process path. ToolLocationFactory and main ServerHost were not edited in this pass.

## Implemented tool behavior

- Source-shaped input has required string `command`, optional string `workdir`, optional nonnegative integer `timeout`, and optional boolean `background`. Artificial command min/max lengths were removed from the advertised schema. Native timeout range remains the runtime's documented Int32 milliseconds limit.
- Foreground timeout defaults to 120,000 ms. Background defaults to zero; explicit background timeouts remain in force. Only foreground-to-background promotion clears the timeout.
- The real Location policy scans and asserts permission before process/capture creation. ShellRuntime captures Session/host environment before approval, including meaningful empty replacements. No allow-all permission adapter is installed.
- Progress reports the actual `shellID` immediately after creation. Cancellation or progress/job-admission failure removes the owned process/capture even when no job was successfully admitted. Progress and foreground waits are cancellable; cleanup does not replace the original user decline/interruption.
- Configured denial and corrected feedback remain recoverable ToolExecutionException values. User decline (`PermissionDeclinedException`) and caller cancellation remain interruption, not a model-visible fake success.
- Foreground output has source `output`, `truncated`, `status: completed`, optional numeric `exit`, and optional `timeout`. Content contains the output plus the separate exit/timeout notice. Timeout output includes the original timeout guidance. Metadata follows the source output shape; the old extra foreground `file` metadata is not manufactured.
- A real background handoff returns source `status: running`, `shellID`, the output path, and the automatic-notification/no-polling instruction. This happens only after the host has committed the recovery/notification identity.
- Nonzero command exit is captured output with an exit notice, not a transport/tool exception. Missing capture is a failure, never a fake empty successful result.

`ShellToolOutput` centralizes result/content/metadata and background notification rendering. No command-interpolation implementation was copied or modified.

## Required Job/PluginRuntime adapter

`IShellToolJobs` is a typed **host integration boundary**, not another in-memory job registry. No default implementation or fake durable store is supplied.

A concrete implementation now exists at `Core/Shell/Jobs/ShellToolJobs.cs`, backed by the generic `Core/Jobs/JobRuntime` and the existing Session admission/execution APIs. It still requires the Core/Event owner's real `IJobBackgroundStore` before host wiring can enable it. See `Core/Jobs/HANDOFF.md` for constructors, persistence coordination, shared subagent adoption, and explicit shell restart recovery.

```csharp
bool SupportsBackground { get; }
Task<ShellJobInfo> StartAsync(ShellJobStart input, CancellationToken ct);
Task<ShellJobBlock?> BlockAsync(string id, SessionId sessionId, CancellationToken ct);
Task<ShellJobInfo?> BackgroundAsync(string id, CancellationToken ct);
Task<ShellJobInfo?> WaitAsync(string id, CancellationToken ct);
Task CancelAsync(string id, CancellationToken ct);
Task AdmitCompletionAsync(SessionId sessionId, MessageId notificationId,
    ShellNotification notification, CancellationToken ct);
Task CompleteBackgroundAsync(MessageId notificationId, CancellationToken ct);
```

Types:

- `ShellJobStart(ShellInfo Shell, SessionId SessionId, Func<CancellationToken, Task<string>> Run)`.
- `ShellJobInfo(string Id, ShellJobStatus Status, MessageId? NotificationId, string? Error)`.
- `ShellJobStatus`: Running, Completed, Error, Cancelled.
- `ShellJobBlock(ShellJobInfo Info, bool Backgrounded)`.

Map Start to the source Job service as follows:

```text
id       = input.Shell.Id.Value         (never context.CallId)
type     = "shell"
title    = input.Shell.Command
metadata = { sessionID, shellID }
recovery = { kind: "shell", sessionID, shellID, command }
run      = input.Run
```

Using shell ID matters because CodeMode children may share one tool-call ID. Start owns Run independently of the submitting tool token and must preserve the supplied ID. Run returns the actual result messages for the host's normal job output/recovery persistence. Cancel must cancel Run; cancelling an unknown job is a no-op.

Block must join the source done/backgrounded state and track the blocking Session so source backgroundAll/promotion can work. A normal completed result requires actual Run completion/capture. Error/cancelled job states become source tool failures. No completed job or output is synthesized when the adapter returns missing/inconsistent state.

`SupportsBackground` must remain false until the host has **all** of: durable background markers, stable notification IDs, Session completion admission/wake, and recovery ownership. With no adapter, foreground uses the shared shell wait directly. Explicit background input fails before approval/spawn. Promotion through an adapter without the required capability/identity also fails and cleans up its owned command.

BackgroundAsync must persist the marker before returning a valid NotificationId, and must not report a cancelled waiter after that commit. Backgrounded Block results have the same requirement. Once this handoff is confirmed, late caller cancellation must not cancel the independent background job. If Location shutdown prevents local observation after a committed handoff, the real host marker remains for recovery.

## Completion admission and recovery

ShellRuntime owns the local notification-observer tasks and cancels/joins them during shutdown. This task ownership stores no Job status or durable marker. Observers wait for real terminal Job state, then build the source `<shell ...>` notification with actual job ID, shell ID, command, state, captured output/exit/timeout when available.

`AdmitCompletionAsync` must reconcile/admit using the **provided stable MessageId** and schedule the Session wake only after commit. These are **model background-job** notifications and resume execution, unlike `ISessionShellLifecycle.NotifyAsync` for explicit user `session.shell`, which remains admit-only/resume:false. Do not reuse the user-shell adapter as a fake auto-wake path.

Only after admission succeeds does the observer call CompleteBackgroundAsync to remove the marker. Failure/shutdown leaves it intact. The concrete adapter now implements stable admission/wake and source shell restart notices. The coordinated KV `job.background/` storage and startup authority/order remain CoreSession/Event owner work; this module does not directly write SQL, call an assistant-message substitute, or claim in-memory state is durable.

## Source mapping

- `packages/core/src/tool/plugin/shell.ts`: input/defaults, permission preparation, progress, job.start/block/background/cancel, promotion timeout reset, result rendering, notifyWhenDone ordering.
- `packages/core/src/job.ts`: the adapter's canonical identity/state/blocking/recovery requirements, stable notification markers, and marker removal after admission.
- `packages/core/src/shell/result.ts`: output notices, metadata, and shell notification format.

Verification is build-only with the pinned local .NET 11 SDK. No tool, command, background job, notification, DB/admission, process, or recovery operation was run.

Final full Core and Server builds succeeded with **0 warnings and 0 errors**, using `.\.dotnet\dotnet.exe`, `--no-restore`, and isolated `C:\tmp\opencode\mcp-finish-pass` artifacts. No tests, cleanup execution, shared project changes, or commits were made.
