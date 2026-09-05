# Implementation Guard Ledger

Source-only review, 2026-08-30. No application, provider, API, database, or test
execution was used to classify these sites. This is an inventory of observed
guards, unavailable responses, and placeholder successes, not a claim about
whole-repository completeness. Files are being edited concurrently: symbols are
the stable locators; line numbers are approximate anchors from this pass.

The sweep covered handwritten `src` C#, Razor, and build-source files for
`NotImplementedException`, `NotSupportedException`, `PlatformNotSupportedException`,
structured `Unsupported` failures, unavailable/capability guards, placeholder
comments, and constant empty/no-content successes. `bin`, `obj`, generated
serializer output, and the generated bootstrap snapshot are excluded. No direct
`NotImplementedException` site was found; that does not imply implementation.
Ordinary empty results and input rejections are accounted for separately below.

Do not remove a guard merely to make the UI appear ready. Replace it only when
the owning implementation and its dependency boundary exist. No credentials,
model identifiers, configuration contents, or diagnostic payloads are reproduced.

## Ownership And Notation

Local prefixes expand as follows: `Core/` = `src/OpenCode.Core/`, `Server/` =
`src/OpenCode.Server/`, `Client/` = `src/OpenCode.Client/`, `Schema/` =
`src/OpenCode.Schema/`, `Protocol/` = `src/OpenCode.Protocol/`, `Sdk/` =
`src/OpenCode.Sdk/`, `Cli/` = `src/OpenCode.Cli/`, `Blazor/` = `src/OpenTui.Blazor/`,
and `Native/` = `src/OpenTui.Native/`.

Upstream references are relative to `C:/Repos/sst/kind-nebula/packages`.
For example, `core/src/session/session.ts` is the actual production domain file.

| Owner | Current accountable role and handoff boundary |
| --- | --- |
| RUN | Current Core runner/coordinator owner. Owns attempts, continuation, wake, interruption, recovery and runner composition. |
| INS | Current native instructions owner. Owns discovery, producers, instruction epochs and guidance composition. |
| EVT | Current durable-event/storage owner. Owns admission, projectors, event ordering and domain mutations. |
| STORE | Current SessionStore/query owner; coordinate migration work with the bootstrap/daemon owner. |
| CFG | Current config/provider/catalog owner. Owns normalization, exact model selection and integration configuration. |
| AI | Current structured transport owner. Owns protocol lowering, stream events and provider-specific replay. |
| TOOL | Current tools/permission/Location owner. Owns authorized leaf composition, shell scanning, grants and file mutation. |
| SRV | Current server integration/bridge owner. Owns endpoint behavior and host-owned Core execution calls. |
| CLIENT | Current Client/daemon owner. Owns lifecycle, deployment, diagnostics and HTTP/SSE transport. |
| UI | Current native Blazor/CLI integration owner. Owns readiness presentation, network adapter and event reduction. |
| NATIVE | Current native OpenTUI/renderer owner. Owns ABI, terminal host and Blazor render integration. |
| SCH | Current Schema/serialization owner. Owns canonical wire/storage validation; not runtime implementations. |
| PARENT | Parent session is accountable until it assigns a dedicated SDK, PTY, plugin/MCP, migration or other feature owner. Rows name the required domain; they are not unowned work. |

Classifications: **T** = temporary port/integration blocker; **S** = legitimate
input, safety, lifecycle, or declared-scope rejection to keep; **M** = a partial
implementation whose guard is correct until the listed extension exists;
**F** = placeholder success that can misrepresent implemented behavior.

## Baseline Priority

1. **Startup and current build coherence:** CLIENT/SRV own persisted startup diagnostics and complete DI registration; UI owns adapter/step-snapshot integration. The latest isolated full CLI build passes after the dependency/adapter fixes. A historical exit code alone is not a diagnosed startup cause. The missing `SessionEnvironment` and `SessionMutations` registrations were corrected during this pass. Capture the next actual sanitized failure before claiming the screenshot is fixed.
2. **Configured local instructions:** INS/RUN must finish the producer branches used by real project/global configuration. Supported local text/epochs now exist; nonempty guidance/plugin/agent configurations can still be rejected. Do not reinstate instructionless execution as the normal UI path.
3. **Truthful selection/catalog APIs:** model/provider endpoints were replaced with real catalog calls during this pass; their metadata/Location guards remain explicit. CFG/SRV must finish the remaining agent/config/location placeholders. Do not substitute a model when selection fails.
4. **Tools and approvals:** TOOL/RUN/SRV/UI must compose real policies and approvals before enabling `Tools`. The registry is deliberately empty and the runner deliberately rejects calls; changing only capability flags is unsafe.
5. **Continuation and recovery:** RUN/EVT/STORE must implement retry, incomplete-stream continuation, control processing and surviving-claim recovery without erasing durable facts.

The server creation, normal text prompt/wake, instruction epoch integration,
environment replacement, and selected mutation/catalog paths changed from unavailable to implemented source
during this review. Their former guards are not listed as still-open blockers.
This observation is not runtime verification. Health readiness is not proof of
all session features or provider availability.

