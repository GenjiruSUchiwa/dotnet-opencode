# .NET 11 Preview Upgrade

## Pinned Toolchain

- SDK: `11.0.100-preview.7.26381.103`.
- Runtime/reference packs: `11.0.0-preview.7.26381.103`.
- Official release metadata: <https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json>.
- Release date: August 11, 2026; latest official .NET 11 preview observed during this upgrade.
- Local install: repo-root `.dotnet`, excluded from version control.
- `global.json` uses the exact version, preview allowed, roll-forward disabled.

The system SDK was not replaced and no global PATH or runtime environment was
changed. Use the local executable explicitly for builds. On this Windows
workspace it is `C:/Repos/hona/opencode-dotnet/.dotnet/dotnet.exe`.

## Installation

Use Microsoft's official script, not an application startup action. From the
repository root on Windows x64:

```powershell
$installer = Join-Path ([IO.Path]::GetTempPath()) 'opencode-dotnet-install.ps1'
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer
$version = (Get-Content ./global.json -Raw | ConvertFrom-Json).sdk.version
& $installer -Version $version -Architecture x64 -InstallDir (Join-Path $PWD '.dotnet') -NoPath
```

For another supported platform/architecture use the matching official
`dotnet-install.sh`/PowerShell installer artifact and the same exact SDK version.
Do not overwrite a system installation or reuse a directory containing a running
daemon's deployment.

The installation performed for this upgrade used the official installer with
`-NoPath`, retained its download, and verified the archive against release
metadata. The installer selected the Windows x64 **tar.gz**, even though the
requested temporary archive filename ended in `.zip`; it is not the zip artifact.

Artifact:
`https://builds.dotnet.microsoft.com/dotnet/Sdk/11.0.100-preview.7.26381.103/dotnet-sdk-11.0.100-preview.7.26381.103-win-x64.tar.gz`

Verified SHA-512:
`54a942d8eab37c0f38b7bb08ca79100db2ce8e2163cfe1d5d236ff6a588e15e5c9cfc89de0361b520a54466719507173386db2ecf8c71a3f27f039bbdcf27f8c`

## Application Projects And Packages

All projects under `src` target `net11.0`. ASP.NET framework references retain
their normal unversioned `Microsoft.AspNetCore.App` identity; the pinned SDK
supplies matching Preview 7 reference/runtime packs.

Microsoft.Data.Sqlite, Microsoft.Extensions.DependencyInjection,
Microsoft.Extensions.FileSystemGlobbing, and Microsoft.Extensions.Logging use
the verified NuGet release `11.0.0-preview.7.26381.103`.
Microsoft.Extensions.AI.Abstractions stays at `10.9.0`: its independently
versioned package feed had no 11.x version at verification. YamlDotNet,
AngleSharp, and Markdig were not changed to unrelated latest versions.

Test projects were not edited, retargeted, restored as build targets, or run.
They still target their original framework and are not claimed compatible with
the upgraded application references. Build `src` projects, not the solution.

## Process API Verification

Reference: <https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/>.
The installed Preview 7 XML documentation omits some new APIs, so the actual
reference-assembly metadata was inspected without executing application code.
It contains:

- `SafeProcessHandle.Start(ProcessStartInfo)` returning a SafeProcessHandle.
- `SafeProcessHandle.ProcessId` returning Int32.
- `TryWaitForExit(TimeSpan, out ProcessExitStatus)` and `WaitForExit[Async]`.
- ProcessExitStatus `ExitCode`, `Canceled`, and nullable `Signal`.
- ProcessStartInfo `StartDetached`, `InheritedHandles`, and standard-handle properties.
- `File.OpenNullHandle()`.

Daemon startup uses `StartDetached = true`, `InheritedHandles = []`, explicit
null standard handles, and `SafeProcessHandle.Start`. No Windows ShellExecute,
Linux shell/setsid wrapper, or KillOnParentExit is used for the persistent daemon.
Its tracked handle is checked with nonblocking TryWaitForExit, including exit
signal/cancellation status, and disposed without killing the child. Startup
diagnostic nonces, protected reports and immutable deployment snapshots remain.

StartAndForget is intentionally not used for tracked contenders. Browser opening
remains the Catalog/Auth owner's ShellExecute exception: StartAndForget rejects
UseShellExecute and must not be forced into that scenario. Tool subprocess
migration and bounded capture remain owned by Tools and Catalog/Auth, not by
the daemon launcher.

## Developer Entry

`./run.ps1` verifies that the local SDK directory contains the pin, builds only
CLI/project dependencies into unique temporary artifacts, and launches the
matching apphost only after build success. The build cwd is the repo root for
SDK resolution; application cwd is the caller's project directory. Child-only
runtime/host variables ensure the app and its daemon find the side-by-side
runtime. The daemon resolves a bare dotnet host from those explicit variables or
its current shared runtime installation, not a random system .NET 10 host.
The child CLI home is also isolated under `.dotnet/.cli-home`; automatic ASP.NET
development-certificate generation and telemetry are disabled for these children.

No existing daemon is automatically replaced or killed. A previously elected
instance can remain on its old runtime until the user performs an explicit,
authenticated transition. This upgrade does not imply runtime, UI, provider,
tool, or recovery verification.

## Build Checkpoint

The full `src/OpenCode.Cli` dependency graph, including Core, Server, Client,
Protocol, Schema and native/Blazor libraries, compiled with the installed SDK at
`C:/tmp/opencode/dotnet11-cli-build-7a00e43998294ded8adc6eda327e05e0`.
There were zero errors and two Blazor `BL0012` warnings in the UI-owned
`OpenCodeApp.razor.cs` (`CloseDialog` and `RunCommand`). The normal preview-SDK
support-policy message was informational.

The generated CLI and Server runtimeconfig files both target `net11.0` and
reference Microsoft.NETCore.App and Microsoft.AspNetCore.App version
`11.0.0-preview.7.26381.103`. `run.ps1` passed parse-only validation. No application,
tool command, browser, provider, recovery sweep, or test executable was run.
Test source/project files were left unchanged. Startup recovery integration was
paused when this toolchain upgrade became the active priority; this build does
not imply that automatic recovery has been wired or exercised.
