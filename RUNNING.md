# Running dotnet-opencode

Public package/repository identity: **dotnet-opencode**. Installed command:
**`dotnet opencode`**, for both global and local .NET tool installations. Internal
assembly/project names and the dotnet data channel are intentionally unchanged.

## Pack and inspect a local feed

Install the exact SDK from global.json in the checkout's `.dotnet` directory,
or explicitly select its installation through `OPENCODE_DOTNET_SDK_ROOT`:
`11.0.100-preview.7.26381.103`. Packaging never falls back to PATH. CI uses that
explicit SDK-root override after the workflow's pinned setup-dotnet step.
The framework-dependent tool requires **Microsoft.NETCore.App** and
**Microsoft.AspNetCore.App 11.0.0-preview.7.26381.103**. The SDK includes these;
a .NET runtime-only installation without ASP.NET Core is not sufficient. The CLI
runtime configuration disables roll-forward rather than silently selecting a
different runtime. No shared framework is bundled into the NuGet package.

```powershell
./packaging/pack.ps1 -Version 0.0.0-local -ArtifactsPath C:\tmp\opencode\local-tool
```

The stable output is
`C:\tmp\opencode\local-tool\packages\dotnet-opencode.0.0.0-local.nupkg`.
Each invocation creates fresh build/publish staging under the supplied absolute
external artifact directory. Existing packages are not overwritten and no build
or user data is deleted. Source native/grammar files, full Server deployment,
dependency metadata/licenses, and asset hashes are included by MSBuild. Packaging
does not depend on run.ps1's development-time Server copy or a user's Bun cache.
The script disables OpenAPI app execution, then validates the nupkg statically.

The default project PackageVersion is `0.0.0-local`; `-Version` overrides it without
changing native service/assembly identities. The CI workflow provides
timestamped versions such as `0.1.0-ci.20260905120000.12345.1` and handles trusted
publishing/OIDC separately. pack.ps1 does not log in, publish, push, install, or run
anything except the pinned SDK build/pack and static archive inspection.

## Optional installation from that feed

The following are **user installation instructions, not executed verification**.
Use the pinned host, and retain its runtime location for
tool shims and managed Server child launches:

```powershell
$sdk = (Resolve-Path ./.dotnet).Path
$dotnet = Join-Path $sdk 'dotnet.exe' # use 'dotnet' on Linux/macOS
$env:DOTNET_ROOT = $sdk
$env:DOTNET_HOST_PATH = $dotnet
$env:OPENCODE_DOTNET_HOST = $dotnet
$feed = 'C:\tmp\opencode\local-tool\packages'

# Local tool: run in the project where you want its tool manifest.
& $dotnet new tool-manifest # only if that project has no tool manifest yet
& $dotnet tool install dotnet-opencode --local --add-source $feed --version 0.0.0-local
& $dotnet opencode --help

# Alternatively, choose a global installation (not required for local tools).
& $dotnet tool install dotnet-opencode --global --add-source $feed --version 0.0.0-local
# With the pinned SDK and global tool directory on PATH:
dotnet opencode
dotnet opencode stats --all
dotnet opencode tui --standalone
```

The private repository is Hona/dotnet-opencode; its default-branch workflow publishes
public development prereleases to NuGet.org using trusted publishing. For published
versions, omit `--add-source $feed` and use `--prerelease` instead of
`--version 0.0.0-local`. No tool installation or invocation was used to
validate the package. See [packaging/README.md](packaging/README.md) for archive
layout, inspection and remaining license/runtime limitations.

## Bundled native and grammar limits

- OpenTUI **0.5.9 Windows x64** is bundled and hash-verified against its upstream
  artifact; all of that artifact's component notices are retained. Matching native
  libraries for other platforms are not invented or downloaded at runtime.
- Wasmtime/SQLite native runtime assets arrive through their pinned NuGet packages.
- Reviewed production grammars/queries and licenses are included with their hashes;
  the existing Clojure/Nix license-provenance exclusions remain in force.
- Persistent terminal daemon support remains conditional on the compatible
  opencode-pty backend; upstream has no bundled Windows daemon artifact. The tool
  does not claim otherwise or silently install a replacement backend.
