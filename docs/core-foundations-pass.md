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

## Pass 2 — configuration, providers and cache lineage

**Frozen for parent review.** Pass 1 was integrated by the parent as `3194600`.
This second pass changes only the same owned Core paths and these two records.
The final Core dependency build reports **0 warnings and 0 errors**, including
**0 owned diagnostics**. This is not a CLI/full-repository sign-off.

### Implemented source gaps

1. **Virtual configuration retains its own provenance.**
   `ProducerConfiguration` now parses `OPENCODE_CONFIG_CONTENT` through the same
   substitution/normalization path as other runtime documents. Previously it
   copied keys out of the effective merged configuration, promoting earlier
   provider/policy values into a new highest-priority document. Ordered producers
   now receive the actual virtual document. Source: `core/src/config.ts:252-281`.
   Discovery order, environment variable names and existing validation behavior
   were not otherwise rewritten.
2. **Configured model capabilities replace rather than recursively merge.**
   `ConfigLoader` distinguishes local configuration transforms from remote catalog
   overlays. Local capabilities contain exactly tools/input/output, dropping
   earlier extension flags as `ConfigProviderPlugin` does. Settings/body retain
   recursive object overlays, headers retain case-insensitive merging, limits and
   variants retain their existing composition. Legacy capability migration now
   supplies the canonical `ModelCapabilities.CreateDefault()` fields before
   applying tool_call/modalities. Sources: `core/src/config/plugin/provider.ts:66-72`
   and `core/src/v1/config/migrate.ts:316-324`.
3. **New configured models start with canonical model defaults.**
   The final provider catalog initializes only missing model records through
   `ModelInfo.CreateDefault`, then applies configuration. Custom models no longer
   fail canonical catalog projection solely because they lack models-dev metadata.
   This uses the real source defaults (200,000 context / 32,000 output and default
   capabilities), not guessed provider limits or a substitute model. Existing
   model records, selection ordering, disabled checks and package support checks
   remain unchanged. Sources: `core/src/catalog.ts:118` and
   `schema/src/model.ts:126-139`.
4. **Failure evidence follows JavaScript character semantics.**
   `ProviderFailure` now matches the source's JavaScript whitespace set, including
   NBSP and BOM, excluding U+0085. Dot spans do not cross CR/LF/U+2028/U+2029.
   String-valued parsed error data is decoded exactly once, like providerCodes;
   already-decoded raw body/message strings are not decoded a second time.
   Classification precedence, original error message/body/HTTP context, and
   unknown-error fallback remain unchanged. This supersedes pass 1's intentionally
   retained .NET ECMAScript ASCII whitespace behavior after direct source review.
   Source: `ai/src/provider-error.ts:16-80,102-168`.
5. **Plugin disposers remove their own registration.**
   NativePluginScope now gives each hook/transform contribution reference identity.
   With registrations A, B, A, disposing the final A previously removed the first
   A and changed execution order. It now leaves A, B. Snapshot iteration, callback
   context, idempotent disposal and reverse-order resource cleanup are unchanged.
   Source: `core/src/plugin/hooks.ts:69-98` (entry identity, not callback equality).

### Cache handoff resolved in the provider layer

The Session owner's `PromptCacheKey` rejection finding is fixed. Source evidence:

- `core/src/session/model-request.ts:209-210,323-339`: one generic lineage key.
- `ai/src/cache-policy.ts:25-44,53-151`: automatic inline policy is independent
  of that key; protocols that ignore inline hints bypass policy placement.
- `ai/src/protocols/anthropic-messages.ts:1003-1057`: no wire lineage-key field;
  inline breakpoints and explicit provider cache_control are separate.
- `ai/src/protocols/gemini.ts:463-500`: cachedContent comes only from provider
  options, not promptCacheKey.
- `ai/src/protocols/shared.ts:28-37`, openai-chat.ts:712-721 and
  open-responses.ts:694-708: cap a nonempty wire key at 64 Unicode code points.

Implemented behavior:

| Route | Generic PromptCacheKey | Other cache behavior |
| --- | --- | --- |
| Anthropic Messages | Accepted; no wire field, metadata/user_id or cache resource is invented | Source default automatic placement: last tool, first/last distinct system parts, final message's last text (otherwise last content); existing manual hints reserve the four-slot budget first |
| Google/Gemini | Accepted; no wire field is invented | Inline annotations are ignored as in the source; explicit provider cachedContent is preserved and is never derived from lineage |
| OpenAI Chat/Responses | Nonempty keys are sent as prompt_cache_key, capped at 64 code points | Surrogate pairs are not split; unmatched UTF-16 units are preserved; null/empty keys remain omitted |

`LlmCachePolicy.AnthropicDefault` operates on copied immutable request records,
not caller-owned messages. The existing Anthropic wire lowering still enforces
the four-marker limit and TTL buckets. Default placement runs regardless of
whether a lineage key exists, and therefore also applies to title/compaction
calls. The port has no request-level `cache: "none"`/custom policy surface yet;
this pass implements the source's undefined/default policy, not a new policy API.
Existing explicit hints remain available. No cache-create request, new model
backend, extra HTTP attempt, Session event, retry or admission change was added.

