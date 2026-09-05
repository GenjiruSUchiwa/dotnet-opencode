# Core foundations modernization pass

## Handoff status

**Enabled diagnostics are clean in the assigned foundation subset.** Source edits
are frozen for parent integration. This is one of eight disjoint ownership areas,
not overall analyzer or runtime-parity sign-off.

Owned paths under `src/OpenCode.Core`:

- Agent, CodeMode, Commands, Config, Filesystem, Instructions, Llm, Plugins,
  Snapshot and Vcs.
- Locations, except `CatalogLocation.cs`.

No edits were made to other owners' source, global analyzer policy, project/package
configuration, generated outputs, or pinned third-party assets in this pass.
No Git, staging, commit, push, installation, tests, or application execution was
performed. The parent owns integration and releases.

## Build evidence

The parent-supplied `provider-identity-build.log` contained **320 unique warnings**
in this subset, excluding CatalogLocation:

| Rule | Baseline count |
| --- | ---: |
| MA0004 | 227 |
| MA0009 | 50 |
| MA0099 | 17 |
| MA0084 | 9 |
| MA0015 | 7 |
| MA0023 | 7 |
| MA0042 | 2 |
| MA0040 | 1 |

The final Core dependency build succeeded with **10 warnings, 0 errors** and
**0 warnings/errors in the assigned subset**. Core was recompiled, not reported
clean from a skipped Core compilation. The remaining build warnings were in
Forms, Reference, Session, Skill, Tools and Schema, which have other owners.
An earlier build caught three transient Session compiler errors; those were
resolved by concurrent work, not edits from this owner.

SDK: repo-local `11.0.100-preview.7.26381.103`.

```powershell
& .\.dotnet\dotnet.exe build .\src\OpenCode.Core\OpenCode.Core.csproj `
  --no-restore -f net11.0 `
  --artifacts-path C:\tmp\opencode\foundations-f945-20260905 `
  -p:NuGetAudit=false -p:OpenApiGenerateDocuments=false -v:quiet `
  '-flp:logfile=C:\tmp\opencode\foundations-f945-20260905-final.log;verbosity=normal'
```

The initial build at the same artifact path performed restore. Retained evidence:

- `C:\tmp\opencode\foundations-inventory.txt`: deduplicated baseline.
- `C:\tmp\opencode\foundations-f945-20260905-build.log`: first build.
- `C:\tmp\opencode\foundations-f945-20260905-final.log`: successful final build.
- `C:\tmp\opencode\foundations-f945-20260905-console.txt`: final console output.

These totals are snapshots during parallel work. Only the parent's later full
CLI graph build can establish the integrated result.

## Confirmed source-parity fixes

### Markdown agent discovery and reads

Source: `packages/core/src/config/plugin/agent.ts:43-52,158-168` in
`C:\Repos\sst\kind-nebula`.

Upstream discards a failed directory discovery and omits individual unreadable
Markdown files. AgentDocuments previously let those I/O failures abort the entire
catalog load. It now completes discovery before reading, discards that directory's
discovery on I/O/access failure, and skips only the affected file on read failure.
Agent/mode ordering, ordinal file ordering, recursive-agent versus shallow-mode
discovery, symlink-cycle tracking and invalid-frontmatter omission remain intact.
Cancellation is not caught as an omitted file or successful empty catalog.

### Project instruction containment

Source: `packages/core/src/config/plugin/instruction.ts:32-36,68-75`.

Upstream contributes project instructions only when the Location is inside the
project root. LocalInstructions previously walked ancestors and appended the
specified project root even when the Location was outside it. It now checks
containment before constructing project candidates. Global instructions remain
independent; normal nearest-to-farthest ordering and source availability handling
are unchanged. No durable instruction event, epoch, blob or Session projection was
changed.

## Async and ownership review

- File discovery, configuration loading, catalog projection, OAuth HTTP/store
  operations, filesystem search, VCS and snapshot helpers have no dispatcher-owned
  continuation or caller callback chain. Their awaits now explicitly use
  ConfigureAwait(false). TimeProvider, cancellation tokens, gates and operation
  ordering remain the same.
- Command preparation/interpolation, native plugin initialization/hooks/change
  callbacks, CodeMode evaluator/tool/progress callbacks and permission Location
  factories/dispatchers retain ConfigureAwait(true). This names the old captured-
  context behavior rather than moving callbacks to a different continuation.
- LlmAnswerText/Guard preserve arbitrary client/iterator callback context. HTTP
  frame helpers retain their classifier callback context. Concrete transport loops
  and HTTP sending use false; no retry loop, model substitution or extra send was
  introduced.
- Async enumerator and stream lifetimes are explicit. Nested disposal order and
  exception propagation are retained, including snapshot temporary stdin and
  LLM decoder-before-stream disposal.
