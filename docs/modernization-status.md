# Modernization completion order

Prioritize the selected cross-cutting migrations while source-based parity review
and implementation proceed in non-overlapping areas. Completed checkpoints are
committed and pushed to GitHub `main`; every default-branch push runs the timestamped
NuGet release workflow.

| Work | Current status | Completion evidence still needed |
| --- | --- | --- |
| Tool packaging and NuGet Trusted Publishing | Two automatic releases succeeded; the first version is listed in NuGet's public download index | Retain working publication on subsequent pushes |
| EF Core 11 + SQLite | Whole persistence conversion committed; final review and owned analyzer cleanup resumed | Review mappings, query translation from provider source, transactions, errors, precision and callers; complete a source/build checkpoint |
| Meziantou analyzers | Enabled across owned source; cleanup split between exclusive owners | Resolve diagnostics without blanket suppression, then re-inventory and build the full dependency graph |
| Vogen | All 25 scalar wrappers and CLI/non-CLI consumers migrated | Include unchanged factories, codecs and enabled analyzers in the final integrated build |
| System.CommandLine | Implemented CLI uses one typed command tree | Include the graph in the final build; retain documented parser and Server adapter boundaries |
| System.IO.Pipelines | Transport inventory and final CLI image-stream collector migrated; full CLI graph built with no errors | Included in final analyzer sign-off; byte limit, overflow read, exception and stream ownership retained |
| TimeProvider | Application-controlled clocks, delays and deadlines migrated | Include composition in the final build; retain documented ID-generation and external-library exceptions |

The September 5 integration build including the final image collector completed
with **0 errors and 1,979 analyzer warnings** (none in CLI). This is not EF review
or analyzer sign-off. Its isolated build log is under the directory recorded in
`C:/tmp/opencode/modernization-integration-location.txt`.

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
| 1 | EF persistence, its callers and host composition | Finish source-contract review and owned diagnostics |
| 2 | Core foundations: providers, Code Mode, configuration, plugins, VCS and snapshots | Analyzer cleanup, then confirmed source-gap fixes |
| 3 | Core tools, integrations, MCP, shell, PTY and jobs; persistence files excluded | Analyzer cleanup and lifecycle/permission gap fixes |
| 4 | Session execution runtime and SDK adapters; persistence and owned host excluded | Analyzer cleanup and execution/history gap fixes |
| 5 | Server endpoints and network Client; ServerHost excluded | Analyzer cleanup and actual route/transport gaps |
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