### Exact remaining Session-owner patch

No Session files were edited. The provider boundary now accepts the source
lineage field for all implemented routes. To finish caller parity, the parent
should consolidate the existing SessionGeneration calculation into
`SessionRequestIdentity.PromptCacheKey(SessionInfo session)`:

```csharp
internal static string PromptCacheKey(SessionInfo session)
{
    var lineage = (session.Fork?.SessionId ?? session.Id).Value;
    return System.Text.RegularExpressions.Regex.IsMatch(lineage, "^ses_[0-9a-f]{64}$",
        System.Text.RegularExpressions.RegexOptions.NonBacktracking) ? lineage[4..] : lineage;
}
```

Set `PromptCacheKey = SessionRequestIdentity.PromptCacheKey(session)` in the
LlmRequest initializers in:

- `SessionExecutionEngine`: normal physical steps, including retries/rebuilds.
- `SessionTitleService.AttemptAsync`: title and distinct-primary fallback attempts.
- `SessionCompaction.RunAsync`: compaction attempts.
- `SessionGeneration`: replace its already-present inline calculation.

Source title uses `context.prepare` (`session/title.ts:66-74`); compaction uses
`plan.prepare` (`session/compaction.ts:284-289`), both sharing model-request's key.
Use the **immediate fork parent** when present, otherwise the Session itself.
Do not recursively chase root lineage: upstream explicitly retains that TODO.
Do not suffix the key with title/compaction/model IDs or map it to Google
cachedContent/Anthropic metadata. No Schema/EF/SDK/Server change is required for
this specific handoff.

### Pass 2 files and verification

Ten Core files changed/added:

- Config/ConfigLoader.cs.
- Instructions/ProducerConfiguration.cs.
- Llm/ProviderCatalog.cs, ProviderFailure.cs, LlmCachePolicy.cs (new),
  AnthropicRequestLowering.cs, LlmRequestLowering.cs,
  ResponsesRequestLowering.cs, LlmRequest.cs.
- Plugins/NativePluginHost.cs.

No new suppressions or global analyzer settings. Existing exact exceptions remain.
ProviderUserAgent.Apply, its callers, persistent channel names, TimeProvider,
Pipelines and Vogen are unchanged.

Artifacts: `C:\tmp\opencode\foundations-pass2-f945-20260905`.

- Initial dependency build, before the cache handoff: 0 warnings / 0 errors;
  `foundations-pass2-f945-20260905-build.log`.
- An intermediate dependency build was blocked by a concurrent duplicate
  Schema.SessionIdleEventData definition; Schema was not edited here.
- The isolated Core build against prior dependency outputs passed with 0/0;
  `foundations-pass2-f945-20260905-isolated.log`.
- **Final dependency build after all code edits passed with 0 warnings / 0 errors**;
  `C:\tmp\opencode\foundations-pass2-f945-20260905-verified.log`.

The final command used the pinned repo-local SDK, Core.csproj, `--no-restore`,
the artifact path above, `NuGetAudit=false` and `OpenApiGenerateDocuments=false`.
It did **not** disable project references. Only source inspection, restore and
builds ran. No tests, evaluator/regex/cache probes, provider calls, runtime/DI,
DB/SQL/migration/native/codec execution, Git or publication occurred.

Unverified behavior includes the new cache placement, Unicode regex matching,
configuration/catalog composition and repeated-registration disposal at runtime.
Unimplemented config watchers/well-known composition, extra plugin domains,
request cache-policy controls and other previously documented unsupported routes
were not replaced by successful stubs. Parent integration still owns the full CLI
build and any cross-owner follow-up.

## Pass 3 — stateless Generate service

**Frozen for parent integration.** The final Core dependency build completed with
**0 warnings and 0 errors**. No Schema, Protocol, Server, SDK, Session, database or
package configuration files were edited. The endpoint remains network-owner work.

### Source contract and integration API

Reviewed source:

- `core/src/generate.ts`: complete text service and error mapping.
- `protocol/src/groups/generate.ts`: prompt, optional Model.Ref, and
  `{ data: { text } }`. There is no structured-output mode, public usage/metadata,
  generation-option payload or streaming endpoint in this API.
- `server/src/handlers/generate.ts`: global config Location and plugin readiness.
- `core/src/model-resolver.ts`: explicit/default/package selection, credentials
  before variants, and explicitly enabled anonymous routes.
- `ai/src/route/client.ts:486-491`, `schema/events.ts:335-366,398-419,630-638`:
  one structured stream, authoritative fragments and terminal text projection.

The Core API does not duplicate the contract owner's wire records:

```csharp
var generate = new OpenCode.Core.Generate.GenerateService(providerResolver);
var text = await generate.TextAsync(prompt, model, cancellationToken);
```

