# dotnet-opencode

Independent .NET 11 port of OpenCode V2. Installed command: **`dotnet opencode`**.

This is a **development prerelease tool**, default local version `0.0.0-local`.
The private `Hona/dotnet-opencode` repository publishes public NuGet prereleases.
No tool was installed or executed during packaging verification.
CI supplies a timestamped version, for example `0.1.0-ci.20260905120000.12345.1`,
and publishes using NuGet trusted publishing/OIDC, not a stored API key.
The first release workflow successfully authenticated, uploaded the package, and
created a GitHub prerelease. See [publishing evidence](TRUSTED-PUBLISHING.md#first-successful-publication).

Requires the repository-pinned **.NET 11 Preview 7 SDK/runtime**
(`11.0.100-preview.7.26381.103`) and its matching **Microsoft.AspNetCore.App**
shared framework. It is framework-dependent, not self-contained. Use the pinned
dotnet host; no system .NET 10 fallback is supported.

The matching shared-framework version is `11.0.0-preview.7.26381.103` for both
Microsoft.NETCore.App and Microsoft.AspNetCore.App. The SDK version above is not
the runtime version. CLI roll-forward is disabled.

The tool includes its managed dependencies, a complete `server/` deployment,
Windows x64 OpenTUI 0.5.9, and reviewed Tree-sitter grammar/query assets with
licenses and hashes. Other OpenTUI platform binaries are not bundled. Native
persistent PTY backend availability is a separate documented prerequisite.

Internal assemblies remain `OpenCode.Cli`/`OpenCode.Server`; the persistent dotnet
channel continues to use `opencode-dotnet.db`, `service-dotnet.json`, existing
`OPENCODE_DOTNET_*` variables and the original internal application identity.
Package branding does not migrate user data or replace another channel's service.

Help: `dotnet opencode --help`. Examples: `dotnet opencode stats --all`,
`dotnet opencode api GET /api/health`, `dotnet opencode tui --standalone`.
These examples were not executed during packaging verification.

See the source checkout's README.md/RUNNING.md for local-feed build/install
instructions and current limitations. Package `THIRD-PARTY-NOTICES.md`,
`third-party/`, grammar manifests and `tool-runtime-assets.json` retain dependency
license/provenance evidence. Build/pack/static archive checks do not establish
runtime, native ABI, provider or database interoperability.

## Stable packaging interface

From PowerShell 7+, with the pinned SDK already installed at repo-local `.dotnet`
or explicitly selected by `OPENCODE_DOTNET_SDK_ROOT` (the CI setup-dotnet handoff):

```powershell
./packaging/pack.ps1 -Version 0.0.0-local -ArtifactsPath C:\tmp\opencode\local-tool
```

Output: `<ArtifactsPath>/packages/dotnet-opencode.<Version>.nupkg`. The script uses
a fresh isolated build directory per invocation, restores/builds/packs only the CLI
dependency graph with `OpenApiGenerateDocuments=false`, then statically validates
the archive. It fails rather than overwrite an existing package of that version.
Version accepts SemVer without build metadata; this keeps the NuGet filename
identical to the requested version. The CI timestamp prerelease format is accepted.

`packaging/inspect.ps1 -PackagePath <nupkg> -Version <version>` checks ToolSettings,
package identity, runtime framework requirements, both dependency closures, build
identity and every runtime-asset hash without extracting or loading application code.
Neither script installs, invokes, signs into NuGet, pushes a package, or deletes
user/build state. The GitHub workflow owns authentication and publication.

The normal release path uses source-vendored OpenTUI bytes/component notices and
grammar assets. It does not look in a user's Bun cache or ambient native DLL path.
The optional explicit OpenCodeToolOpenTuiSourceDirectory build property is a
maintainer-only source-notice import, not a release dependency or runtime lookup.

Dependency notice directories use short stable ordinal names under
`third-party/nuget/`; the runtime manifest maps each directory back to the complete
package ID/version and original source filename. Nuspec bytes are retained as
`metadata.xml`, because NuGet excludes nested `.nuspec` files. This also avoids
long tool-store paths for timestamped package versions.

## Packaging evidence

The exact script interface succeeded with local `0.0.0-local` and CI-shaped
version overrides, including the explicit SDK-root override used by the workflow.
The final validation-only artifact was
`dotnet-opencode.0.1.0-ci.20260905000000.1.2.nupkg` (not published):

- Build and pack exited successfully; static inspection passed.
- ToolSettings: Name `dotnet-opencode`, Runner `dotnet`, EntryPoint `OpenCode.Cli.dll`.
- 352 runtime payload hashes verified, including complete CLI/Server dependency closures.
- 58 staged Server runtime files; CLI and Server source-build IDs match.
- 65 production grammar/query assets and their license hashes verified.
- 26 NuGet dependency provenance records and pinned Windows x64 OpenTUI included.
- Size 113,217,853 bytes; SHA-256
  `b05e556ed80ff551df5e61dd5f793bbd502c5f0174ec4a1fe54dc62c85d49e53`.
- No NuGet packaging warnings or build errors in the final pass. Existing
  analyzer warnings in the frozen Core/Server/SDK graph remain; they were not
  suppressed or changed by this work.

Evidence artifacts are outside the repository under
`C:/tmp/opencode/dotnet-tool-final-validation`; do not commit build directories or
NuGet archives. The native notices/provenance under `packaging/third-party/` are
intentional source assets and must accompany the existing pinned native/grammar
files for a clean CI checkout. No install, tool/app execution, listener, DI startup,
database access, tests, Git publication or NuGet publication was performed here.