## Server And Catalog Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| S01 | T | `Server/Endpoints/SessionEndpoints.cs::MapSessionEndpoints`, GET `/active` (~83) | `server/src/handlers/session.ts`, `core/src/session/execution.ts` | SRV + RUN; expose a complete process-local active snapshot, not a guessed set from projected rows. |
| S02 | T | Same symbol, interrupt `continue=true` (~127) | `core/src/session/execution.ts`, `core/src/session/runner/index.ts` | RUN then SRV; preserve the steering/control continuation rule. Ordinary interrupt is already delegated to Core. |
| S03 | T | Same symbol, prompt attachment/metadata branch (~157-158) | `core/src/session/prompt.ts`, `core/src/session/session.ts` | INS + EVT + SRV; prepare URI files, agents, skills and metadata before durable admission; preserve first-admission-wins reconciliation. Canonical DTOs already exist. |
| S04 | T | Same symbol, DELETE and remaining action loop (~220-222): `background`, `move`, `fork` | `core/src/session/session.ts`, `core/src/session/projector.ts`, `core/src/session/revert.ts` | SRV + EVT; move/fork/background/delete need their own lifecycle/history dependencies. Rename/agent/model/view now delegate to `SessionMutations`; do not recreate them in HTTP handlers. |
| S05 | S | Same symbol, unknown interrupt query, invalid `continue`, prompt model/variant rejection (~121-126,155-156), NotSupported-to-503 filter (~61) | `protocol/src/groups/session.ts`, `server/src/handlers/session.ts` | SRV/Protocol; retain canonical validation and explicit unavailable responses. Prompt-level model override is not a missing field to add. |
| S06 | S/M | `Server/Services/SessionExecutionService.cs::Capabilities`, `RequireReady`, `RequireRecordingReady` (~26-73) | `core/src/session/execution.ts`, `server/src/process.ts` | SRV + RUN; keep lifecycle/event-bridge readiness. Any valid-input rejection must be traced to the concrete engine/producer dependency, not bypassed with `Resume` or forced true flags. |
| S07 | F | `Server/Endpoints/FeatureEndpoints.cs::MapFeatureEndpoints`: `/skill` (9-16), `/command` (19-26), `/plugin` (29-36), `/reference` (39-46) | `server/src/handlers/{skill,command,plugin,reference}.ts`; `core/src/{skill,command,plugin,reference}.ts` | SRV with INS/CFG; PARENT assigns plugin/command ownership. Replace unconditional empty arrays and synthetic location envelopes with real domain lists or explicit unsupported results. |
| S08 | F | `Server/Endpoints/PermissionEndpoints.cs::MapPermissionEndpoints`: request list (9-20), saved list (23-28) | `server/src/handlers/permission.ts`, `core/src/permission.ts`, `core/src/permission/saved.ts` | TOOL + SRV + UI; connect pending approvals, replies, cancellation and durable saved grants. Empty lists must reflect actual state, not stand in for an unwired service. |
| S09 | T/M | `Server/Endpoints/PtyEndpoints.cs`, `ServerHost` PTY composition | `server/src/handlers/pty.ts`, `core/src/pty.ts`, `core/src/shell/select.ts`, `core/src/persistent-pty.ts` | PTY + SRV; basic CRUD/ticket/WebSocket boundary and host registration implemented. Shared credential verifier, config-first Windows shell selection, narrow ticket bypass, CORS, and Location-close invalidation are wired. Creation explicitly refuses the absent plugin-supervisor flush; Unix and persistent PTYs remain unsupported. No runtime/native/WebSocket verification. See `Server/Pty/HOST-INTEGRATION.md`. |
| S10 | Rechecked | `Server/Endpoints/ModelEndpoints.cs::MapModelEndpoints`, `CatalogRequestLocation.ResponseAsync` | `server/src/handlers/model.ts`, `core/src/model.ts`, `core/src/model-resolver.ts` | CFG + SRV replaced the fixed catalog/default selections during this pass. Track remaining explicit metadata/Location limitations at C09; do not reimplement the removed placeholder. No model literals are reproduced here. |
| S11 | F/M | `Server/Endpoints/AgentEndpoints.cs::MapAgentEndpoints` (9-41) | `server/src/handlers/agent.ts`, `core/src/agent.ts`, `core/src/config/plugin/agent.ts` | CFG + INS + SRV; replace the fixed agent list and permissive fixed rule samples with resolved agents, descriptions and permissions. |
| S12 | Rechecked | `Server/Endpoints/ProviderEndpoints.cs::MapProviderEndpoints` | `server/src/handlers/provider.ts`, `core/src/provider.ts`, `core/src/model-resolver.ts` | CFG + SRV replaced credential-row-derived providers with the real catalog response during this pass. Remaining catalog guards are C09. |
| S13 | F/M | `Server/Endpoints/ConfigEndpoints.cs::MapConfigEndpoints` (9-22): one synthetic document entry around merged config | `server/src/handlers/config.ts`, `core/src/config.ts` | CFG + SRV; preserve actual config documents, order and requested Location rather than relabeling merged config as one global file. |
| S14 | F/M | `Server/Endpoints/LocationEndpoints.cs::MapLocationEndpoints`: `/location`, `/fs/list`, `/fs/find` (9-86) | `server/src/handlers/{location,fs}.ts`, `core/src/location.ts`, `core/src/filesystem.ts` | TOOL/Location + SRV; remove synthetic project identity and process-cwd placement. Wire target Location, canonical project identity and filesystem semantics. Nonexistent directories currently become empty successes in list/find. |
| S15 | T | `Server/Endpoints/HealthEndpoints.cs`, legacy migration 501 (~36) | `core/src/database/migration.ts`, `core/src/database/v1-migration.ts` | STORE + PARENT migration assignment; legacy import/upgrade runner, journal reconciliation and data transformations. Fresh bootstrap is separate and already source-derived. |
| S16 | T | `Server/Endpoints/SessionEndpoints.cs`, rename with empty title (~187) | `core/src/session/title.ts`, `server/src/handlers/session.ts` | SRV + RUN/CFG; source-derived automatic title generation. Nonempty rename already uses its durable mutation. |

