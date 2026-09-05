# Modernization completion order

Prioritize the selected cross-cutting migrations while source-based parity review
and implementation proceed in non-overlapping areas. Completed checkpoints are
committed and pushed to GitHub `main`; every default-branch push runs the timestamped
NuGet release workflow.

| Work | Current status | Remaining verification boundary |
| --- | --- | --- |
| Tool packaging and NuGet Trusted Publishing | Repeated automatic releases succeeded; public versions are indexed; committed-snapshot package inspection passed | Retain working publication on subsequent pushes; installation/runtime behavior is unverified |
| EF Core 11 + SQLite | Full source review and owned cleanup complete; integrated committed snapshot built without diagnostics | Runtime model/query/database parity is outside authorized verification |
| Meziantou analyzers | Selected cleanup wave complete: committed full CLI build/pack emitted no warning or error diagnostics | Preserve documented source-scoped exceptions and keep new work clean |
| Vogen | All 25 scalar wrappers and CLI/non-CLI consumers migrated and included in the clean snapshot build | Existing ID-compatibility holds remain explicit; no serialized-runtime claim |
| System.CommandLine | Implemented CLI uses one typed command tree and builds cleanly | Documented parser/Server adapter differences and runtime invocation remain unverified |
| System.IO.Pipelines | Transport inventory and final CLI image collector migrated; included in the clean snapshot | Runtime framing/ownership behavior remains unverified |
| TimeProvider | Application-controlled clocks, delays and deadlines migrated; included in the clean snapshot | Retain documented ID-generation and external-library exceptions; runtime timing remains unverified |

## Independent committed-snapshot checkpoint

On September 5, 2026, commit **`a051b00c7c745eeac4138bcd1bb622dd020c3cbc`** was
exported with `git archive` to an isolated source directory. Its own packaging
script restored, built, packed, and statically inspected the complete CLI dependency
graph using the pinned SDK. Active workers' uncommitted files were not part of it.

- Build/pack succeeded with **zero warning or error diagnostics**.
- Package: `dotnet-opencode.0.1.0-local.modernization.nupkg` (verification-only, not published).
- Verified 352 runtime payload hashes, 58 Server runtime files, 65 grammar assets,
  and 26 dependency provenance records.
- SHA-256: `b5d44816c26b960c0972da5edf5a2e9501f9ebe0c68f8e122113ee83fffb1135`.
- Evidence: `C:/tmp/opencode/modernization-checkpoint-5a163642e6f64e6081e7ba3247f26862/pack.log`.
- No application, native, database, serializer or test execution was used.

This supersedes the earlier 1,979-warning integration baseline. The selected
modernization wave is complete at the source/build/package boundary. New parity
work continues; this is not a claim that the whole OpenCode port is complete.

The embedded SDK's manual hosting lifecycle remains a separate modernization
candidate. No Generic Host migration has been started. The persistent managed
server already owns standard ASP.NET hosting in its independent process.

## Verification boundary

- Use repository-pinned SDK `11.0.100-preview.7.26381.103` and isolated external
  build/pack outputs. Keep `OpenApiGenerateDocuments=false`.
- Source inspection, dependency restore, builds, static package checks, GitHub
  workflow status and public NuGet metadata are allowed.
- Do not add, edit or run tests. Do not execute the application, CLI, SDK host, DI
  container, EF model, SQL, migrations, native/WASM code, providers, MCP or PTYs for
  verification. Do not read production databases or credentials.
- Successful builds and publication do not prove runtime or behavioral parity.
- The EF owner also cleans up diagnostics in persistence. Other workers own only
  non-overlapping source areas; do not let workers rewrite the same files concurrently.
- Cross-package contract changes require explicit coordination before caller edits.
- Do not declare the modernization wave complete until the selected migrations have
  a completed integrated source/build checkpoint and remaining exceptions are explicit.

## Active work allocation

| Slot | Exclusive area | Priority |
| --- | --- | --- |
| 1 | Persistence, Forms/Reference, and owned SDK composition; ServerHost excluded | Usage-event publication and SDK WebSearch integration |
| 2 | Core foundations and stateless generation | Port the global Generate service and confirmed config/provider gaps |
| 3 | Core tools, integrations, MCP, shell, PTY and jobs; persistence files excluded | Lifecycle, permission, discovery and tool gap fixes |
| 4 | Session execution runtime and SDK adapters; persistence and owned host excluded | Compaction, instruction/history and execution gap fixes |
| 5 | Server and network Client, including ServerHost | Exporter metadata, stateless generation endpoints and transport gaps |
| 6 | CLI and Razor application; EF-owned Auth and generated/assets excluded | Source parity review and implementation |
| 7 | Generic OpenTui, Pipelines and TimeProvider support libraries | Native/transport ownership and algorithm gap fixes |
| 8 | Schema and Protocol source; generated assets and channel identity excluded | Wire-contract, codec and scalar gap fixes |

The integration owner handles packaging, GitHub/NuGet release status, cross-owner
handoffs, and commits/pushes. Workers stop at coherent source/build checkpoints so
their completed changes can be published without staging another worker's WIP.

## Detailed records

- [Publishing setup and evidence](../packaging/TRUSTED-PUBLISHING.md)
- [EF migration](ef-core-migration.md)
- [Analyzer migration](analyzer-migration.md)
- [Vogen migration](vogen-migration.md)
- [Command-line migration](../src/OpenCode.Cli/CommandLine/README.md)
- [Pipelines migration](pipelines-migration.md)
- [TimeProvider migration](time-provider-migration.md)
