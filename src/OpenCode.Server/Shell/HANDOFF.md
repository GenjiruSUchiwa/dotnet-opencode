# Shell Server/Client wiring

## Registration

No ServerHost, main SessionEndpoints, existing Tools/Pty, or Session/event implementation was edited. Parent Server owner mounts:

```csharp
services.AddShellServices(new ShellHostOptions(
    ResolveShell: resolveConfiguredShell,
    FlushPlugins: ensureShellPluginReadiness,
    OutputDirectory: location => Path.Combine(channelData, "shell-dotnet", location.Project.Id.Value),
    CleanupOwnedOutput: true));
app.MapShellEndpoints();
```

Signatures:

```csharp
Func<LocationInfo, CancellationToken, Task<string>> ResolveShell;
Func<LocationInfo, CancellationToken, Task> FlushPlugins;
Func<LocationInfo, string> OutputDirectory;
```

ResolveShell must return an absolute configured shell executable using source config-priority selection. On Windows the existing public `PtyShellSelection.Resolve(location)` can supply selection only; it does not spawn a PTY. Do not use daemon deployment cwd or a fake executable fallback. OutputDirectory must be absolute and belong to the existing .NET channel's output storage.

FlushPlugins must actually verify readiness and reject unimplemented shell hooks. No no-op plugin-success default is provided. This module authors noninteractive shell execution, not a plugin supervisor or permission bypass.

Register the CoreSession owner's `ISessionShellLifecycle` adapter before the host starts. See `Core/Shell/HANDOFF.md` for the exact canonical event/output/admission contract. Without it, `session.shell` returns a truthful unavailable response **before process creation**. Never substitute AppendAssistantEventAsync, AddMessageAsync, a compaction publisher, direct SQL, or synthetic ShellMessage construction.

Register hosted Shell services after the shared tool/permission Location graph. During Location invalidation, **before** waiting for borrowed leases to drain, invoke:

```csharp
await services.GetRequiredService<ShellLocationServices>()
    .InvalidateAsync(new LocationRef(location.Directory, location.WorkspaceId));
```

That stops the Location's commands and releases waits held by server-owned Session shell work. Host shutdown stops Session completion tasks before disposing ShellLocationServices, which then kills remaining owned processes. Do not create a second permission map or Session runner.

Tools owner can obtain this same `ShellRuntime` from `ShellLocationServices.ForLocation(info)` when later wiring a model tool. Use CreateToolAsync with the real policy/context; do not call the authenticated-user lane as an approval shortcut. Do not advertise source background-job notifications until the separate real Job/PluginRuntime integration exists.

The model ShellTool now accepts `runtime: () => shellLocations.ForLocation(location)` and optional `jobs: IShellToolJobs`. Its legacy process/environment arguments are compilation compatibility only and no longer execute an independent process path. Main owner must supply the runtime getter in ToolLocationFactory. Details and the required canonical Job adapter are in `Core/Shell/TOOL-JOBS.md`.

## Seven-day orphan output maintenance

`ShellHostOptions.CleanupOwnedOutput` defaults **false**. Enable it only when OutputDirectory refers to this host's private .NET shell tree (for example `shell-dotnet/<projectID>`). `GetDefaultDataDirectory()` is shared with the TypeScript installation; do not sweep its mixed `shell/<projectID>` files while another runtime can own captures there.

When enabled, the host queues cleanup on startup, when a private output directory is registered, and hourly. Public `CleanupOutputAsync(ct)` uses the same implementation. Known directory paths remain eligible after Location invalidation so their now-orphaned files can expire. Direct Core hosts can call `ShellOutputRetention.CleanupAsync(explicitOwnedDirectories, protectedFiles, ct)` with their own complete ownership snapshot.

The sweep:

- Applies the source seven-day **mtime** cutoff and exact `^sh_[0-9a-f]{12}.*\.out$` basename pattern.
- Enumerates only explicitly known private directories, one level only. It does not scan an arbitrary data root, recurse, or delete directories.
- Skips directory/file reparse points and symbolic links, nonmatching files, and all registered captures across the host's loaded Locations, including exited results still observable by jobs/readers.
- Serializes with Location removal/shutdown so active-file protection is not lost before owned processes stop. Newly created captures have current mtime and cannot qualify as old orphans between snapshots.
- Ignores inaccessible/disappeared files as source retention does. Cancellation/shutdown stops the sweep.

This is intentionally orphan-only: registered retained results are protected, and private project directories never registered with this host are not discovered/swept automatically. This narrower ownership boundary prevents deletion of another channel's or untracked legacy command service's captures. No cleanup was executed during verification.

## Canonical HTTP routes

Mapped exactly from `protocol/src/groups/shell.ts`, under the existing host authentication middleware:

| Method | Path | Result |
| --- | --- | --- |
| GET | `/api/shell` | Location.response array of running ShellInfo |
| POST | `/api/shell` | Location.response real created ShellInfo |
| GET | `/api/shell/{id}` | Location.response retained/live ShellInfo |
| PATCH | `/api/shell/{id}/timeout` | `{ timeout }` replaces deadline; Location.response ShellInfo |
| GET | `/api/shell/{id}/output` | Optional byte cursor/limit; Location.response ShellOutput |
| DELETE | `/api/shell/{id}` | Terminate/remove command and capture; 204 |

These accept `location[directory]` and optional `location[workspace]`; explicit non-local placement is not implemented. Output additionally accepts cursor/limit. Unknown IDs return canonical `ShellNotFoundError` with ID/message, not empty output or successful deletion. Missing captures are unavailable rather than represented as empty files.

The same mount adds the exact source Session route:

`POST /api/session/{sessionID}/shell` with `{ command, id?: EventId }`, returning 204 after server-owned completion. It derives Location from the real Session and does not accept a caller-supplied Location override. Unknown Sessions use SessionNotFoundError. Request cancellation only cancels that HTTP wait; the owned operation records completion through the injected lifecycle adapter. The original execute Location and current durable publication Location remain separate.

There is no invented `/shell/interrupt`, `/shell/wait`, PTY, or persistent-shell-daemon endpoint.

## Client API

`Client/SessionHttpClient.Shell.cs` adds network-only methods using existing Schema/Protocol types:

- `ListShellsAsync`, `CreateShellAsync`, `GetShellAsync`
- `SetShellTimeoutAsync`, `ReadShellOutputAsync`, `RemoveShellAsync`
- `RunSessionShellAsync(SessionId, string command, EventId? id = null, CancellationToken ct = default)`

Location methods accept directory/workspace arguments. RunSessionShellAsync does not: Session placement is authoritative. No Core or Server dependency, process execution, polling, retry, or second connection discovery is added to Client. ShellNotFoundError remains available in SessionApiException's original status/payload even though the shared query-error union does not yet include a typed Shell-specific variant.

## Verification and limits

Only pinned local `.dotnet/dotnet.exe` Server/Client builds with `C:\tmp\opencode\mcp-finish-pass` artifacts are used. No tests, shell/process execution, DB access, API/native verification, or network operation was run. No shared project files or commits were changed.

Final full Server and Client builds both succeeded with **0 warnings and 0 errors**, using `--no-restore`. This verifies the complete referenced .NET build graph, not live process or lifecycle behavior.

See Core/Shell/HANDOFF.md and TOOL-JOBS.md for timeout range, output page limits, missing capture behavior, source mappings, and the explicit background/recovery/scanner boundaries.