## Runner And Context Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| R01 | T | `Core/Session/SessionExecutionEngine.cs::Capabilities` reports `Tools=false`; `RunAsync` submits an empty tool set | `core/src/session/runner/{index,step,llm}.ts`, `core/src/tool.ts` | RUN + TOOL; Location-scoped tool registry, permission loop, tool output and durable settlement before enabling tools. Supported text/instructions must remain distinct from full agent-loop readiness. |
| R02 | S | `SessionExecutionEngine.PromptAsync`, transient model override with admit-only (~54); `SessionRunCoordinator.RunAsync`, override while busy (~47) | `core/src/session/session.ts`, `core/src/session/run-coordinator.ts` | RUN; preserve selected-model ordering and durable admission. Do not save a transient override by guessing new event fields. |
| R03 | T | `SessionExecutionEngine.RunAsync`, pending compaction/move control (~153) | `core/src/session/runner/index.ts`, `core/src/session/compaction.ts`, `core/src/session/session.ts` | RUN + EVT; domain control execution and instruction epoch movement, not generic inbox promotion. |
| R04 | M | Same symbol, explicit workspace placement (~156) | `core/src/session/execution.ts`, `core/src/location-service-map.ts`, `core/src/workspace.ts` | RUN + TOOL/Location; preserve implicit-local baseline; add actual workspace routing before accepting explicit placement. |
| R05 | S/M | Same symbol, no model-visible text (~172) | `core/src/session/history.ts`, `core/src/session/runner/to-llm-message.ts` | RUN + INS; empty invalid input stays rejected; valid attachment/control-only histories depend on R03/R08. |
| R06 | S | `SessionExecutionEngine.RequireToolsDisabled` (~204) | `core/src/session/runner/model.ts`, `core/src/session/runner/step.ts` | RUN + TOOL; retain protection against provider body overrides bypassing the disabled tools policy. |
| R07 | T | `Core/Session/SessionAttempt.cs::RunAsync` (~55-56,102): unsupported finish/retry/continuation | `core/src/session/runner/step.ts`, `core/src/session/runner/retry.ts` | RUN + AI; implement logical-step accounting, pre-output retry, rejected-continuation rebuild, incomplete-stream continuation and overflow-compaction ownership. Keep one stream per physical attempt. |
| R08 | T | `Core/Session/SessionHistory.cs::Lower` (~22), `ToolContent` (~90) | `core/src/session/prompt.ts`, `core/src/session/runner/to-llm-message.ts` | INS + RUN + AI; prepared prompt attachment lowering and tool-file URI materialization. Do not discard media to make history load. |
| R09 | T | `SessionHistory.Lower`: incomplete assistant (~31), unsettled tool (~54) | `core/src/session/execution/restart.ts`, `core/src/session/runner/index.ts` | RUN + EVT; durable restart/settlement recovery, not marking unfinished messages complete. |
| R10 | S/M | `SessionHistory.Lower`: unknown assistant content (~50), default history branch (~79) | `core/src/session/history.ts`, `core/src/session/runner/to-llm-message.ts` | RUN + SCH; add valid compaction/location/shell lowering; unknown malformed variants remain errors. |
| R11 | S | `SessionAttempt.PublishAsync`, provider step index other than zero (~110) | `core/src/session/runner/llm.ts`, `ai/src/llm.ts` | RUN + AI; physical-attempt single-step invariant. Do not turn the adapter into an in-memory multi-step loop. |
| R12 | T | `SessionAttempt.PublishAsync` / `StartToolAsync`: disabled tool failures (~179,252), hosted result/error (~193) | `core/src/session/runner/step.ts`, `core/src/session/runner/publish-llm-event.ts` | RUN + TOOL + AI; actual authorized execution or hosted settlement and canonical result projection. |
| R13 | T/S | `SessionAttempt.PublishAsync`: opaque provider state (~206), default structured event (~207) | `core/src/session/runner/publish-llm-event.ts`, `core/src/session/runner/to-llm-message.ts` | RUN + AI + SCH; support valid provider-owned replay/output; reject genuinely unknown event variants rather than silently ignoring them. |
| R14 | S | `SessionAttempt.State` (~289), provider metadata namespace mismatch | `core/src/session/model-transport.ts`, `core/src/session/runner/to-llm-message.ts` | RUN + AI; correct producer namespace when valid metadata fails. Never rewrite state under a guessed provider/model identity. |
| R15 | M | `SessionAttempt.RunAsync` (~85-87), cost fixed to zero with no resolved price table | `core/src/session/usage.ts`, `core/src/model.ts` | CFG + RUN; attach pricing and calculate usage/cost. Zero here is documented unpriced fallback, not measured billing. |
| R16 | S/M | `Sdk/OpenCodeClient.cs::AskAsync` capability guard (~79) | `sdk/src/internal/host.ts`, `sdk/src/effect/opencode.ts` | PARENT SDK assignment + RUN; compose real Core readiness. Keep unavailable errors; do not make SDK an alternate guard-bypassing path. |

