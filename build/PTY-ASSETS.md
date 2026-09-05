# Pinned persistent PTY assets

## Origin and inspected bytes

Source OpenCode pins `@opencode-ai/pty@0.1.13` in `packages/core/package.json`,
`packages/cli/package.json`, and `bun.lock`. Its native packages come from
https://github.com/anomalyco/opencode-pty at commit
`42ef5b055f972196ea61aaea7a5e4a87405b29f0` / tag `v0.1.13`.

On 2026-09-04, the six exact npm archives were inspected without executing their
contents. Their SHA-512 integrity values match the source lockfile and registry
metadata. Each contains exactly `package/bin/opencode-pty`, `package/package.json`,
and `package/LICENSE`. Binary, metadata, and license SHA-256 values are recorded
in `opencode-pty.assets.json`; the same pins are embedded into Core for offline
runtime validation. No npm JavaScript launcher is extracted or used.

| .NET RID | Upstream package suffix | Binary SHA-256 |
| --- | --- | --- |
| linux-x64 | linux-x64-gnu | b8caf78cbed9610aa5b744264aade552ed57cac70b2ebc27a788a0b1d2e02caa |
| linux-arm64 | linux-arm64-gnu | 25e9273e84016d34812a06e37974d6ca8b6cd9359c4e8dd647e5dc5b412b599a |
| linux-musl-x64 | linux-x64-musl | 2ce93573af4052cfcefd322dea9a43d98fbaea08e37f424bb09c3b85a612a3da |
| linux-musl-arm64 | linux-arm64-musl | f9b069d3c7f934d8add09eed186cdd148acfbee88f6cc1a65447a4fda173f711 |
| osx-x64 | darwin-x64 | 449beaea11f4c90c66ecf2bd1cb9610bd9079ba4cf257f55ebb092c3da5eefc6 |
| osx-arm64 | darwin-arm64 | d333339292bb9f9a739dbce9e2ababbce81b3040ea3d064b8a9b359a1c05ab61 |

**No Windows package exists.** The pinned upstream README explicitly excludes
Windows releases until its persistent daemon has a named-pipe transport. A .NET
named-pipe client compiling is not evidence of a compatible Windows daemon.
Ordinary Windows ConPTY terminals remain a distinct supported implementation.

The component is Rust (`Cargo.toml`, Rust 1.90), using `portable-pty` and
`libghostty-vt`; it is not OpenTUI and does not replace the TUI renderer library.
The shipped package license is MIT, copyright 2026 James Long; its exact license
file is always copied beside the binary. `libghostty-vt@0.2.1` registry metadata
declares MIT OR Apache-2.0. This inspection is not an exhaustive transitive-license
audit. The npm provenance metadata identifies the upstream publish workflow and
commit; no claim is made that a Sigstore trust-chain verification was performed.
Upstream describes its platform releases as unsigned and Linux GNU as requiring
glibc 2.30 or newer.

## Explicit build / publish

Ordinary builds and application startup do **not** download native assets. Opt in:

```powershell
.\.dotnet\dotnet.exe build src/OpenCode.Server/OpenCode.Server.csproj `
  --artifacts-path C:/tmp/opencode/my-build `
  -p:OpenCodePackagePersistentPty=true `
  -p:OpenCodePtyRuntimeIdentifier=linux-x64
```

Use the same properties with `dotnet publish` or a CLI build. RuntimeIdentifier
selects the asset unless `OpenCodePtyRuntimeIdentifier` explicitly selects one;
otherwise the SDK host RID is used. Unsupported targets, including Windows, emit
a clear build message and no pretend asset.

`OpenCodePtyArchive=/absolute/path/archive.tgz` uses an already downloaded archive
offline. It must still match the pinned SHA-512 value. Without it, the explicitly
enabled packaging target fetches only the pinned tarball URL. The .NET 11 build
task is compiled and loaded by MSBuild; it never invokes Bun, Node, the official
launcher, Rust/Cargo, or the extracted executable.

Only the three exact regular-file members are accepted; links and unknown members
are rejected. Archive integrity is checked before extraction, then all three
content hashes and package identity are checked. Output is under the invocation's
intermediate artifact tree and copied to `native/opencode-pty/<rid>/` in build and
publish output. On Unix build hosts the binary is marked executable. Cross-host
distributors must preserve/set the executable mode in their distribution archive;
runtime validation fails rather than modifying an immutable deployment or silently
launching a script. No global install or user runtime cache is modified by packaging.

For the existing root launcher, explicitly set `OPENCODE_DOTNET_PACKAGE_PTY=1`.
Optional `OPENCODE_DOTNET_PTY_TARGET` and `OPENCODE_DOTNET_PTY_ARCHIVE` select the
same build properties. Build identity includes packaging selection and pin inputs.
The existing CLI-to-Server asset copy and immutable daemon snapshot preserve the
native package; no separate manual PATH setting is needed for a matching Linux
package with executable permissions. `run.ps1` was not executed during this work.

## Runtime resolution and capability

The resolver uses an explicit native override first, then hash-verified packaged
assets, then a native PATH candidate. Existing `OPENCODE_DOTNET_PTY_BIN` and
`OPENCODE_PTY_BIN` overrides remain explicit user choices. Scripts/npm launchers
are not native assets. Protocol and instance identity are still checked by the
daemon transport when an operation is explicitly requested.

The native extension `/api/experimental/persistent-pty/capabilities` and Client
`PersistentPtyCapabilitiesAsync` distinguish published platform availability,
packaged assets, overrides, automatic-launch implementation, and whether a launch
can be attempted. None claims that native execution has been tested. macOS assets
are packageable, but this .NET transport's automatic-launch guard currently supports
Windows/Linux only; macOS auto-launch remains an explicit implementation limit.

This does not make default Windows startup self-contained for persistent PTYs.
Windows requires a future actual upstream-compatible native implementation, not
a new shim or substituted emulator. Linux packaging is now reproducible without
manual PATH setup, but deployment still needs authorized native/runtime verification
and release-level notice/signing review. No native binary was built or executed.

Source mappings: `packages/cli/script/opencode-pty.ts`, `packages/cli/src/node/target.ts`,
`packages/core/src/persistent-pty/binary.bun.ts`, `binary.node.ts`, and the upstream
native README/Cargo.toml/LICENSE at the pinned commit.