- CodeMode progress waits explicitly use CancellationToken.None. Final/error
  progress must settle after work cancellation, before its semaphore is disposed.
  Host control failures still escape guest normalization. The Jint owner thread,
  completed-task reads and manual promise pump are unchanged.

## Regex and value review

Reviewed regexes use NonBacktracking without new timeouts. Patterns retain
anchors, flags, greedy behavior and character classes; unused groups become
noncapturing, and observed groups receive names in their existing numeric order.
No site consumes repeated capture history. Coverage includes Markdown headers,
color/model validators, command interpolation, config substitution, CodeMode
catalog/signatures, instruction names, provider model/version selection, VCS
parsing and snapshot validation.

ProviderFailure's boolean-only evidence patterns explicitly retain the old .NET
ECMAScript ASCII digit/space classes before selecting NonBacktracking. Independent
overflow alternatives are evaluated separately to avoid a large combined
automaton; the observed result is still any-match. The bounded server-error regex
retains ECMAScript word boundaries (exact exception below). Classification
precedence, status/codes and retry-facing failure types are unchanged.

FileAttributes.None replaces only zero masks; native values are unchanged.
JintCodeModeSyntax's outer `body` local was renamed `programBody`, resolving nine
shadowing diagnostics without changing rendered guest source.

ProviderUserAgent.Apply was inspected and left unchanged. All existing calls to
it remain, including provider request overlays and both OAuth paths. The explicit
parent policy from `622ae4c` requires leading `dotnet-opencode` and preserves
configured metadata after it; source parity does not override that policy.

## Exact exceptions added

| Rule | Scope | Reason |
| --- | --- | --- |
| MA0015 | AgentCatalog.ReadAsync absolute-directory check | Preserve public catalog exception text |
| MA0015 | CodeModeTool.Bindings.Search compound validation | Message becomes the guest InvalidToolInput diagnostic |
| MA0015 | FileSearchIndex.FindAsync entry-type check | Preserve public filesystem query error text |
| MA0015 | LocalToolLocation constructor, two validations | Preserve compound Location/path and marker errors |
| MA0015 | PermissionLocationMap.Canonical absolute-directory check | Preserve public Location validation text |
| MA0015 | ProviderResolver.ParseSelection string-format check | Preserve model-selection exception text/type |
| MA0042 | NativePluginRegistration.DisposeAsync calling Dispose | Shared idempotent synchronous registration removal; the async facade must not call itself |
| MA0042 | NativePluginHost.DisposeAsync tool registration removal | Preserve synchronous registry change callback order during close |
| MA0009 | ProviderFailure.ServerError field only | Fixed/bounded alternatives scan linearly; retain ECMAScript word boundaries that the linear engine cannot express |

No MA0004, package-wide NoWarn, blanket regex timeout, or new global suppression
was added. Existing policy is unchanged.

## Modified source inventory

Paths below are relative to `src/OpenCode.Core` (36 files):

- Agent: AgentCatalog.cs, AgentDocuments.cs.
- CodeMode: CodeModeCatalog.cs, CodeModeSignature.cs, CodeModeTool.cs,
  JintCodeModeSyntax.cs.
- Commands: BuiltinCommands.cs, CommandCatalog.cs, CommandDocuments.cs,
  CommandRuntime.cs, CommandTemplate.cs.
- Config: ConfigLoader.cs.
- Filesystem: FileSearchIndex.cs, LocalFileSystem.cs, RipgrepFileScanner.cs.
- Instructions: InstructionCatalog.cs, LocalInstructions.cs,
  McpInstructionSource.cs, ProducerConfiguration.cs.
- Llm: AnthropicLlmClient.cs, AnthropicRequestLowering.cs,
  ConsoleIntegrationService.cs, LlmClient.cs, ModelsDevCatalog.cs,
  OpenAiOAuthService.cs, OpenAiResponsesLlmClient.cs, ProviderCatalog.cs,
  ProviderFailure.cs, ProviderResolver.cs.
- Locations: LocalToolLocation.cs, PermissionLocationMap.cs,
  StorePermissionLocationFactory.cs.
- Plugins: NativePluginHost.cs.
- Snapshot: SnapshotService.cs.
- Vcs: GitPatch.cs, LocalVcs.cs.

Documentation: this file and `docs/analyzer-migration.md`.

## Unverified boundaries

Verification was source inspection and compilation only. No evaluator programs,
regex samples, provider calls, OAuth, codec/serializer probes, Git child processes,
snapshot restore, filesystem service, native/WASM, DB/SQL/migration, DI, SDK or
CLI execution occurred. The build cannot prove scheduling, cancellation, regex
engine equivalence, callback affinity, process cleanup or behavioral parity.

Existing explicitly unsupported routes remain explicit failures, not successful
stubs: external JS plugin loading, unsupported provider transports, Unix special-
file classification, linked-directory instruction discovery and explicit workspace
placement. Existing CodeMode conformance limits remain in its README. No language
frontend experiment, storage rewrite or public API signature change was introduced.