- Build/pack/static inspection cannot establish native ABI, terminal behavior,
  provider/database compatibility, or successful tool installation.

## Local CLI development

Use PowerShell 7 or newer and the exact .NET 11 preview SDK pinned in `global.json`:

```powershell
./run.ps1
./run.ps1 -- tui
./run.ps1 status
```

The current pin is `11.0.100-preview.7.26381.103`. `run.ps1` uses the repo-local
`.dotnet/dotnet.exe` (or `.dotnet/dotnet` on Unix), not whichever system SDK is
first on PATH. `OPENCODE_DOTNET_SDK_ROOT` can select another dedicated installation
containing that exact SDK. Missing SDKs fail explicitly; there is no .NET 10
fallback. See [the SDK upgrade notes](docs/dotnet-11.md) for installation and API
verification details.

Arguments after the optional first `--` are passed to the CLI. To open another
project, invoke the script by its absolute path while your shell is in that
project. The script preserves the caller's working directory; it does not change
global environment variables. Arguments are passed through .NET's argument list,
including empty strings and embedded quotes, rather than PowerShell's legacy
native-command quoting.
The build child runs from the repository root so `global.json` is authoritative;
the CLI child still runs in the caller's project directory. Only child processes
receive `DOTNET_ROOT`, the architecture-specific root, `DOTNET_HOST_PATH`,
`OPENCODE_DOTNET_HOST`, and the local SDK PATH prefix.

Each invocation fingerprints the source inputs, builds only `OpenCode.Cli.csproj` and its project dependencies
into a new temporary `--artifacts-path`. It does not build a solution or test
projects. It uses a fixed `run` artifact pivot, packages the complete Server
runtime into the CLI's `server` directory, and launches the exact CLI apphost.
A failed build never launches an older executable. The script returns the build
or CLI exit code and attempts to remove only its own artifacts after the child
exits. If interrupted while the tracked build or CLI child still runs, it leaves
that invocation's artifacts intact. Cleanup failure is reported without
terminating any process.
The source fingerprint is checked before and after compilation, and the CLI and
Server output stamps must match it before packaging. A source edit during the
build fails the invocation instead of launching mixed or stale assets.

## DLL Locks

`run.ps1` avoids normal project `bin` and `obj` directories, so an older daemon
holding DLLs in `OpenCode.Server/bin` does not prevent this isolated CLI build.
It does not need to stop or restart that daemon. Standard
`dotnet run --project src/OpenCode.Cli` still uses mutable outputs and can encounter
those existing locks; use `run.ps1` for this development workflow.

Updated managed-service launches copy the complete runtime assets to an immutable
content-addressed deployment under:

```text
<XDG_CACHE_HOME or ~/.cache>/opencode/service-dotnet/deployments/<SHA-256>/
```

The daemon runs there, not in either normal build output or the script's temporary
artifacts. See [managed service deployment](src/OpenCode.Client/SERVICE-DEPLOYMENT.md)
for packaging and supported command formats. Foreground `serve` remains a child
of the script and keeps its temporary artifacts until it exits.

## Existing Daemons

Discovery requires both the semantic service version and the compiled application
build fingerprint. A constant service version no longer permits stale-build
reuse. Unchanged source/build inputs keep the same fingerprint across unique
artifacts directories; no timestamp or output path is used as the build identity.

Managed Ensure can perform one cooperative replacement after authenticating the
exact .NET channel/instance, verifying its active-session map is empty, and
validating the replacement package stamp. It uses the instance-bound shutdown
endpoint and waits for registration/lease release, never a PID kill. Explicit
servers are never replaced. Unknown/busy/unavailable activity or identity returns
an actionable mismatch instead of stale success. Set ReplaceIncompatible=false
to require manual transition in all cases.

To transition the daemon itself or unblock a standard mutable-output build,
explicitly stop the verified .NET instance once through authenticated .NET
shutdown, if it supports the current identity/stop protocol. Older instances may
require their own supported shutdown mechanism after identity verification.
Do not delete registration or terminate an arbitrary PID to force discovery.

The script does not run tests or directly restart a daemon. Its launched CLI may
request the controlled managed replacement described above. No runtime restart
was performed while implementing or verifying these build changes.
