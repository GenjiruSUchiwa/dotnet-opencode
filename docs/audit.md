# OpenCode .NET Port Audit

## Provisional Status

This is a source-review snapshot, not a final audit or a completion certificate. Active implementation is in flight, including CLI, Blazor, native interop, and backend review. Findings describe the files inspected on 2026-08-30 and must be checked again after those changes settle.

See [Port Status](port-status.md) for current architectural boundaries, blocking findings, behavior-level work packages, and coordination rules. That document also reconciles earlier planning documents with the current no-tests restriction and reusable `OpenTui.Blazor` requirement.

The previous ledger's `Ported` checkboxes, package completion counts, and claim of a faithful file-for-file port were unsupported. They are withdrawn. A C# record, route, or class with a corresponding name does not establish contract or behavioral fidelity. The old file totals were not a reproducible inventory and must not be used to calculate progress.

Current validation is build-only in scope. The initial documentation review did not run a build; later authorized feed-only implementation builds are recorded separately in [Port Status](port-status.md#event-feed-implementation). No runtime fidelity proof is recorded here. A successful build proves compilation for that configuration, not native ABI correctness, API compatibility, provider behavior, or database compatibility. No tests were added, edited, or run; no application was executed, process terminated, or database opened for this review.

## Original Goal

Deliver a faithful, idiomatic .NET 10 implementation of the full OpenCode core, server, SDK/client, configuration system, and provider behavior, including compatible persistence semantics. The original same-live-database goal is superseded by the explicit channel-isolation decision below. Preserve externally observable contracts and lifecycle behavior while using appropriate .NET abstractions rather than copying TypeScript syntax or Effect machinery literally.

The terminal UI must use an owned Blazor renderer connected directly to native OpenTUI. It must not be a Bun process or SolidJS wrapper. A matching screenshot, basic chat stream, or loadable native library is not sufficient to claim the full terminal experience is ported.

Decision: use OpenCode channel `dotnet` and upstream channel filename/path conventions, including `opencode-dotnet.db`, rather than the live production database. Do not copy or write the live production database. Shared configuration compatibility remains a goal; database and service-state isolation must not silently isolate all configuration. Follow-up source inspection finds channel constants and database/client-registration defaults implemented, and the bounded configuration pass is complete as recorded in [Port Status](port-status.md#configuration-pass-record). Full daemon/configuration/provider fidelity and runtime proof remain pending.

Persistence fidelity still requires matching migrations, event ordering, projections, transaction boundaries, identifiers, encoding, recovery, and concurrency semantics within the isolated channel. Isolation does not fix direct projection writes or missing durable admission. See [Port Status](port-status.md#channel-decision) for ownership and the decision record.

## Review Baseline

| Source | Baseline |
| :--- | :--- |
| Upstream | `C:\Repos\sst\kind-nebula`, HEAD `e70d667a9fe3e84cc071a5596aa522c142c525b7` |
| .NET port | `C:\Repos\hona\opencode-dotnet`, HEAD `2d7e023ccb82145bb3b463b3da6a1d5ff5e83aea` |
| Review input | Working-tree source, including uncommitted work; HEAD identifiers alone do not reproduce this snapshot |
| Review method | Selected source comparisons and file discovery only; not an exhaustive file or symbol audit |

Paths beginning with `packages/` are relative to upstream. Paths beginning with `src/` are relative to this repository. Grouped paths below describe review boundaries, not a complete manifest.

## Status Definitions

| Label | Meaning |
| :--- | :--- |
| Partial | Corresponding implementation exists, but known gaps or missing fidelity evidence prevent acceptance. |
| Stub | Inspected code returns fixed or placeholder behavior instead of implementing the upstream operation. |
| Unreviewed | No sufficiently detailed comparison is recorded. Presence or absence of a similarly named file is not a verdict. |
| Accepted | Reserved for a bounded source mapping that satisfies all applicable acceptance criteria below, with reproducible evidence. No area is assigned this label here. |

## Source Map

These mappings replace the old schema checkboxes. Every previously checked row is now unreviewed unless a more specific partial or stub finding is documented below. Previously proposed target paths are not proof that those files exist. In particular, do not carry forward guessed lowercase filenames or `src/OpenCode.Schema/Identifiers.cs`; identifier files currently include `src/OpenCode.Schema/Identifier.cs` and `src/OpenCode.Schema/Ids/Identifiers.cs`.

| Upstream source boundary | .NET implementation or candidate | Status and review limit |
| :--- | :--- | :--- |
| `packages/schema/src/session-message.ts`, `schema.ts` | `src/OpenCode.Schema/SessionMessage.cs`, `Serialization/OpenCodeJsonContext.cs`, supporting scalar converters | Partial: foundational epoch/required-field/discriminator work is build-checked; remaining message variants and nested contracts are incomplete. |
| `packages/schema/src/config/compaction.ts` | `src/OpenCode.Schema/ConfigDetails.cs` (`ConfigCompactionInfo`, `ConfigCompactionKeep`) | Source constraints implemented in the canonical config pass and Schema build-checked; runtime integration is separate. |
| Remaining `packages/schema/src/` contracts, IDs, manifests, and retained `v1/` contracts | `src/OpenCode.Schema/` | Unreviewed: no comprehensive field, union, validation, or wire-encoding comparison. |
| `packages/core/src/session/session.ts`, `prompt.ts`, `inbox.ts`, `execution.ts`, `run-coordinator.ts`, `runner/` | `src/OpenCode.Core/Session/SessionExecutionEngine.cs` | Partial: direct text-stream path is not the durable execution lifecycle. |
| `packages/core/src/session/history.ts`, `instructions.ts`, `instruction-state.ts`, `compaction.ts`, `revert.ts`, `transfer.ts` | `src/OpenCode.Core/Session/SessionExecutionEngine.cs` (integration point) | Unreviewed as separate ports; the inspected prompt path does not implement these domains. |
| `packages/core/src/database/`, `session/sql.ts`, `session/projector.ts`, `session/store.ts` | `src/OpenCode.Core/Database/SqliteDatabase.cs`, `SessionStore.cs` | Partial: SQL access exists; channel isolation is assigned, and persistence/recovery fidelity is not established. |
| `packages/core/src/config.ts`, `config/` | `src/OpenCode.Core/Config/ConfigLoader.cs`, `src/OpenCode.Schema/Config.cs`, `Config/ConfigModels.cs`, `ConfigDetails.cs` | Partial overall: bounded discovery/normalization pass complete; full location-scoped reload and domain resolution remain incomplete. |
| `packages/core/src/provider.ts`, `plugin/provider.ts`, `session/model-transport.ts` | `src/OpenCode.Core/Llm/ProviderResolver.cs`, `LlmClient.cs` | Partial: provider resolution and text streaming exist; full provider contract is unreviewed. |
| `packages/protocol/src/api.ts`, `groups/`, `errors.ts`; `packages/server/src/api.ts`, `handlers/`, `middleware/` | `src/OpenCode.Protocol/`, `src/OpenCode.Server/` | Partial overall; selected stubs below. Route presence does not prove request, error, authorization, or response compatibility. |
| `packages/server/src/event-feed.ts`; `packages/protocol/src/groups/event.ts` | `src/OpenCode.Server/Services/EventFeedService.cs`, `Endpoints/EventEndpoints.cs` | Partial: typed overflow/encoding failures, drain-preserving completion, and serialized encode-once fan-out implemented and build-checked; canonical filtering/Bus/endpoint fidelity remain open. |
| `packages/client/src/service.ts`, `promise/generated/client.ts`; `packages/sdk/src/opencode.ts`, `promise.ts` | `src/OpenCode.Client/ServiceDaemon.cs`, `src/OpenCode.Sdk/OpenCodeClient.cs` | Partial: service and embedded prompt surfaces exist; full HTTP/SSE resource coverage and lifecycle are unreviewed. |
| `packages/cli/src/`, `packages/tui/src/` | `src/OpenCode.Cli/`, `src/OpenTui.Blazor/`, `src/OpenTui.Native/` | Partial, actively changing: source structure is not runtime or visual acceptance. |
| Other upstream packages and files | No exhaustive target mapping recorded | Unreviewed, not implicitly complete or excluded. Scope decisions require explicit rationale. |

## Major Known Gaps

### Contracts And Validation

The foundational serializer and prompt passes corrected location references and prepared user attachments. The later [assistant contract pass](port-status.md#assistant-contract-pass) supplies the full declared assistant content/tool-state/retry/snapshot fields, shell output, and concrete compaction variants, with Schema build evidence. This supersedes the earlier missing-field findings, not the requirement for runtime fidelity evidence. Nested contract/ID boundaries, date-range limits, remaining event families, and Core preparation/execution remain separate work; no final SessionMessage or full event-manifest acceptance is claimed.

The canonical config pass supersedes the earlier compaction validation finding: ConfigCompactionInfo/Keep now enforce the source nonnegative integer constraints. The broader current config root and all 17 submodule mappings are recorded in [Port Status](port-status.md#canonical-config-contracts). The protected provider-loader model file remains separate, so canonical Schema coverage must not be mistaken for complete runtime loading/normalization.

The foundational serializer pass added property-level epoch-millisecond converters to `SessionTime` and `MessageTime`, including streamed/completed timestamps, plus omission attributes for the covered optional fields. These work independently of the default ISO `DateTimeOffset` encoding. Canonical source-generation options now allow out-of-order discriminator metadata. Full optional-null decoding, nested contracts, other timestamp types, and JavaScript's wider date range remain fidelity gaps; compilation alone does not establish round-trip parity.

### Durable Session Execution

`src/OpenCode.Core/Session/SessionExecutionEngine.cs`, `PromptAsync`, inserts a visible user message immediately, then sends a hard-coded system message and only the current user prompt. It does not load stored history into the request. It streams strings and inserts the assistant message after streaming. Although it receives a tool registry, this method does not dispatch tool calls.

Compare with upstream `packages/core/src/session/inbox.ts`, `execution.ts`, `run-coordinator.ts`, `runner/`, and `history.ts`. Durable prompt admission and delivery, queue/steer ordering, idempotent retries, logical-step accounting, execution claims, interruption, and restart recovery are not established by the inspected .NET flow. Instruction epochs, compaction, revert, and transfer require their own mappings and evidence, not just schema records.

### Persistence Compatibility

`src/OpenCode.Core/Database/SessionStore.cs` directly inserts into `session_v2` and `session_message`. `AddMessageAsync` obtains `MAX(seq) + 1` and inserts in separate statements without an enclosing transaction in that method. `CreateSessionAsync` inserts a supplied session ID rather than adopting an existing session there. These paths do not demonstrate upstream ordered event publication and projection semantics.

Compare upstream `packages/core/src/session/projector.ts`, `sql.ts`, `inbox.ts`, and `packages/core/src/database/migration/`. `SqliteDatabase.cs` configures a connection and PRAGMAs; it is not evidence of matching migrations or atomic inbox-to-message delivery. Use the isolated `dotnet` channel decision, not same-path production database access. Do not copy production data to initialize it. No database contents were inspected.

### Configuration And Providers

The former single-file-only observation is superseded by the completed bounded configuration pass. `src/OpenCode.Core/Config/ConfigLoader.cs`, `LoadConfig`, now discovers shared global, explicit, ancestor, `.opencode`, and content configuration; related methods add substitutions and selected provider normalization/overlays. `ProviderResolver.ResolveAsync` now honors explicit/configured selection without silent substitution. Full location-scoped discovery/reload, domain resolution, OAuth/channel credential integration, and built-in/remote catalog discovery remain incomplete. The inspected server prompt handler still supplies its own defaults. See the [configuration pass record](port-status.md#configuration-pass-record) for scope and evidence; compilation is not configuration/provider fidelity.

`src/OpenCode.Core/Llm/LlmClient.cs` exposes `ILlmClient.StreamChatAsync` as a string stream with role/content inputs. The inspected requests and parsers handle text, not the complete structured tool-call, reasoning, usage, finish-state, and continuation lifecycle. Compare upstream `packages/schema/src/llm.ts`, `packages/core/src/provider.ts`, `plugin/provider.ts`, and `session/model-transport.ts`. Provider catalog, authentication refresh, custom-provider behavior, and request-option fidelity still need detailed review. No live provider requests were made.

### Server And SDK

`src/OpenCode.Server/Endpoints/FeatureEndpoints.cs` returns fixed empty skill, command, plugin, and reference lists with a current-directory location. Upstream `packages/server/src/handlers/skill.ts`, for example, resolves the skill service, and `packages/protocol/src/groups/skill.ts` specifies a location query. These .NET operations are stubs, not verified implementations of discovery or location selection.

`src/OpenCode.Server/Endpoints/PermissionEndpoints.cs` returns fixed empty request and saved-permission lists. This does not establish permission request/reply, persistence, or enforcement behavior. Compare upstream `packages/protocol/src/groups/permission.ts` and `packages/server/src/handlers/permission.ts`.

`src/OpenCode.Sdk/OpenCodeClient.cs` composes database stores, provider resolution, and the simplified prompt engine. It is a partial embedded facade, not evidence of full upstream SDK or generated HTTP client coverage. Compare `packages/sdk/src/promise.ts` and `packages/client/src/promise/generated/client.ts`; API resource coverage, typed failures, cancellation, streaming, and host disposal need acceptance evidence.

### Terminal UI And Native Interop

`src/OpenTui.Blazor/Rendering/TuiRenderer.cs` translates Blazor render-tree frames into owned nodes. `src/OpenTui.Native/OpenTuiNative.cs` declares native entry points and a library resolver. This is consistent with the intended architectural direction, but does not prove native binary compatibility or complete TUI behavior.

The CLI, renderer, layout engine, host, and bindings are under active implementation by other contributors. Native ABI and ownership, input dispatch, resize, Unicode width, scrolling, focus, terminal restoration, and upstream feature coverage remain unaccepted. Do not replace this status with obsolete claims about earlier code, or infer that a successful managed build loads and exercises the native library.

## Fidelity Acceptance Criteria

Acceptance is per bounded source mapping, not per filename count. An idiomatic many-to-one or one-to-many mapping is valid when it preserves the required behavior and records deliberate differences.

1. Record exact upstream files, exported symbols or operations, revision, .NET files and symbols, reviewer, remaining gaps, and evidence references. Inventory every in-scope source file, including explicit reasons for generated files, platform adapters, or legacy contracts that are not copied.
2. Compare schemas field by field: required versus optional values, omitted versus null keys, discriminators, unions, validation, defaults, IDs, dates, numeric ranges, errors, and storage encoding. Record representative accepted and rejected payloads.
3. Compare protocol and server behavior: all required routes and methods, query/body decoding, response and error schemas, status codes, authorization, location selection, event delivery, streaming, cancellation, and disposal.
4. Compare core behavior: durable admission and delivery, history and instruction assembly, tool permissions and execution, provider requests, logical steps and retries, compaction, interruption, concurrency, and recovery. A text-only prompt success cannot satisfy this criterion.
5. Verify channel `dotnet` path selection and establish persistence compatibility on isolated disposable fixtures, including migrations, ordered events and projections, transaction boundaries, replay, existing IDs, and recovery. Do not copy or write a live production database. Fixture execution remains subject to the validation restriction.
6. Verify configuration precedence and discovery, normalization, substitutions, reload behavior, provider catalog and authentication behavior, and SDK remote and embedded resource coverage without exposing credentials or confidential model identifiers.
7. Verify the Blazor-to-native rendering path without a Bun/SolidJS wrapper, including ABI version, resource ownership, input, layout, resize, terminal cleanup, and representative narrow/wide terminal workflows. Visual checks supplement, not replace, behavioral evidence.
8. Attach reproducible build evidence with revision, command, configuration, and result. Separately attach the contract and runtime evidence needed for the mapped behavior. Until those later checks are authorized and completed, retain partial or unreviewed status even if compilation succeeds.

These are future acceptance requirements, not claims that validation has run. This documentation-only pass does not add or execute the checks. Reconcile this provisional map with the implementation and backend audit in flight before making any final completion claim.
