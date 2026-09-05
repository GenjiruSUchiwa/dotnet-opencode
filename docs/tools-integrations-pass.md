# Tools and integrations pass — frozen Core handoff

## Outcome

The complete owned area is analyzer-clean in the final Core build: **0 owned warnings and 0 errors**. The isolated baseline contained **651 unique diagnostics in 62 eligible source files**. Another 26 diagnostics were in the three EF-reserved files and were not this pass's work. Concurrent owners also reduced warnings elsewhere; this report does not attribute their changes to this pass.

The pass also fixes confirmed MCP output-schema/error-text parity and legacy subagent Session ID compatibility. It adds the missing native WebSearch tool composition and request-bound eligibility refresh over the network owner's existing shared runtime. **One Server option must be supplied by the parent/network owner**; the exact patch is below. No Server file was edited here.

Owned source is frozen for parent integration. Parent owns all Git, CI, push, and release operations.

## Analyzer baseline and fixes

| Rule | Unique owned baseline diagnostics |
| --- | ---: |
| MA0004 — explicit await context | 546 |
| MA0015 — argument identity | 41 |
| MA0099 — explicit enum values | 19 |
| MA0042 — async operations | 16 |
| MA0040 — explicit cancellation | 9 |
| MA0009 — regex complexity | 7 |
| MA0011 — invariant formatting/parsing | 5 |
| MA0061 — interface defaults | 4 |
| MA0023 — explicit captures | 3 |
| MA0084 — shadowed local | 1 |
| **Total** | **651** |

### Context and cancellation review

This is not a blanket `ConfigureAwait(false)` conversion. The initial fixes use **430 explicit true** and **116 explicit false** await sites, with source-only syntax edits and a caller/context review:

- Tool orchestration (`ToolSnapshot`, tool leaves, permission policies, Location creation) retains the incoming context across arbitrary codecs, hooks, executors, progress, formatting, and instruction callbacks.
- Integration flows and MCP credential adapters retain context around host authorization and post-commit publication callbacks. `CredentialMutation.Notifications` is still published only after the actual store commit, with the original non-cancellable publication token. No fabricated updated field or notification shortcut was introduced.
- Job/Shell/PTY coordination retains context around run delegates, lifecycle publication, attachments, and host callbacks. Existing in-flight ownership and completion order are unchanged.
- Pure file/transport helpers opt out: persistent daemon framing, Windows pipe I/O/teardown, HTML parsing, HTTP body collection, skill source reads, Wellknown HTTP/config decoding, and private read helpers. Pure helpers inside a callback-owning class were handled separately from the orchestration methods that invoke callbacks after awaiting them.
- `await using` declarations keep their original resource scope and disposal order. Where needed, the resource variable and configured async-disposal lifetime are separate, so a configured-disposable wrapper does not replace the usable stream/lease.
- Explicit `CancellationToken.None` preserves existing uninterruptible cleanup/settlement waits: counted Job dependency removal, terminal ownership updates, shutdown, and MCP registry flush. Request/shutdown tokens were not substituted into post-commit cleanup.
- Shell timeout cancellation stays synchronous at the private timeout transition boundary. `CancelTimeout` is shared by replacement, removal, exit, and shutdown; it does not await callbacks while holding the lifecycle gate. Other owned cancellation outside those transitions uses `CancelAsync`.

These choices preserve the existing caller-context behavior; they do not claim that arbitrary SDK receive-loop callbacks acquire a UI synchronization context. Such callback implementations still own their dispatch contract.

### Other diagnostic fixes

- Completed Task results are awaited, including the guarded OAuth token-cache read; no incomplete task is synchronously waited as a replacement.
- Async stream/registration disposal is used where supported. `ToolRegistry.Close` is shared by synchronous and asynchronous disposal without recursively calling `DisposeAsync` or changing its worker-drain semantics.
- Internal MCP token-cache implementations now match `ITokenCache`'s required cancellation parameters instead of adding different defaults.
- Argument exceptions name the actual input or captured host producer. Validation order, exception classes, permission failure handling, and unsupported-feature guards remain intact.
- Diff hunk numbers, read/grep continuation output, and persistent PTY numeric IDs use invariant formatting/parsing.
- Regex patterns use non-backtracking matching where compatible and named/explicit captures where consumed. This is not a new shell/HTML lexer or a grammar replacement. YAML frontmatter recovery, wellknown variable names, MCP field titles, and Exa source fields retain their original interpretation.
- Zero-valued enum masks remain zero; explicit casts preserve inclusion of hidden resources and existing file/link/execute-bit checks.
- The Windows PTY construction local no longer shadows the owned pseudoconsole field.