## Instruction And Reference Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| I01 | M | `Core/Instructions/LocalInstructions.cs::ReadAsync` (~34), explicit workspace | `core/src/instruction-discovery.ts`, `core/src/location-service-map.ts` | INS + Location; bind discovery to the selected Location rather than process cwd. |
| I02 | T | Same symbol (~83-84): configured `instructions`, `plugins`, `plugin`, `skills`, `references`, `mcp` loop | `core/src/config/plugin/{instruction,source,skill,reference,mcp}.ts`, `core/src/session/instructions.ts` | INS + CFG; compose each actual producer. Reference/other producer files appearing in source do not automatically remove this integration gate. PARENT assigns plugin/MCP runtime ownership. |
| I03 | T | Same symbol (~86-93): auto-discovered agent/mode/plugin/skill directories, global/upward skill sources | `core/src/config/plugin/{agent,source,skill-file}.ts`, `core/src/skill.ts` | INS + CFG; load definitions and guidance with source ordering. Do not treat nonempty discovered directories as empty catalogs. |
| I04 | T | `LocalInstructions.SelectedSystem` (~112-119): legacy agent/mode, non-default unresolved agent, settings beyond the supported subset | `core/src/agent.ts`, `core/src/config/plugin/agent.ts`, `core/src/v1/config/agent.ts` | INS + CFG; normalize and resolve complete agent settings before constructing system instructions. |
| I05 | T | `LocalInstructions.Removed` (~133-135): stored source baseline/update renderer throws | `core/src/instructions/index.ts`, `core/src/session/instruction-state.ts`, `core/src/session/instructions.ts` | INS; supply renderer for each retained epoch source. Removal text alone cannot render an existing baseline. |
| I06 | M | `LocalInstructions.ResolveDirectory` (~175), directory links rejected | `core/src/location.ts`, `core/src/instruction-discovery.ts` | INS + Location; canonical path/link resolution and discovery boundaries before broadening access. |
| I07 | S/M | `Core/Event/InstructionPersistence.cs::RequireSources` (~114-122), `InstructionInitializationBlockedException` | `core/src/session/instruction-state.ts` | INS + EVT; retain unavailable initial-source blocking and retained values. A missing native renderer/producer is I02-I05; transient filesystem unavailability is not a reason to erase prior instructions. |
| I08 | T | `Core/Reference/ReferenceSources.cs::Observe` (~31-38): git shorthand/source without local path | `core/src/reference.ts`, `core/src/config/plugin/reference.ts` | INS/Reference + PARENT repository/cache assignment; materialize the requested repository in its real cache. Local references are already represented. |

## Durable Storage Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| D01 | T/S | `Core/Database/DatabaseBootstrap.cs::Apply` (~34), `RequireCurrentSchema` (~61-87); `SqliteDatabase.cs::DatabaseSchemaUnavailableException` | `core/src/database/{migration,schema.gen,migration.gen}.ts` | STORE + PARENT migration assignment; existing-schema/data upgrades and legacy journal reconciliation. Nonempty unrelated databases and malformed journals must stay rejected. Do not stamp missing transformations. |
| D02 | T/S | `Core/Database/SessionStore.cs::RequireExecutionReadyAsync` (~355), `Core/Event/AssistantProjector.cs::AppendAsync` (~30), `ExecutionProjector.ProjectAsync` (~49), `SessionAdmission.AdmitAsync` (~46), `SessionMutationProjector.PublishAsync` (~62) | `core/src/session/projector.ts`, `core/src/database/migration.ts`, `core/src/event/sql.ts` | EVT + STORE; migrate genuine pre-event history without allowing a new event to disguise unsequenced projection writes. |
| D03 | T | `SessionStore.RequireExecutionReadyAsync`, surviving claim (~358) | `core/src/session/execution/restart.ts`, `core/src/session/store.ts` | RUN + EVT; startup recovery with bounded durable attempt accounting. Never release or overwrite an orphaned claim merely to resume. |
| D04 | T | `SessionStore.CreateSessionAsync`, fork parameter (~271) | `core/src/session/projector.ts`, `core/src/session/session.ts` | EVT + STORE; canonical fork event, selected history boundary, newest instruction-value adoption and projection copying. |
| D05 | S | `SessionStore.RequireDirectMutationAsync` (~406) | `core/src/session/session.ts`, `core/src/session/projector.ts` | EVT/STORE; keep durable aggregate protection. Wire implemented mutations through their events instead of reopening direct SQL helpers. |
| D06 | T/S | `Core/Event/SessionCreation.cs::CreateAsync` (~22), aggregate exists but session missing | `core/src/session/session.ts`, `core/src/session/projector.ts` | EVT + STORE; canonical removal/recovery and ID adoption. Do not recreate an aggregate by clearing its sequence. |
| D07 | T | `SessionAdmission.AdmitAsync` (~30), `SessionInboxOperations.cs::ReconcileAsync` (~41), `PromoteAsync` (~101), `ProjectDeliveredAsync` (~202) | `core/src/session/inbox.ts`, `core/src/session/compaction.ts`, `core/src/session/session.ts` | EVT + RUN; compaction/move controls and operation-specific conflict semantics. User/synthetic reconciliation is not a generic control implementation. |
| D08 | T | `SessionAdmission.AdmitAsync`, staged revert (~52) | `core/src/session/revert.ts`, `core/src/session/session.ts` | EVT + RUN; durable revert commit after successful input preparation and before new admission. |
| D09 | T | `SessionAdmission.DecodePayload` (~103), `RequireSupportedPayload` (~122) | `core/src/session/prompt.ts`, `core/src/session/inbox.ts`, `schema/src/prompt.ts` | EVT + INS + SCH; remove stale DTO-absence assumptions only after prepared attachment reconciliation/lowering is integrated. Current Schema has richer attachment types; source existence alone is not integration proof. |
| D10 | S | `AssistantEventData.Version/Validate` (~17,34), `ExecutionProjector.AppendAsync` (~25), `SessionMutationProjector.ProjectAsync` (~118) | `core/src/session/event.ts`, `core/src/session/projector.ts`, `schema/src/session-event.ts` | EVT + SCH; family dispatch guards. Live deltas/progress must not be made durable merely to avoid these exceptions; unknown event kinds remain invalid. |
| D11 | M | `Core/Session/SessionMutations.cs::SelectModelAsync` (~53,59): workspace catalog and unsupported selected transport | `core/src/session/session.ts`, `core/src/model-resolver.ts` | EVT + CFG + Location; real scoped catalog/transport support before selection. Unavailable or invalid model/variant errors remain legitimate. |