`TextAsync(string prompt, ModelRef? model = null, CancellationToken ct = default)`
returns Task<string>. Map `GenerateModelSelectionException` to InvalidRequestError
with the same message. Map `GenerateUnavailableException` to ServiceUnavailableError
with the same message and optional `Service` property. The network owner should
wrap the returned string in the shared `{ data: { text } }` response; no separate
Server model loop or temporary Session is required.

The service captures `ConfigLoader.GetDefaultConfigDirectory()` as its base
Location and accepts no per-request directory. It borrows ProviderResolver and
its transport/store lifetimes. Construction does not load catalogs, read
credentials, connect providers or create projects. Execution rejects configured
or discovered plugin sources with service `model.catalog` until a real configured
plugin runtime can supply the catalog. Host-level readiness remains network-owned.

### Resolution and request semantics

- Resolution uses one catalog snapshot and the existing provider overlays,
  protocol selection, credential logic and transport constructors.
- Explicit input uses model.get, not available-model filtering. This intentionally
  retains source behavior for an explicitly selected disabled model; the existing
  Session resolver keeps its stricter enabled check.
- Omitted input uses the catalog default when it has a package, otherwise the first
  available model with a nonempty package. A declared but unsupported package is
  an error, not a reason to substitute another model.
- Omitted input does not inherit the configured default's variant; source passes
  only the explicit request's variant. Existing explicit `default` alias behavior
  is unchanged.
- Missing selections report `Model unavailable: provider/model` or
  `No model specified and no supported model is available` exactly as upstream.
- Active credentials resolve before variants/package initialization. Refresh
  failures report `Generation credentials are unavailable`, without a service
  label. The resolved credential snapshot is reused rather than refreshed twice.
- Variant/package/unresolved-variable errors map to model selection for explicit
  input and unavailable with provider ID for implicit input. Initialization
  exceptions retain their original causes. Route authentication and failed-stream
  errors map to unavailable with the selected provider ID. Cancellation propagates.
- Explicitly enabled providers with neither credentials nor configured auth can
  make an unauthenticated request, matching Auth.none. Only the stateless resolver
  enables internal transport flags for this mode; configured headers are retained.
  Direct constructors and Session resolution gain no anonymous fallback.

One LlmRequest contains the exact selected API model ID and the user prompt, and
one real StreamAsync call executes it. There is no Session instruction discovery,
tool registry, affinity/cache-lineage header, event, retry, usage ledger or prompt
admission. Provider overlays/defaults, Anthropic automatic caching and the mandated
`dotnet-opencode` User-Agent still apply.

### Response semantics

- Empty/whitespace prompts and empty output are valid; null prompts are rejected.
- Fragment order is first delta or authoritative end, not text-start. TextEnd
  replaces accumulated deltas, including retractions; later deltas append.
- Finish of any reason or a ProviderError event completes the source response.
  A ProviderError event is distinct from a thrown AI error. Length/tool-call
  outcomes are not rejected by the stricter legacy text-stream adapter.
- StepFinish alone is not terminal. EOF without terminal completion reports
  `The provider response ended unexpectedly.` as unavailable.
- The full stream is consumed before return. A later read/disposal failure or
  cancellation does not return partial text as success.
- Reasoning, tools, usage and provider metadata are not in Generate.text's public
  result. No tool executes or accounting event is emitted. Thrown LlmException
  details remain in the cause; the wire mapping exposes only message/service.

### Pass 3 files and verification

New files:

- `src/OpenCode.Core/Generate/GenerateService.cs`.
- `src/OpenCode.Core/Llm/ProviderResolver.Generation.cs`.

Updated source: `Llm/ProviderResolver.cs` (shared selection/credential helpers),
`Llm/LlmClient.cs`, `Llm/AnthropicLlmClient.cs`, and
`Llm/OpenAiResponsesLlmClient.cs` (internal explicit anonymous-auth mode).
Public client constructor signatures are unchanged. No new suppressions,
dependencies, generated types or global analyzer changes were added.

Artifacts: `C:\tmp\opencode\foundations-pass3-f945-20260905`.
Final log: `C:\tmp\opencode\foundations-pass3-f945-20260905-final.log`.

```powershell
& .\.dotnet\dotnet.exe build .\src\OpenCode.Core\OpenCode.Core.csproj `
  --no-restore -f net11.0 `
  --artifacts-path C:\tmp\opencode\foundations-pass3-f945-20260905 `
  -p:NuGetAudit=false -p:OpenApiGenerateDocuments=false -v:quiet
```

The initial build performed restore. Initial and final Core dependency builds
passed with **0 warnings / 0 errors** using the pinned preview SDK. This does not
certify the future endpoint/contract integration. No tests, runtime/DI/SDK,
provider/credential/database/evaluator/regex/codec/native probes, Git commands,
installs or publication occurred. Authentication, response collection, error
mapping, cancellation and endpoint wiring remain runtime-unverified.