**One precise suppression was added:** `WebSearch/WebSearchContracts.cs`, only the cross-property throw in parameterless `WebSearchSelection.Validate`, suppresses MA0015. It preserves the existing `ArgumentException` contract where no method argument exists. No `.editorconfig`, `NoWarn`, global/project analyzer setting, or generated asset was changed. Existing SDK experimental-API suppressions remain unchanged.

## Confirmed source-backed parity changes

### MCP tools without a declared output schema

`packages/core/src/tool/mcp.ts:54` always supplies an output schema, using `{}` if the MCP server omits one. The native adapter previously passed null, but still returned machine output. The strict `ToolSnapshot` correctly rejects output without a schema, so such real MCP tools could not complete.

`McpRuntime` now supplies the source unconstrained JSON schema only for that omitted MCP output schema. Declared server schemas remain unchanged. No global codec/registry validation was relaxed, and no empty tool result was fabricated. MCP `isError` text is trimmed before reporting, matching the source fallback/error content behavior.

SDK XML documentation for `ModelContextProtocol.Core` 2.2.0 confirms Image/Audio `Data` and resource `Blob` contain base64 UTF-8 bytes. Existing conversion was therefore retained; it was not incorrectly changed to base64-encode those bytes again.

### Subagent legacy Session identities

`packages/schema/src/session-id.ts` accepts strings beginning with `ses`, while new identities are generated with `ses_`. The native `SessionId.FromExisting` already preserves that distinction. `SubagentTool` incorrectly tightened both its input and output schemas to `^ses_`.

Both tool schemas now match `^ses`. New ID generation, child execution, admission, permission checks, and Job ownership are unchanged. No second subagent runner was added.

### Reviewed and intentionally retained

- Source `shell.ts:226–245` explicitly maps a removed command to a killed result and unavailable capture. That existing native removal fallback was retained rather than misclassified as a fabricated successful exit.
- `job.ts` retains counted blocking, committed background markers before exposing detached ownership, and persisted explicit cancellation before completion. Native non-cancellable settlement and the shared host-wide `JobRuntime` remain intact.
- Permission deny/correction/decline behavior, source tool context, persistent grant-before-settlement behavior, and same-Session rejection fan-out remain intact.
- No parser capability was inferred by executing a shell. Existing managed scanner limits and fail-closed dynamic executable/directory rules remain in `Tools/ShellParsing/README.md`; no grammar files were changed by this pass.
- Formatting still rejects unimplemented npm-backed readiness rather than claiming a formatter ran. Remote skill discovery, arbitrary JS plugin execution, and platform limitations were not replaced with fake successful results.

## WebSearch integration follow-up

Read-only inspection of the network owner's `Server/Services/WebSearchHostService.cs` confirmed real plugin-scoped providers, `WebSearchRuntime`, credential/selection stores, and Integration contributions already exist. Core does **not** create any of these again.

### Implemented Core counterpart

`LocalToolOptions.WebSearchReady` is an optional `Func<CancellationToken, Task<WebSearchRuntime>>`. When supplied, `ToolLocationFactory` installs `opencode.tool.websearch` in the existing built-in order. Its `WebSearchToolBinding`:

1. Installs one native `scope.TransformTools` contribution, initially empty.
2. Waits until provider plugin initialization has finished before borrowing the host runtime.
3. Uses the factory's **actual PermissionService, shared Location FormService, and injected clock** to construct the real `WebSearchTool`.
4. Rechecks config/credential/selection eligibility on factory acquisition and immediately before `ToolLocationLease.SnapshotAsync` captures a model request. This is request-bound refresh, not a timer, credential poll, or second event subscription. Credential/config changes therefore affect the next request, including an already-held lease's next snapshot.
5. Replays the **same** native tool transform only when eligibility, runtime identity, or the year-bearing description changes. It does not append a new transform or remove a later plugin override. A cancelled flush keeps its dirty bit so a later capture must finish the replay.
6. Removes only its own built-in contribution for known unavailable backends. No placeholder executor, fake availability, or successful empty search is registered.
7. Cancels/settles refresh through the native plugin lifetime; disposal never disposes the borrowed runtime/providers/transports. Existing request snapshots retain their captured executors, as before.

The network host registers all four supported providers. Because native keyless access is intentionally disabled, `random` now draws from **credentialed registered providers**, not a pool that could randomly hide/fail an otherwise usable tool. With none available it reports unavailable. Explicit provider requests still fail for that provider rather than silently switching. No search is performed during eligibility evaluation.

The parent's branding is preserved exactly: `NativeWebSearchProvider` parses the supplied custom User-Agent, then calls `ProviderUserAgent.Apply(request)`. The leading `dotnet-opencode` identity and custom suffix remain; this pass does not restore the upstream-only User-Agent.

### Exact Server patch for parent/network owner

**Not applied by this pass.** In `src/OpenCode.Server/ServerHost.cs`, at the end of `LocalToolOptionsFor`, change:

```diff
             Plugins: OpenCode.Server.Plugins.NativePluginComposition.Definitions(services, location),
-            PluginChanged: id => OpenCode.Server.Plugins.NativePluginComposition.Publish(services, location, id));
+            PluginChanged: id => OpenCode.Server.Plugins.NativePluginComposition.Publish(services, location, id),
+            WebSearchReady: ct => services.GetRequiredService<WebSearchPluginSource>().ReadyAsync(location, ct));
```

`WebSearchPluginSource.ReadyAsync` is internal to Server and can be called here. Existing Server imports expose the type. `AddNativeWebSearch` and route mounts were already integrated by the parent; do not add another registration or map the routes again.

**Do not pass `IWebSearchLocationSource.AcquireAsync` here.** Its implementation acquires `CommandHostService`, which acquires this same `ToolLocationFactory`; that would recursively acquire the Location under construction. The direct ready callback reads/configures the already initialized plugin entry and returns that exact runtime.

Until the option is wired, the default-null Core option leaves the WebSearch model tool unregistered. This report does not claim that an unmodified Server host advertises it. No Schema/Protocol, credential persistence, or EF follow-up is needed for this Core binding.

### Remaining limits

- Eligibility refresh is guaranteed at the supported factory/model snapshot boundaries, not for code that retains `Registry` and bypasses `ToolLocationLease.SnapshotAsync` indefinitely.
- Source configured/discovered JS/TS plugin readiness guards remain; this does not implement an external plugin loader.
- Keyless backend fallback remains deliberately unavailable. Real empty decoded result arrays still use the source no-results response; missing credentials/errors do not.
- Backend readiness checks mean usable local credential composition, not a network health probe. No live backend, OAuth, MCP, PTY, permission, or model invocation was verified.

## Exact source files changed

Paths are relative to `src/OpenCode.Core/`:

```text
Formatting/LocalFormatter.cs
Integrations/IntegrationCommands.cs
Integrations/IntegrationProviders.cs
Integrations/IntegrationRuntime.cs
Integrations/Wellknown/WellknownConfigSources.cs
Integrations/Wellknown/WellknownService.cs
Integrations/Wellknown/WellknownTransport.cs
Jobs/JobRuntime.cs
Mcp/McpElicitation.cs
Mcp/McpOAuthCredentials.cs
Mcp/McpOAuthService.cs
Mcp/McpRuntime.cs
Mcp/McpRuntime.OAuth.cs
Mcp/McpRuntime.Operations.cs
Permissions/PermissionRules.cs
Permissions/PermissionService.cs
Permissions/StorePermissionRules.cs
Pty/PersistentPtyAssets.cs
Pty/PersistentPtyDaemon.cs
Pty/PersistentPtyService.cs
Pty/PtyLocationMap.cs
Pty/PtyService.cs
Pty/WindowsPty.cs
Shell/Jobs/ShellToolJobs.cs
Shell/ShellOutputRetention.cs
Shell/ShellResult.cs
Shell/ShellRuntime.Background.cs
Shell/ShellRuntime.cs
Shell/ShellToolExecution.cs
Skill/SkillSources.cs
Tools/Builtins/EditTool.cs
Tools/Builtins/GlobTool.cs
Tools/Builtins/GrepTool.cs
Tools/Builtins/QuestionTool.cs
Tools/Builtins/ReadTool.cs
Tools/Builtins/ShellTool.cs
Tools/Builtins/SkillTool.cs
Tools/Builtins/SubagentTool.cs
Tools/Builtins/WebFetchTool.cs
Tools/Builtins/WebSearchTool.cs
Tools/Builtins/WriteTool.cs
Tools/HtmlMarkdown.cs
Tools/HttpBody.cs
Tools/JsonToolCodec.cs
Tools/LocalFileMutation.cs
Tools/LocalLiteralShellPolicy.cs
Tools/LocalShellPolicy.cs
Tools/LocalToolPath.cs
Tools/OwnedToolProcess.cs
Tools/ReadInstructionDiscovery.cs
Tools/RipgrepProcess.cs
Tools/ShellProcessSource.cs
Tools/ToolLocationFactory.cs
Tools/ToolPolicy.cs
Tools/ToolRegistry.cs
Tools/ToolSnapshot.cs
Tools/ToolTextDiff.cs
Tools/WebFetchTransport.cs
Tools/WebSearchToolBinding.cs (new)
WebSearch/NativeWebSearchProvider.cs
WebSearch/WebSearchBackendResponses.cs
WebSearch/WebSearchContracts.cs
WebSearch/WebSearchRuntime.cs
```

Report: `docs/tools-integrations-pass.md` (new).

Untouched reservations: `Permissions/SqlitePermissionGrantStore.cs`, `Integrations/Wellknown/WellknownSourceStore.cs`, `WebSearch/WebSearchSelectionStore.cs`, all other direct EF/Persistence/SQL/query/mapping files, and the separate Filesystem/CodeMode/Commands/Plugins/Vcs/Snapshot/Locations work. No central analyzer document, project/global configuration, or third-party/generated assets were edited.

## Build-only verification

Pinned executable `.dotnet/dotnet.exe`, SDK `11.0.100-preview.7.26381.103`. Artifacts are isolated under `C:/tmp/opencode/tools-pass-f945b861/`. Every build used `-p:OpenApiGenerateDocuments=false`.

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Core\OpenCode.Core.csproj --artifacts-path C:\tmp\opencode\tools-pass-f945b861 --no-restore -p:OpenApiGenerateDocuments=false -v:minimal
.\.dotnet\dotnet.exe build src\OpenCode.Server\OpenCode.Server.csproj --artifacts-path C:\tmp\opencode\tools-pass-f945b861 --no-restore -p:OpenApiGenerateDocuments=false -v:minimal
```

| Final build log | Errors | Owned warnings | Other warnings emitted |
| --- | ---: | ---: | ---: |
| `build-websearch-core.log` | 0 | 0 | 3 |
| `build-websearch-server.log` | 0 | 0 | 0 |

The Core build's remaining diagnostics are outside ownership:

- `OpenCode.Schema/Config/ConfigDuration.cs:26:6`: MA0009, MA0023.
- `OpenCode.Schema/WorktreeJson.cs:14:20`: MA0009.

The Server build was incremental and reused that compiled Core/Schema output; its zero emitted warnings is not a separate clean rebuild claim for the whole graph. Earlier `build-final-core.log` also saw `Forms/FormService.cs:68:22` MA0004 and `Reference/ReferenceSources.cs:23:46` MA0009; those were cleared by other owners before the final integration build.

Supporting source-only diagnostic manifests and await review are in the same artifact directory. No tests were added, edited, or run. No app/CLI/SDK/DI/EF model/DB/SQL/migration/native/WASM/codec/clipboard/provider/MCP/PTY/process runtime verification, production data/credential reads, Git operation, publication, global install, or delegation was performed. Source inspection and compilation do not establish runtime interoperability.