## Configuration And Provider Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| C01 | T | `Core/Config/ConfigLoader.cs::NormalizeDocument` (~90): enable/disable/provider policies | `core/src/config/normalize.ts`, `core/src/config/plugin/policy.ts`, `core/src/v1/config/migrate.ts` | CFG; source-faithful policy normalization/evaluation without dropping configured restrictions. |
| C02 | T | Same symbol (~98), legacy provider transport migration | `core/src/v1/config/provider.ts`, `core/src/v1/config/migrate.ts`, `core/src/model-resolver.ts` | CFG + AI; canonical provider identity and actual transport migration. Do not rename to an arbitrary supported protocol. |
| C03 | T | Same symbol (~139), legacy interleaved model compatibility | `core/src/v1/config/provider.ts`, `core/src/config/normalize.ts` | CFG + AI; preserve compatibility into native model/request lowering. |
| C04 | S | `Core/Llm/ProviderResolver.cs::ResolveAsync` (~64): legacy fields in canonical provider map | `core/src/config/normalize.ts`, `core/src/model-resolver.ts` | CFG; correct normalization/input location. Keep canonical-map validation rather than silently guessing mixed formats. |
| C05 | T | Same symbol (~73), selected transport outside implemented packages; `ConsoleIntegrationService.cs` selection (~54) | `core/src/model-resolver.ts`, `ai/src/protocols/index.ts`, `ai/src/providers` | AI + CFG; implement the exact selected protocol/package. No provider/model substitution. |
| C06 | T/M | `ProviderResolver.ResolveAsync` (~98), non-console OAuth; `ConsoleIntegrationService.ResolveCredentialAsync` (~158), `RefreshCredentialAsync` (~169) | `core/src/integration/connection.ts`, `core/src/plugin/provider/opencode.ts`, `core/src/credential.ts` | CFG + PARENT integration-owner assignment; method-specific authorization/refresh and metadata. The console refresher must not refresh another integration's credentials. |
| C07 | S | `ProviderResolver.ResolveAsync` (~143,161), provider-identity override or malformed/unresolved endpoint | `core/src/model-resolver.ts`, `core/src/config/variable.ts` | CFG; retain exact identity and HTTP endpoint validation. Missing/disabled model, unavailable variant and absent credentials are real selection/auth errors, not grounds for fallback. |
| C08 | T/S | `ConsoleIntegrationService.DiscoverProvidersAsync` (~210-211) converts normalization rejection | `core/src/plugin/provider/opencode.ts`, `core/src/v1/config/provider.ts` | CFG; fix C01-C03/C05 as applicable. The catch itself is correct error propagation and must not return a fake empty catalog. |
| C09 | T/S | `Core/Llm/ProviderCatalog.cs`, missing capabilities/limits (~110) and noncanonical compatibility (~142); `Core/Locations/CatalogLocation.cs::ResolveAsync/RealPath/RunAsync` | `core/src/model.ts`, `core/src/model-resolver.ts`, `core/src/project.ts`, `core/src/location.ts` | CFG + Location; supply real missing metadata and explicit-workspace placement. Failed VCS discovery, nonexistent directories, unresolved links and absent executables remain real environment/input errors, not grounds to invent project identity or model limits. |

