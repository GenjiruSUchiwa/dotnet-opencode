# Modernization completion order

Finish the selected cross-cutting migrations before starting another broad
multi-agent parity pass. Completed checkpoints are committed and pushed to GitHub
`main`; every default-branch push runs the timestamped NuGet release workflow.

| Work | Current status | Completion evidence still needed |
| --- | --- | --- |
| Tool packaging and NuGet Trusted Publishing | Two automatic releases succeeded; the first version is listed in NuGet's public download index | Retain working publication on subsequent pushes |
| EF Core 11 + SQLite | Whole persistence conversion committed; final source review resumed | Review mappings, query translation from provider source, transactions, errors, precision and callers; build and hand off to analyzer cleanup |
| Meziantou analyzers | Enabled across owned source; cleanup incomplete | Re-inventory after EF handoff, resolve diagnostics without blanket suppression, then build the full dependency graph |
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
- After the EF owner finishes, transfer persistence ownership to analyzer cleanup;
  do not let independent workers rewrite the same files concurrently.
- Return to the broad parity pass only after these selected migrations have a
  completed source/build checkpoint and all remaining exceptions are explicit.

## Detailed records

- [Publishing setup and evidence](../packaging/TRUSTED-PUBLISHING.md)
- [EF migration](ef-core-migration.md)
- [Analyzer migration](analyzer-migration.md)
- [Vogen migration](vogen-migration.md)
- [Command-line migration](../src/OpenCode.Cli/CommandLine/README.md)
- [Pipelines migration](pipelines-migration.md)
- [TimeProvider migration](time-provider-migration.md)
