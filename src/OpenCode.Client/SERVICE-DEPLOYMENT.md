# Managed Service Deployment

Persistent service launch never executes an application assembly or apphost from
mutable build output. `ServiceProcess` first copies the complete runtime directory
into a content-addressed channel cache:

```text
<XDG_CACHE_HOME or ~/.cache>/opencode/service-dotnet/deployments/<SHA-256>/
```

The digest covers relative file names, file contents, and normalized Unix modes.
All runtime assets are retained, including DLLs, dependency manifests, runtime
configuration, appsettings, native `runtimes` trees, and executable apphosts.
Framework-dependent deployments still require the installed .NET shared runtime.
The application now requires the exact side-by-side .NET 11 preview toolchain
described in `docs/dotnet-11.md`; run.ps1 supplies child-only runtime/host paths.
Files are copied, not hard-linked to build output.

The source and copied manifests must agree before an atomic directory rename
publishes the snapshot. A source change during copying fails deployment instead
of launching a partial snapshot. Existing snapshots are verified and reused,
never overwritten. Files are read-only and Unix executable permissions are
preserved. Only an attempt's unpublished staging directory is cleaned up;
published snapshots have no automatic garbage collection because a daemon may
still be using them.

## Packaging Contract

The default launcher prefers `server/OpenCode.Server.dll` beside the CLI, falling
back to an adjacent `OpenCode.Server.dll` for existing complete packages. The
chosen directory must contain the complete multi-file Server build/publish
output, including `OpenCode.Server.deps.json`,
`OpenCode.Server.runtimeconfig.json`, and runtime dependencies. A project reference
that copies only the Server assembly is not sufficient. CLI packaging should
stage the complete Server output into its `server` directory without launching it.

`ServiceStartOptions.Command` and `OPENCODE_DOTNET_SERVICE_COMMAND` accept either
`dotnet <built-server.dll>` or a built multi-file apphost, followed by service
arguments. They do not accept `dotnet run --project`, build commands, runtime
configuration overrides through `dotnet exec`, or single-file bundles.
The daemon uses native `StartDetached`, an empty inherited-handle list, null
standard handles, and a tracked SafeProcessHandle. It does not use ShellExecute
or a shell/setsid wrapper, and it never enables KillOnParentExit.
Application entry paths and explicit relative service-config paths are resolved
in the parent before the child's working directory changes to the snapshot.

Inherited `OPENCODE_CONFIG_DIR`, `OPENCODE_DATA_DIR`, `OPENCODE_CONFIG`, and XDG
config/data/state/cache roots must be absolute paths. Relative overrides fail
before staging with `ServiceFailure.InvalidConfiguration`, naming the setting.
They are not reinterpreted relative to the deployment directory or written back
to the caller's environment. Explicit relative `--service-config` and
`OPENCODE_DOTNET_SERVICE_CONFIG` paths still resolve against the caller's directory.

## Development Transition

A daemon already running from `Server/bin` continues to hold its original DLLs.
Deploying this change cannot release those existing Windows file locks, and
discovery reuses only a version-and-build-compatible healthy incumbent. Do not delete
registration files or kill a PID to force a transition.

The repo-root `run.ps1` builds into unique temporary artifacts, so it bypasses
those existing locks without stopping the old instance. Standard mutable-output
builds do not have that protection.

Managed Ensure can request one authenticated cooperative replacement of an idle
incompatible .NET instance after verifying the new package fingerprint. It never
kills a PID. If identity/activity cannot be verified, it reports the mismatch and
the explicit transition action rather than returning the stale server.

To move the incumbent itself onto a snapshot or unblock a standard build, the
user must explicitly stop the verified old instance once through its authenticated
.NET shutdown interface if it supports the current identity/stop protocol. If it
does not, use the old instance's supported shutdown mechanism after verification.
Then start through the updated managed-service launcher. A foreground `serve`
invocation that bypasses `ServiceDaemon` is not a snapshot launch.

Once the daemon runs from a snapshot, later CLI builds can replace the original
Server build assets normally. CLI project references can remain ordinary
incremental builds; no forced rebuild, skipped build, or automatic daemon
termination is needed to work around snapshot file locks.