## Transport Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| A01 | S/M | `Core/Llm/LlmClient.cs::ILlmClient.StreamAsync` (~21) default unsupported | `ai/src/llm.ts`, `ai/src/protocols/index.ts` | AI; concrete structured transports must override it. Implemented Google/chat adapters already do; do not treat this interface fallback as a globally broken stream. |
| A02 | S | `LlmAnswerText.StreamAsync` (~75,89,94): tool messages, structured/opaque output, authoritative replacement | `core/src/session/runner/llm.ts`, `ai/src/llm.ts` | AI + SDK; use the structured API for these cases. A string-only compatibility API cannot safely fabricate tool/retraction semantics. |
| A03 | M | `LlmHttp.RequireFields` (~189), unknown provider options | `ai/src/protocols/{gemini,openai-compatible-chat}.ts`, `ai/src/protocols/utils/openai-options.ts` | AI + CFG; extend only genuinely supported options from the selected protocol. Unknown/invalid option keys stay rejected. |
| A04 | S/M | `LlmRequestLowering.Body` (~69), chat topK | `ai/src/protocols/openai-compatible-chat.ts` | AI; determine protocol support, not provider-name guesses. Preserve rejection or use an explicitly supported raw provider option, not silent dropping. |
| A05 | T/M | Same symbol (~113), Google prompt-cache-key lowering | `ai/src/protocols/gemini.ts`, `ai/src/protocols/utils/cache.ts` | AI; map only source-supported cache semantics. Existing cached-content support is not equivalent to a generic prompt key. |
| A06 | S | Same symbol (~123), store disabled by model compatibility | `ai/src/protocols/openai-compatible-chat.ts`, `core/src/model-resolver.ts` | AI + CFG; honor compatibility; do not enable storage by deleting this guard. |
| A07 | M | `LlmRequestLowering.Messages` (~171), Google unsupported content/provider-executed history | `ai/src/protocols/gemini.ts` | AI + RUN; add valid protocol content/replay representations. Invalid role/content combinations remain invalid. |
| A08 | M | Same symbol (~193,211,220,224), chat tool results, non-image user media, unsupported assistant content, hosted replay | `ai/src/protocols/openai-compatible-chat.ts`, `ai/src/protocols/openai-responses.ts` | AI + RUN; preserve protocol distinctions. Do not route responses-only features through chat merely to avoid rejection. |
| A09 | S/M | Same symbol (~233), generic encrypted reasoning replay | `ai/src/protocols/open-responses.ts`, `core/src/session/runner/to-llm-message.ts` | AI + RUN; use correct provider metadata/replay; generic encrypted text is not interchangeable. |
| A10 | T | `LlmRequestLowering.ToolResultText` (~268), media-bearing tool results | `ai/src/protocols/{gemini,openai-compatible-chat}.ts`, `core/src/session/runner/to-llm-message.ts` | AI + TOOL; materialize and lower supported media rather than replacing it with text. |
| A11 | S/M | `Core/Llm/LlmStreamParser.cs::ChatStreamParser` (~256), legacy function-call field | `ai/src/protocols/openai-compatible-chat.ts` | AI; verify selected protocol compatibility before supporting legacy streaming. Current tool-calls framing must not be corrupted. |
| A12 | S | `LlmRequestLowering.Body` (~16), maximum-token field; other `Invalid(...)` role/schema/reserved-field checks | `ai/src/protocols/utils/openai-options.ts`, `ai/src/protocols/utils/gemini-tool-schema.ts` | AI; invalid-input checks, not missing implementations. `Unsupported(...)` at the file bottom is a shared error factory, not another feature. |

## Tools, Permissions And Shell Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| T01 | T | `Core/Tools/ToolRegistry.cs::.ctor` (11-15), deliberately installs no builtins | `core/src/tool.ts`, `core/src/tool/runtime.ts`, `core/src/location-services.ts` | TOOL + RUN; bind real Location, permission, output, mutation and process services, then expose only supported tools. Empty catalog is fail-closed composition, not completed tool support. |
| T02 | T/S | `ReadTool.ExecuteAsync` (~37), `GrepTool.ExecuteAsync` (~30), `GlobTool.ExecuteAsync` (~24) | `core/src/tool/plugin/{read,grep,glob}.ts`, `core/src/tool/read-filesystem.ts` | TOOL; inject permission/Location/ripgrep dependencies. Keep missing-policy rejection. |
| T03 | T/S | `WriteTool.ExecuteAsync` (~34), `EditTool.ExecuteAsync` (~45) | `core/src/tool/plugin/{write,patch}.ts`, `core/src/file-mutation.ts` | TOOL; authorized preview, serialized mutation and formatter integration before registration. Keep mutation-policy requirement. |
| T04 | T/S | `ShellTool.ExecuteAsync` (~31), missing policy | `core/src/tool/plugin/shell.ts`, `core/src/shell.ts`, `core/src/shell/scan.ts` | TOOL; actual shell selection/create hooks/scanning and permission context. No always-allow replacement. |
| T05 | T | Same symbol (~30), background jobs; truncation notice (~54), missing full-output capture | `core/src/shell.ts`, `core/src/shell/result.ts`, `core/src/plugin.ts`, `core/src/tool-output.ts` | TOOL + PARENT plugin-runtime assignment; owned background lifecycle and retained output artifacts. Foreground success must not imply background/truncation fidelity. |
| T06 | M | `LocalLiteralShellPolicy.PrepareAsync` (~36,42,47,49): shell family, directory stack, nonliteral path, named-user home | `core/src/shell/{select,scan,parse}.ts` | TOOL; full source-derived scanner and supported shell profiles. Keep the literal-subset guard until then. |
| T07 | M | `LocalLiteralShellPolicy.Parse` (~86,92,98,110,112): control/substitution/pipe/redirection syntax, concatenated quotes, quoted command head | `core/src/shell/{scan,parse}.ts` | TOOL; parse actual command structure and permissions. These are valid full-shell features, but unsafe to approximate in the literal policy. Unterminated quotes remain invalid input. |
| T08 | M | `ReadTool` documentation/implementation (8,43-68): text/directory only | `core/src/tool/plugin/read.ts`, `core/src/instruction-discovery.ts`, `core/src/image` | TOOL + INS; media/PDF/image handling and read-triggered instructions. Text read success does not establish these missing branches. |
| T09 | F/M | `WebFetchTool.ExecuteAsync` (35-46): advertises format/timeout, performs plain whole-body fetch | `core/src/tool/plugin/webfetch.ts` | TOOL; permission policy, format conversion, timeout and managed large-output behavior. Do not expose the prototype as a faithful registered tool. |
| T10 | T/M | `Core/Permissions/LocalPermissionRules` and `MemoryPermissionGrantStore` in `PermissionRules.cs` (23-87) | `core/src/permission.ts`, `core/src/permission/saved.ts`, `core/src/permission/sql.ts` | TOOL + STORE + SRV/UI; bind current Session/Agent rules and durable saved grants. Empty initial state and deny-on-missing-agent are legitimate; memory-only grants are not durable always-approval. |
| T11 | T/M | `ToolPolicy.cs` required interfaces; `LocalFileMutation.cs` optional formatter dependency | `core/src/file-mutation.ts`, `core/src/formatter.ts`, `core/src/location-services.ts` | TOOL + CFG; install actual host policies and honor configured formatting. No default implementation is installed by an interface declaration. |

## Client, Daemon And UI Sites

| ID | Class | Source symbol / anchor | Upstream owner | Accountable owner; next dependency |
| --- | --- | --- | --- | --- |
| L01 | S/M | `Client/ServiceProcess.cs::Start`, Windows/Linux platform restriction (~20) | `client/src/service-contender.ts`, `cli/src/server-process.ts` | CLIENT; Windows baseline is supported. PARENT must approve/assign another platform launcher before relaxing the restriction. |
| L02 | S | Same symbol, relative inherited roots, unsupported command forms, missing package; `ServiceDeployment.Snapshot` | `client/src/service-contender.ts`, `cli/src/services/service-config.ts` | CLIENT; retain safe absolute state roots and immutable multi-file deployments. Source-project daemon launches and build-directory writes must stay rejected. Single-file bundle support is a separate packaging extension. |
| L03 | S/M | `ServiceDaemon.EnsureWithOptionsAsync` incompatible version/build, repeated probe timeout, deadline; `StopAsync` deadline; `ServerHost.ServiceLifetime.StartingAsync` incumbent refusal | `client/src/promise/service.ts`, `client/src/service-timing.ts`, `cli/src/services/service-registration.ts` | CLIENT; deterministic source build identity and one authenticated cooperative idle-instance replacement implemented at user request. Explicit servers are never replaced; uncertain activity yields an actionable mismatch. Force recovery, PID signalling and PTY handoff remain unsupported. Full .NET 11 CLI graph compiled with matching output stamps; runtime replacement not exercised. See `docs/build-identity.md`. |
| L04 | S | `SessionHttpClient.RequestAsync` (~119), `SessionHttpClient.Events.ParseEvent` schema-support catches | `client/src/promise/generated/client.ts`, `protocol/src/groups/event.ts` | CLIENT + SCH; preserve the diagnostic wrappers. A valid wire payload rejected by incomplete Schema is assigned to SCH; never fabricate a decoded value. |
| L05 | S | `ServiceDaemon` launch-error catch and `ServiceLifecycleException` codes; `ServerHost` not-ready routing | `client/src/service-contender.ts`, `server/src/process.ts` | CLIENT; lifecycle/error boundaries, not additional unfinished feature implementations. Private nonce-correlated startup diagnostics now replace the former exit-code-only visibility gap. |
| L06 | S/M | `Cli/Tui/InteractiveTui.cs::ReadReadiness`, `ServerReadinessClient.ReadAsync`, UI unavailable states | `tui/src/context/data.tsx`, `client/src/promise/service.ts` | UI + SRV; consume truthful selected-server capabilities. Missing capability endpoint, missing required instructions, and connection failure must block submission rather than silently use embedded Core. |
| L07 | M | `Cli/Tui/SessionClientAdapter.cs` unsupported-event retention/reducer fallback | `tui/src/context/data.tsx`, `schema/src/session-event.ts` | UI + SCH; add canonical reducers for valid event families while retaining raw unknown events and reconciling after volatile disconnection. No guessed text extraction. |
| L08 | S/M | `Native/NativeTerminal.cs` Windows-console restriction (~33) | Native terminal implementation belongs to external `@opentui/core`; repository consumer is `tui/src/component/session-frame.tsx` | NATIVE; retain Windows host scope. PARENT assigns other OS console adapters; there is no upstream Blazor class to claim as a literal port. |
| L09 | S | `Blazor/Rendering/TuiRenderer.cs::ApplyEdits` default (~275) | Blazor render-diff contract; native rendering dependency `@opentui/core`, consumed by `tui/src/component/session-frame.tsx` | NATIVE; current known edit kinds are handled. Reject unknown framework edits; add an implementation only when a real new edit kind is required. |

## Empty Results That Are Not Fake Implementations

These were inspected because the sweep also matched empty/false/null/completed
returns. They must not become invented backlog items or be replaced with dummy data.

| Source family / symbols | Classification and owner | Upstream boundary / next action |
| --- | --- | --- |
| `SessionEndpoints` real list/get/inbox/message results, prompt reconciliation, successful resume and environment replacement | S, SRV/STORE | `server/src/handlers/{session,message}.ts`: empty real pages, idempotent existing admission and no-content after completed mutations are legitimate. Unlike S07-S09, these inspect or mutate real state. |
| `Core/Session/SessionQueries.cs::MessagesAsync`, missing anchor returns empty | S, STORE | `core/src/session/store.ts`: absent message cursor anchor yields an empty page in source; do not guess a different boundary. |
| `SessionStore.GetSessionAsync`, `SessionAdmission.ReconcileAsync`, `SessionInboxOperations.NextPromotableAsync`, projector optional lookups | S, STORE/EVT | `core/src/session/{store,inbox,projector}.ts`: absence and idempotent no-op branches, not success placeholders. |
| `InstructionPersistence.StateAsync`, preview/no-delta `PrepareAsync`, unavailable-source retention | S, INS/EVT | `core/src/session/instruction-state.ts`: no prior epoch, preview without mutation, no change and retained unavailable values are explicit semantics. |
| `ConsoleIntegrationService.DiscoverProvidersAsync` no credential/404/no provider map; `ConfigLoader.LoadAuth` absent file | S, CFG | `core/src/plugin/provider/opencode.ts`, `core/src/config.ts`: legitimate absent sources. Do not mask a normalization/auth error as absence. |
| `LlmHttp.Frames` end-of-stream, `LlmStreamParser` failed optional partial-JSON preview, `LlmRequestLowering.GoogleSchema` empty object-schema omission | S, AI | `ai/src/protocols/utils/{partial-json,gemini-tool-schema}.ts`: parser/protocol behavior, not a missing provider answer. |
| `SessionHistory.Lower` empty partial tool-error content, empty metadata map; `SessionAttempt.State` absent metadata | S, RUN/AI | `core/src/session/runner/to-llm-message.ts`: optional content/state absent. Supported actual values must still be preserved. |
| `LocalFileMutation` absent original file and idempotent dispose; `LocalToolLocation` missing path/repository; `OwnedToolProcess` stop conditions | S, TOOL | `core/src/file-mutation.ts`, `core/src/filesystem.ts`, `core/src/shell.ts`: ordinary absence/ownership cleanup, not permission bypass or fake process completion. |
| `PermissionService` no pending approvals; `MemoryPermissionGrantStore.ListAsync` no grants | S for empty state, TOOL | `core/src/permission.ts`; persistent-store integration is separately T10. Default-deny/ask/reject and missing-session errors are intentional. |
| `ServiceDaemon` discovery absence; `ServerHost` auth/ownership false branches; no-op hosted StartAsync before StartedAsync boot | S, CLIENT | `client/src/promise/service.ts`, `server/src/process.ts`: discovery/lifecycle phases. They must not be mistaken for an initialized database or ready execution. |
| `StartupDiagnostics.Dispose`, logging scope returning null; event-feed completion and cleanup | S, CLIENT/SRV | File-per-write logger owns no persistent handle; `server/src/event-feed.ts` owns subscriber lifecycle. These are not empty domain implementations. |
| `TuiRenderer.UpdateDisplayAsync` completed task after applying edits; `OpenTuiHost` unchanged-frame false; input parser false/null for unmatched input | S, NATIVE/UI | Render/input control flow; no application outcome is being fabricated. |
| `Blazor/ITerminalApp.OnFrame`, `OnTerminalFocusChanged`, `OnCapabilitiesChanged` default empty hooks | S, NATIVE/UI | Optional adapter extension hooks, not unimplemented required interface methods. |
| Schema validators returning false, enum fields named disabled, and UI fields named placeholder | S, SCH/UI | Input validation and form presentation; keyword matches are not port blockers. |
| `Core/Database/GenerateBootstrap.ps1` unsupported generator syntax checks | S, bootstrap owner | `core/script/migration.ts`: reject changed source grammar instead of generating a partial schema. This is build-source validation, not an application runtime guard. |

## Parent Handoff And Closure Rules

1. Assign PARENT-owned PTY, plugin/MCP, migration and SDK follow-up explicitly; keep the domain dependencies above attached to the assignment.
2. Reconcile S04 and I02-I05 with newly landed mutation/reference/agent producers before writing duplicate implementations. A stale unavailable endpoint can be an integration task even when Core code exists.
3. Keep SRV creation/admission/wake, RUN instructions/recovery, TOOL authorization, and UI readiness as separate owners with explicit handoffs. No single capability boolean proves this chain.
4. For each T/M/F row, closure requires source references for the real implementation, caller composition, and the exact guard replaced. Runtime verification remains pending unless separately authorized and reported; a clean build alone is not closure evidence.
5. Keep S rows as invariants unless the supported input/platform contract is deliberately extended. Do not remove protocol, credential, mutation, or permission rejections to produce an empty success.
6. Update this ledger when concurrent edits move a guard. Preserve the reason and dependency; do not report file-count percentages or infer completeness from absence of exception text.

The urgent startup-diagnostic work interrupted the initial inventory. A second
source sweep incorporated newly added reference/model-mutation guards and removed
the superseded normal-text/creation/environment hard blocks. A final handoff check
also incorporated wired selected mutations, catalog replacements, and their new
metadata/Location guards. `SessionEnvironment` and `SessionMutations` now have
explicit singleton registrations in ServerHost; their endpoint bodies must not
be mistaken for extra inferred request bodies. No live state was
read to update this document, and no build was run for this documentation pass.

## Build Checkpoint

A subsequent daemon review checked the declared `YamlDotNet` dependency and the
adapter helpers, then completed one fresh isolated full CLI build with zero
warnings/errors at
`C:/tmp/opencode/dotnet-final-run-build-404e9355dc60400490284a989f0b55e5`.
This closes the previously reported compile blockers for that source checkpoint,
not the runtime guard rows above. `run.ps1`, the app, the daemon, and tests were
not executed. No live diagnostic record was read, so the historical startup
screenshot remains unclassified by actual failure evidence.
