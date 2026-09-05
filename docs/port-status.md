# Port Status And Backlog

## Current Mandate

Deliver the complete faithful, idiomatic .NET port, not a chat demo or a wrapper. The original plan targeted .NET 10; current sources target .NET 11 with the repository-pinned preview SDK. Preserve OpenCode's core, server, SDK/client, shared configuration compatibility, providers, wire contracts, and persistence semantics in the isolated `dotnet` channel. Use clean, maintainable .NET code with efficient resource ownership and execution. Do not substitute apparent structural similarity for behavioral compatibility.

Deliver `OpenTui.Blazor` as a standalone reusable terminal UI library for **any application**. It must contain no OpenCode branding, application screens, SDK integration, provider selection, or session logic. Use an owned Blazor renderer and direct native OpenTUI bindings, with no Bun, Node, SolidJS, or embedded JavaScript wrapper required to run the .NET UI.

This is an active coordination backlog, not a final audit. Native and daemon owners are implementing; the Blazor/CLI owner is active. Backend audit findings remain blockers until implementation and review resolve them. Documentation ownership is limited to `docs/audit.md` and this file. The snapshot uses the source baseline recorded in [Audit](audit.md); working-tree changes can supersede individual observations.

## Channel Decision

**Decision D1: approved by the user; channel defaults now observed in source, runtime verification pending.** This supersedes the earlier requirement to use the same live database as production OpenCode.

- Use OpenCode channel `dotnet` and upstream channel-specific filename and directory conventions. The database filename is `opencode-dotnet.db`, not the production database filename.
- Use upstream channel service registration conventions rather than an unrelated application-specific directory scheme. `packages/cli/src/services/service-config.ts`, `filename`, maps this channel to `service-dotnet.json`; registration state and service configuration are distinct files under their respective upstream roots.
- Do not copy, seed from, or write the live production database. Isolation is a design requirement, not permission to access either database during this documentation task.
- Preserve shared OpenCode configuration compatibility. Channel-specific database and service state do not imply that all global/project configuration should move to a separate namespace. The configuration owner must record which paths are shared, which are channel-specific, and how explicit overrides and precedence behave.
- The daemon owner implements channel lifecycle paths. Follow-up source inspection finds `OpenCodeChannel.Name`, `DatabaseFileName`, and `ServiceFileName` in `src/OpenCode.Schema/OpenCodeChannel.cs`; `SqliteDatabase` uses the channel database default, and `ServiceDaemon.GetDefaultRegistrationFile` uses the upstream state root with the channel filename. The configuration pass retains shared global/project configuration. Coordinate remaining lifecycle work with those owners rather than changing their files here.
- The configuration implementation pass is recorded below as completed within its bounded scope, not full configuration fidelity. Observed defaults do not prove every override or daemon lifecycle path. Runtime and database proof remain absent.

This changes deployment isolation, not durable semantics: B1-B3 remain blockers even with a separate file; B4 is partially addressed by the configuration pass but remains open at the server integration boundary. Shared database access is no longer an acceptance goal; compatible contracts, migrations, events, projections, and recovery within the isolated channel remain goals.

## Validation Limits

- Build-only validation is currently permitted for implementation owners. This documentation pass performs source inspection only and does not run a build.
- Do not add, edit, or run tests under the current instruction. Earlier documents requiring tests do not authorize them.
- Do not execute the application, access a database, run process-management commands, terminate processes, or commit as part of this documentation work.
- There is no runtime fidelity proof, native ABI execution proof, database compatibility proof, or performance measurement in this status report.
- Build success is a distinct evidence field, not acceptance of behavior. Record the actual command, configuration, revision, result, and warnings when an owner supplies build evidence. Do not infer it from source or another package's build.
- Future contract, recovery, interoperability, terminal, and performance verification requires authorization beyond this restriction. Keep acceptance blocked rather than silently weakening its criteria.

## Requirements Reconciliation

The following original documents were inspected. They remain useful design input, but examples, estimates, and proposed filenames are not current implementation evidence.

| Original requirement source | Retained intent | Current clarification |
| :--- | :--- | :--- |
| `docs/README.md`, Background and Motivation | Faithful translation to idiomatic .NET 10 using tasks, channels, cancellation, records, DI, and source-generated interop/JSON | Fidelity covers accepted inputs, outputs, errors, state transitions, and lifetime semantics. File-for-file translation is not an acceptance metric. |
| `docs/README.md`, Target Solution Architecture; `docs/architecture/package-mapping.md` | Separate Schema, Protocol, Core, Server, Client, SDK, CLI, and native UI concerns | Proposed target paths and architecture diagrams are not a verified dependency graph. Use the explicit boundaries below. |
| `docs/architecture/tui-and-opentui.md`, Phase 1 and hybrid options | Decouple terminal UI from the backend and use native OpenTUI | Bun-hosted and embedded-JavaScript UI options are superseded for the target .NET UI. The reusable component layer is Blazor, not a new app-specific reactive framework. |
| `docs/ruleset/04-persistence-and-storage.md` | Compatible SQLite schema, durable inbox, atomic delivery, and explicit connection lifetime | D1 supersedes same-live-database use with channel `dotnet`. Example DDL is not the migration authority. Compare the pinned upstream migrations, Bus, event sequence, and projector behavior before any compatibility claim. |
| `docs/vertical-slice/agent-fleet-workplan.md` | Coordinate package ownership and preserve JSON keys, event strings, routes, and columns | Its mandatory test commands and test-file checkboxes are superseded by the current no-tests/build-only restriction. A vertical slice is not full-port completion. |
| `docs/README.md` and `docs/architecture/tui-and-opentui.md`, performance and AOT goals | Efficient startup, allocation discipline, source-generated boundaries, and deployability | Numeric startup/memory claims and AOT compatibility are unverified goals, not measured results. Blazor framework requirements and native assets need explicit review. |

## Architectural Boundaries

In this table, dependencies mean runtime project dependencies; they do not require one-to-one translations of upstream helper types.

| Area | Owns | Boundary |
| :--- | :--- | :--- |
| `OpenCode.Schema` | Canonical serializable contracts, IDs, validation, wire/storage encoding | No Core, Server, provider execution, filesystem discovery, or application services. |
| `OpenCode.Protocol` | Routes, request/response/error contracts, event surface | Depends on Schema, not Core or Server implementation. |
| `OpenCode.Core` | Location-scoped domain services, durable events/projections, session execution, providers, tools, configuration | Uses Schema and, where required, Protocol. No Server or UI dependency. Keep process-global execution coordination separate from location-scoped runners. |
| `OpenCode.Server` | Hosting, authorization, decoding, HTTP/SSE/WebSocket adapters | Composes Core and Protocol. Handlers delegate to shared domain operations; they must not implement an independent runner or write projections directly. |
| `OpenCode.Client` | Typed HTTP/SSE client and service connection/lifecycle support | Depends on Schema/Protocol, never Core/Server runtime projects. Service discovery is not permission to mutate a shared database. |
| `OpenCode.Sdk` | Remote/embedded composition and owned host lifetime | May compose Client, Core, and Server; remote and embedded calls must converge on the same domain semantics. |
| `OpenTui.Native` | Versioned native ABI bindings, native resource lifetime, terminal primitives | No OpenCode assemblies, application policy, service discovery, or provider integration. Native symbol ownership and binary provenance belong here. |
| `OpenTui.Blazor` | Generic component host, render-tree translation, layout, input/event dispatch, generic terminal components | Depends on native bindings and required .NET/Blazor facilities only. Any application's root component, parameters, services, and cancellation must be supplied without naming an OpenCode type. |
| `OpenCode.Cli` | OpenCode commands, branded screens, prompt/session UI, client/SDK integration | Consumes the generic library. Owns the OpenCode application component and adapts its state/events to generic UI primitives. |

Follow-up source inspection shows progress: `src/OpenTui.Blazor/OpenTui.Blazor.csproj` references native bindings and the ASP.NET Core framework, not the SDK. `OpenTuiHost.RunAsync<TComponent>` now takes generic parameters and requires `IComponent, ITerminalApp`; the branded component is now `src/OpenCode.Cli/Tui/Components/OpenCodeApp.cs`. No direct OpenCode references or branding were found in the inspected Blazor library source. This supersedes the earlier observation of `RunAppAsync` and `Components/OpenCodeApp.cs` inside that library. It is source evidence, not final reusable-host acceptance: native title branding, host lifetime, platform support, and nested component input remain review items below.

## Blocking Findings

These incorporate the backend audit handoff and source inspection. They describe concrete compatibility failures in the inspected paths, not incidents observed against a live database or provider. Recheck them after the active owners' changes.

| ID | Blocker and source evidence | Required resolution |
| :--- | :--- | :--- |
| B1 | Invalid direct projection mutation semantics, originally found in the shared-database path: `SessionStore.CreateSessionAsync` and `AddMessageAsync` in `src/OpenCode.Core/Database/SessionStore.cs` write projected tables directly; message sequence allocation uses a separate `MAX(seq) + 1` query. | Route durable changes through the upstream-equivalent event/sequence/projector transaction boundary in the isolated channel. Match migrations and storage encoding. D1 removes same-live-database use but does not resolve this blocker. |
| B2 | No durable inbox/Bus equivalent in the inspected prompt path: `SessionEndpoints.MapSessionEndpoints` publishes an enqueue-shaped payload through `IEventFeedService.PublishRaw`, then starts work; the inbox GET returns an empty array. | Persist admission before acknowledging it, implement `resume: false`, ID reuse/conflicts, ordered queue/steer delivery, cancellation, and atomic inbox consumption/message projection. An in-memory feed cannot replace `packages/core/src/bus.ts`. |
| B3 | Concurrent runner and duplicated execution paths: the server prompt handler starts a `Task.Run` per request with `CancellationToken.None`; `SessionExecutionEngine.PromptAsync` implements another model-stream path. The interrupt endpoint returns a fixed response. | One process-global Session-ID coordinator must serialize same-session execution, coalesce wakeups, join resumes, permit different sessions concurrently, and route interruption/settlement correctly. Share the runner between API and embedded SDK; preserve recovery claims on shutdown. |
| B4 | Resolver fallback defect addressed in the completed configuration pass: `ProviderResolver.ResolveAsync` now uses explicit/configured selection and rejects unavailable models or unsupported transport behavior instead of substituting a model. The inspected server prompt handler still supplies hard-coded model/variant defaults (`SessionEndpoints.cs`, `modelToUse`/`variantToUse`). | Server owner must delegate default selection to the configuration/domain boundary. Full location/agent/session selection, catalog, credentials/OAuth, and transport fidelity remain incomplete. Do not treat the bounded configuration pass as end-to-end completion. |
| B5 | Generic-library separation is partially addressed: branded component moved to CLI and host is generic in the follow-up snapshot. `src/OpenTui.Native/NativeTerminal.cs:37` still sets an OpenCode-branded title; reusable host/input/lifetime acceptance remains open. | Native owner removes application branding from the terminal layer; Blazor/CLI owner addresses host lifecycle and generic component interaction. Preserve the now-clean Blazor project dependency direction. See H1/H2/H8 below; no runtime acceptance claimed. |

## Accountable Coverage

Every work package must maintain a symbol/behavior record with these fields: upstream revision and exact source path; exported symbol or protocol operation ID; target path and symbol (or explicitly proposed target); accepted inputs and validation; outputs/errors; state transitions and side effects; ordering/cancellation/lifetime rules; deliberate differences; owner; reviewer; build evidence; missing runtime evidence; and remaining blockers.

Use one row per coherent operation or invariant. Expand grouped rows below before declaring an implementation ready for review. `Partial`, `Stub`, and `Unreviewed` have the meanings in [Audit](audit.md). An implementation may be build-checked while behavioral acceptance remains blocked. File presence, method signatures, records, and comments claiming a faithful port do not close a row. Do not invent completion percentages.

## Safe Work Packages

Ownership below reflects the coordination handoff, not an assignment to a new agent. Backend packages need an explicit implementation owner; the backend audit itself is not an implementation assignment. Proposed services are responsibilities to map, not claims that target files already exist.

| Package | Upstream symbols or behavior boundary | Current/proposed .NET boundary | Next deliverable and dependencies | Ownership/status |
| :--- | :--- | :--- | :--- | :--- |
| W1 Contracts | `packages/schema/src/session-message.ts`: `LocationSwitched`, `User`; `schema.ts`: `DateTimeUtcFromMillis`, `optional`, `NonNegativeInt`; `session-inbox.ts` and event manifests | `OpenCode.Schema/SessionMessage.cs`, `ConfigDetails.cs`, `SessionInbox.cs`, `EventManifest.cs`, `Serialization/OpenCodeJsonContext.cs` under `src/` | Foundational serializer pass recorded below; continue field/union/validation coverage for remaining message variants and manifests. Distinguish omitted keys, nulls, numeric dates, and current versus legacy events. Supplies W2, W3, W6. | Foundational Schema scope assigned and build-checked; broader contracts partial. |
| W2 Persistence | `packages/core/src/bus.ts`: `latestSequence`, `reserveSequence`, durable publication; `event/sql.ts`; `session/projector.ts`, `session/sql.ts`; `database/migration/` | Current `src/OpenCode.Core/Database/SessionStore.cs`, `SqliteDatabase.cs`; proposed Core durable Bus/projector boundary | Map every mutation, migration, sequence reservation, projection, and transaction boundary. Replace the B1/B2 bypasses through one durable path; no database execution in this work mode. Depends on W1. | Backend owner to assign; blocked B1/B2. |
| W3 Admission | `packages/core/src/session/prompt.ts`, `inbox.ts`; `session.inbox.enqueued` and `session.inbox.delivered` | Current `SessionExecutionEngine.PromptAsync` and server prompt/inbox operations; proposed Core admission service | Map admission versus execution, first-admission-wins ID reuse, cross-session/type conflicts, `resume: false`, steer/queue ordering, edit/cancel, and atomic delivery. Depends on W1/W2; do not retain a feed-only acknowledgment. | Backend owner to assign; blocked B2. |
| W4 Execution | `packages/core/src/session/execution.ts`: `Interface.resume`, `wake`, `interrupt`, `awaitIdle`; `run-coordinator.ts`: `Coordinator`, `make`; `execution/restart.ts` | Current `src/OpenCode.Core/Session/SessionExecutionEngine.cs`; proposed shared coordinator/runner | Map process-global ownership, location routing, same-session joining, wake coalescing, independent-session concurrency, user/shutdown cancellation, claims, and restart attempts. Replace server `Task.Run` orchestration after W3. | Backend owner to assign; blocked B3. |
| W5 Request And Providers | `packages/core/src/config.ts`: `Interface.entries`, `changes`; `config/normalize.ts`, `config/variable.ts`; `provider.ts`; `session/model-request.ts`, `model-transport.ts`, `history.ts`, `instructions.ts`, `runner/` | `ConfigLoader.LoadConfig`, `ProviderResolver.ResolveAsync`, `ILlmClient.StreamChatAsync`, current prompt engine | Bounded configuration/discovery and explicit-selection pass completed; see record below. Continue server default integration, credentials/OAuth/catalog, location-scoped reload, structured provider events, history, instructions, tools, retries, and compaction. Preserve one explicit model stream per physical attempt. | Configuration pass complete, broader W5 partial; B4 integration remains open. |
| W6 API And SDK | `packages/protocol/src/groups/session.ts`, `event.ts`, remaining groups; `packages/server/src/handlers/`; `packages/client/src/promise/generated/client.ts`; `packages/sdk/src/promise.ts` | `src/OpenCode.Protocol/Groups/`, `OpenCode.Server/Endpoints/`, `OpenCode.Sdk/OpenCodeClient.cs`; proposed typed Client resources | Enumerate operation IDs/methods/paths, request/response/error envelopes, auth/location rules, streaming/reconnect, and embedded/remote lifetime. Replace stub operations with domain calls only as W2-W5 land; no second runner or direct projected-table writes. | Backend owner to assign; partial/stubs. |
| W7 Daemon And Channel | `packages/client/src/service.ts`, `service-contender.ts`, `service-version.ts`, `service-timing.ts`, `promise/service.ts`; `packages/cli/src/services/service-config.ts`: `filename` and path selection | `src/OpenCode.Client/ServiceDaemon.cs`: `DiscoverAsync`, `EnsureAsync`, lifecycle operations; `src/OpenCode.Core/Database/SqliteDatabase.cs` default path | D1 channel/database/registration defaults observed in source. Continue lifecycle and override review with owner: identity/version/health checks, election, auth, start/stop ownership, explicit-server behavior, cancellation, and errors. No production DB copying/writes. | Daemon owner retains ownership; defaults implemented in inspected source, full lifecycle unverified. |
| W8 Native | Upstream OpenTUI dependency's actual exported native ABI and version; dependency selection in `packages/tui/package.json` | `src/OpenTui.Native/OpenTuiNative.cs` and native resource wrappers | Record exact native source/binary version and each bound symbol's signature, struct layout, handle ownership, error result, and cleanup. Do not use illustrative ABI names from old architecture docs as authority. Agree terminal/input/resize contracts with W9. | Native owner active; partial, ABI execution unverified. |
| W9 Generic Blazor | Blazor renderer/component lifecycle and native ABI from W8; upstream `packages/tui/src/` is app behavior reference, not a library dependency | `src/OpenTui.Blazor/OpenTuiHost.cs`, `Rendering/TuiRenderer.cs`, `TuiLayoutEngine.cs`, `Components/`, `Nodes/` | Retain the newly generic host and CLI-owned branding; finish B5 boundary/lifetime review, generic event dispatch, input/focus, resize/layout, Unicode, scrolling, and owned versus caller-owned services. Record a non-OpenCode consumer usage design without adding tests or claiming runtime proof. | Blazor/CLI owner active; partial, B5 acceptance open. |
| W10 OpenCode UI | `packages/cli/src/`, `packages/tui/src/` commands, session state, tools, permissions, settings, themes, keybindings | `src/OpenCode.Cli/Program.cs`, `Tui/`; app component moved from Blazor by owner | Map user actions and state transitions to Client/SDK operations, not just visual resemblance. Consume W7/W9 contracts; keep branding and all OpenCode integration here. Track each missing upstream feature explicitly. | Blazor/CLI owner active; partial. |
| W11 Remaining Domains | Core agents, tools, permissions, MCP, skills, plugins, references, filesystem, shell/PTY, VCS/worktrees, workspace/location, fork/revert/transfer and related protocol groups | Existing `src/OpenCode.Core/Tools/`, schema records, and server endpoints; remaining target symbols to map | Inventory exported services and reachable operations, distinguishing partial implementations, stubs, and absent mappings. Split by domain after dependencies and ownership are clear. W1-W10 are not the entire port. | Unassigned; unreviewed except audit findings. |

Paths abbreviated within a .NET project row are relative to that project's `src/` directory. Backend owners must expand W5 and W11 into smaller operation-level records rather than treat either broad row as a single completion checkbox.

## Coordination Order

1. Keep native W8, daemon W7, and generic Blazor/CLI W9-W10 with their active owners. Agree public contracts before overlapping edits. Document owner handoffs and changed symbols; do not overwrite another owner's work.
2. Assign W1-W4 explicitly. Establish canonical contracts and durable persistence/admission before reconnecting server and SDK mutation paths. B1-B3 are compatibility blockers, not optional cleanup after UI work.
3. The configuration owner aligns shared versus channel-specific conventions with the daemon owner under D1. Configuration mapping and B4 correction can proceed alongside persistence work; agree selection/error contracts before the runner consumes them. Channel-path changes alone do not close B4.
4. Inventory W6 routes and W11 domains through source inspection in parallel. Avoid implementing handlers on speculative services or silently returning success for unsupported operations.
5. After owners report build results, reconcile changed symbols and remaining gaps in this ledger. Do not label the port accepted while runtime evidence is prohibited or missing. Escalate the validation restriction when runtime acceptance is needed; do not run checks implicitly.

## Code Quality Gate

Compatibility takes priority over speculative optimization, but avoid unnecessary abstractions, duplicated execution loops, detached tasks without lifecycle ownership, unbounded queues without an explicit policy, and repeated full-history/string materialization where the required behavior permits bounded processing. Use source-generated serialization at public/storage boundaries, cancellation with defined ownership, explicit async disposal, and idiomatic .NET DI scopes.

For renderer and interop changes, review frame rebuild/allocation behavior, UTF-8 conversion, native buffer/handle lifetime, redraw scheduling, and input fairness. For server/client changes, review HTTP connection reuse, streaming/backpressure, and task/resource lifetime. Record tradeoffs and unsupported platforms. Do not claim zero allocation, native AOT compatibility, throughput, or startup targets without evidence; performance claims remain unreviewed under build-only validation.

## Performance Architecture Review

This follow-up is a source-only review of the active working tree on 2026-08-30. Line references identify the inspected snapshot and can move during concurrent implementation. No tests, benchmarks, builds, application execution, process commands, or database access were performed. Priorities below indicate implementation order, not measured cost. P1 denotes correctness or lifecycle work before optimization; P2 denotes a concrete allocation/work-scaling issue or architectural gap requiring owner review. These findings do not replace the durable-core blockers above.

### Prioritized Findings

**H1 / P1: Host cleanup and repeat-use contract are incomplete.** `src/OpenTui.Blazor/OpenTuiHost.cs:25-31,69-78` creates a terminal, attaches a component, then enters the cleanup region. `StopAsync` is called on normal loop exit, but attachment failure occurs before that region. `TuiRenderer.AttachRootComponentAsync` adds a root ID before rendering, and `_roots` is never pruned (`Rendering/TuiRenderer.cs:11,17-24`). A second run on one host retains the first root; a failed attachment also leaves root ownership with the renderer. Components are not disposed at the end of a run. In addition, failure from `Renderer.DisposeAsync` skips disposal of the host-owned service provider. The native `using` does provide terminal cleanup when control leaves the run method, but does not solve managed component ownership. Owner: Blazor/CLI. Define either single-run hosting with an explicit guard or a reusable run that removes/disposes its root, including failed initialization. Ensure renderer and owned services each get cleanup even if the other fails; never dispose caller-owned services. Coordinate shutdown of active run work before renderer disposal. Do not add an elaborate concurrent-host model if the public contract is single-run.

**H2 / P1: Generic hosting still inherits application branding and platform limits.** `src/OpenTui.Native/NativeTerminal.cs:20-23,37` rejects non-Windows/non-interactive hosts and sets the terminal title to an OpenCode-specific value. `src/OpenTui.Blazor/OpenTuiHost.cs:25-26` changes the title afterward, but that does not remove the native library's application policy. `NativeRenderer.cs:3-7` requires serialized calls and borrowed-handle lifetime discipline. Owner: Native for branding/terminal ownership, Blazor for host integration. Keep the default native layer neutral, let the application supply a title, and document the Windows interactive host limitation separately from the platform-neutral renderer API. Confirm whether serialized dispatcher calls suffice for native ownership or whether actual thread affinity is required; an async dispatcher is not a dedicated OS thread. Do not claim cross-platform hosting based on the presence of a native library resolver.

**H3 / P1: SSE overflow defect corrected in source; runtime acceptance deferred.** The earlier `DropWrite` mode could report successful writes while dropping frames, bypassing subscriber eviction. The dedicated feed change in `src/OpenCode.Server/Services/EventFeedService.cs` now uses bounded `Wait` mode exclusively through nonblocking `TryWrite`, evicts only the subscriber that rejects a frame, and completes its writer with an error so previously accepted frames drain first. Publication and registration/removal/disposal are serialized under one lock. The isolated Server build passed; no behavioral test or runtime check was run. See the implementation record below for scope and remaining fidelity gaps.

**H4 / P2: Presentation batching happens after costly tree rebuilding.** `src/OpenTui.Blazor/Rendering/TuiRenderer.cs:27-35,44-76` ignores the changed-component subset of `RenderBatch` and reconstructs every root's entire `TuiNode` tree on each update. Each node owns a new child list (`Nodes/TuiNode.cs:13-15`); multiple text frames concatenate strings. `src/OpenCode.Cli/Tui/Components/OpenCodeApp.cs:127-130,192` calls `StateHasChanged` per streamed chunk and materializes the full transcript for rendering. `OpenTuiHost.cs:53-65` skips clean painting and limits painting cadence, but does not coalesce these preceding managed render/tree costs. Owner: Blazor/CLI. First coalesce presentation invalidations while retaining all input data and final/error updates; then consider dirty-subtree translation or stable node reuse if the simple approach remains insufficient. Preserve Blazor identity, keyed reorder, nested updates, event ownership, and disposal. Do not pool live nodes or retain borrowed render-frame arrays without a documented ownership boundary.

**H5 / P2: Layout repeats full text work before clipping.** `src/OpenTui.Blazor/Rendering/TuiLayoutEngine.cs:22-38,55-63,117-122,148-176` splits text for width, wraps it for natural height, may repeat measurement while assigning child sizes, then wraps again for painting. `Wrap` builds a list and strings for all lines before the tail viewport chooses visible lines. Text normalization, grapheme strings, and line materialization scale with total text rather than visible output. Input scrolling repeatedly slices and measures shrinking prefixes (`:107-115`), producing repeated scans for long input. Owner: Blazor/CLI. Reuse one measurement/wrap result per unchanged text, width, and negotiated width method, or retain line boundaries and materialize visible lines only. Compute input viewport boundaries in a single grapheme-aware pass. Keep caches bounded and invalidate on width-method/resize/content changes; do not cache entire historical transcripts indefinitely or substitute UTF-16 length for terminal cell width.

**H6 / P2: Native drawing has improved, but measurement still allocates per cache miss.** `src/OpenTui.Native/OpenTuiNative.cs:232-255` uses a bounded stack allocation for small UTF-8 strings and `ArrayPool<byte>` for larger strings, slices to the encoded length, pins only for the native call, and returns the rental in `finally`. This is useful source evidence, not zero-allocation proof. `MeasureCellWidth` still creates a UTF-8 byte array and calls native encode/free (`:196-214`); `TuiLayoutEngine.cs:15-16,132-145` clears the grapheme-width cache every paint and allocates grapheme strings while measuring. Owner: Native implements interop, Blazor owns cache/work reuse. Preserve the native negotiated-width algorithm. Document that the native draw/measure call consumes input synchronously and does not retain the pointer; neither stack nor returned pool memory may escape into deferred native rendering. Keep the existing native-output `finally` cleanup and verify count/ABI types against native source. Consider a span-based measurement path and a bounded width-method-aware cache only after avoiding repeated layout work. Do not introduce speculative SIMD or unbounded stack allocations.

**H7 / P2: Generated JSON coverage exists, but several hot/public paths bypass it.** `src/OpenCode.Schema/Serialization/OpenCodeJsonContext.cs:5-91` declares generated contracts; `EventFeedService.Publish` uses its type metadata (`src/OpenCode.Server/Services/EventFeedService.cs:56-60`). In contrast, `PublishRaw` serializes an anonymous envelope containing `object` with default overloads (`:63-73`); provider request bodies do the same in `src/OpenCode.Core/Llm/LlmClient.cs:90,197`. `src/OpenCode.Core/Database/SessionStore.cs:149-167` parses data, builds a dictionary, serializes it, parses the new JSON, and clones the root; the second `JsonDocument` is not disposed. The message write path also uses anonymous/default serialization (`:319-327`). Owner: backend/schema. Map each concrete wire/storage payload to generated metadata or explicit `Utf8JsonWriter`/JSON DOM handling as appropriate; dispose each owned document and avoid unnecessary encode/decode round trips. Merely registering a context does not make default overloads use it. Preserve timestamps, polymorphism, null/omission rules, and payload bytes. `ServerHost.CreateApp` does not visibly configure the generated HTTP JSON resolver in the inspected source (`src/OpenCode.Server/ServerHost.cs:31-49`); endpoint DTO coverage and ASP.NET serialization need separate review. No trimmed/AOT acceptance is established.

**H8 / P2: Reusable components still lack an input-event contract beyond the root.** `src/OpenTui.Blazor/ITerminalApp.cs:7-13` exposes root-level key/paste/resize/stop hooks. `Components/TuiComponents.cs:40-54` exposes display value and cursor for `Input`, but no value-change/submit callbacks or focus identity. `Rendering/TuiRenderer.cs:90-115` translates visual attributes only; it does not record event-handler IDs for generic dispatch. Owner: Blazor/CLI. Document the current low-level root-controller model and define how an arbitrary application's nested inputs receive focus and dispatch callbacks through Blazor, without importing SDK concepts. Source generation or lower allocations cannot compensate for missing interaction behavior. Retain the library's clean project dependency direction while adding only the necessary reusable event boundary.

### Lifetime And Batching Rules

- Native input pointers are borrowed for the documented synchronous call only. Rentals return exactly once after the last consumer, including exceptional paths; asynchronous continuations must own their memory. Do not retain a span, render-tree frame array, or native buffer handle beyond its owner's valid lifetime. Reacquire native buffers after resize.
- Do not add clearing of every pooled text buffer, global caches, or node pools without a concrete data-lifetime/performance reason. If sensitive-buffer clearing is required, document the policy and its cost rather than making an unmeasured performance claim.
- Separate ingestion from presentation. Coalesce display updates, not ordered session facts or provider chunks needed for durable state. Final/error/cancellation state must become visible without waiting for another chunk.
- Retain the input fairness bound and clean-frame check already present in `OpenTuiHost`. Its 16 ms delay and repeated console polling are a scheduling choice, not evidence of a frame rate or CPU target. Do not replace them with a busy loop.
- Generated `LibraryImport` declarations are present throughout native bindings and OS calls. They reduce reliance on runtime interop stub generation, but do not establish ABI, pointer retention, asset packaging, or AOT correctness. `InstantiateComponent(typeof(TComponent))` delegates activation to Blazor; do not claim the entire application is reflection-free because interop and some JSON are generated. Review the framework activation and trimming requirements explicitly.

### Build-Only Acceptance

1. Owners report the changed symbols, source rationale, affected public contracts, and build command/configuration/result. This reviewer does not execute those commands. A normal build result is not a trimmed or native-AOT publish result.
2. Review root, service-provider, task, rental, JSON document, and native-handle ownership on success, cancellation, and failure. State whether hosting is single-run, sequentially reusable, or concurrent; enforce only the supported contract.
3. Enumerate the allocations removed or work avoided from code structure, without assigning throughput, memory, latency, or percentage improvements. Keep Unicode width, rendering identity, event ordering, and wire encoding invariant.
4. Confirm no OpenCode branding, SDK dependency, or app policy remains in reusable Blazor/native code; document unsupported host/platform combinations and the generic interaction model.
5. Keep benchmarks, tests, runtime interaction, and database checks deferred under the current restriction. Performance and runtime fidelity remain unaccepted even after source review and a successful build.

Ongoing steering order: resolve H1-H3 with the relevant owners, then address H4/H5 batching and repeated work before H6 interop micro-optimization. H7 runs with the schema/backend work; H8 belongs to generic-library coverage. Native implementation remains with the native owner. Re-read each finding after concurrent edits and record superseded observations rather than presenting this snapshot as final.

## Event Feed Implementation

Exclusive implementation ownership for this change was `src/OpenCode.Server/Services/EventFeedService.cs`, plus documentation status notes. The entire upstream `packages/server/src/event-feed.ts` and `specs/v2/event-stream-architecture.md` were read. No endpoint, host, client, configuration, native, or Blazor implementation was edited in this change.

| Boundary | Change and source reasoning |
| :--- | :--- |
| `EventFeedSubscriber` constructor and `TryWrite` | Preserve capacity 4,096. Use `BoundedChannelFullMode.Wait` with `TryWrite` only: a full queue rejects immediately without dropping an accepted frame or awaiting space. Explicitly disable synchronous continuations so reader work does not run inline under the feed lock. |
| `BroadcastFrame` | Remove a rejecting subscriber immediately, complete its writer with `SubscriberOverflowException` carrying its capacity, and continue offering the same immutable frame to healthy subscribers. Channel completion preserves accepted backlog; its failure surfaces after that backlog drains. No durable replay or persistence is introduced. |
| `Publish`, `PublishRaw`, `Subscribe`, `Unsubscribe` | One `System.Threading.Lock` serializes encoding/fan-out and membership changes. Concurrent publishers have one lock-acquisition order shared by all active subscribers. This is process-local feed ordering, not an assertion of upstream durable event-sequence ordering. Queue offers never wait for a subscriber; publishers can contend for the short synchronous feed critical section. |
| Allocation scope | Skip encoding when no subscribers exist. Encode/frame once per publication and share the string. Replace `ConcurrentDictionary.Keys` snapshot enumeration with an owned list under the lock; reverse traversal permits removal without a snapshot. No pooling, SIMD, byte-budget change, or performance measurement claim. |
| Lifetime | `EventFeedService` now implements `IDisposable`: complete current subscriber writers and clear registrations under the same lock, with idempotent disposal. Subscribe/publish after disposal throw `ObjectDisposedException`; endpoint cleanup can still unsubscribe safely. The existing public interface and subscriber read/write methods remain unchanged. |

Build evidence: `dotnet build src/OpenCode.Server/OpenCode.Server.csproj --artifacts-path C:\tmp\opencode\dotnet-feed-build` completed successfully with **0 warnings and 0 errors**, Debug configuration, against the active working tree on 2026-08-30. Outputs and restore artifacts were directed to that isolated path. No tests were added, edited, or run; no application/API/database/process-control checks or commits were performed. This authorized build supersedes the earlier source-only restriction only for this feed implementation task, not the no-runtime rule.

### Encoding Failure Follow-Up

`EventFeedService.cs` now defines `SubscriberOverflowException` with `Capacity` and `EventFeedEncodingException` with `EventId`, `EventType`, and the original cause in `InnerException`. Both `Publish` and `PublishRaw` catch failures only around encoding/framing, complete all currently registered subscriber writers with the same encoding exception, clear the active registry, log the event identity/type and cause, and return without publishing the malformed frame. The service is not disposed or poisoned: later subscriptions can receive later valid events. The parameterless constructor remains available; ASP.NET DI can use the added logger constructor, while explicit parameterless construction uses `NullLogger`.

The existing lock covers encoding, failure fan-out, registry changes, and successful fan-out. Concurrent publication is ordered by lock acquisition, and a concurrent subscription joins either before the failed publication or after its registry reset. This is not durable Bus sequence ordering. No frame or raw envelope is built when there are no subscribers, and encoding remains once per publication, not once per subscriber.

Actual Effect Queue semantics were inspected in the local Effect source reference, `packages/effect/src/Queue.ts`: `offerUnsafe` rejects full dropping queues (`:694-711`); `failCauseUnsafe` enters `Closing` while buffered messages remain (`:924-939`); `takeUnsafe` returns those messages (`:1525-1541`); `releaseCapacity` finalizes failure once a closing queue empties (`:1885-1894`). Therefore **encoding failures, as well as overflow failures, preserve accepted backlog before failing the reader**. Writer completion with an exception is the .NET counterpart used here. This must not be confused with Effect `Queue.shutdown`, which discards buffered messages during scope cleanup. Current .NET unsubscribe/dispose still use normal channel completion plus endpoint cancellation, not an exact queue-shutdown primitive; cancellation may stop the reader before draining.

The complete upstream `packages/protocol/src/groups/event.ts` and `packages/schema/src/event-manifest.ts` were also inspected. The public predicate is backed by domain `ServerDefinitions`, not a small event-name list. The later typed-definition pass below replaces the constants-only situation with validated envelopes, typed codecs, and explicitly partial inventories; it still does not provide the complete public union. No guessed whitelist was added. Canonical manifest completion remains Schema/Protocol work, while wiring one scoped Core Bus listener remains Core/Server work.

Current build evidence for this follow-up: the same isolated command, `dotnet build src/OpenCode.Server/OpenCode.Server.csproj --artifacts-path C:\tmp\opencode\dotnet-feed-build`, passed in Debug with **0 warnings and 0 errors** on 2026-08-30 after the typed failure changes. This is a new compilation result, separate from the prior overflow-only build. No tests, runtime/API calls, database access, process control, benchmarks, or commits were performed.

Residual fidelity gaps: `PublishRaw` still uses object/anonymous-payload serialization; canonical public filtering and payload validation are absent. The feed is not backed by one Core Bus subscription and cannot certify durable order. Connection-local heartbeat/connected-frame fidelity belongs to the endpoint owner and is unchanged. Typed queue failures and encode-once/drain-before-failure behavior are implemented in source, not proven through runtime validation. Full event-stream parity remains unaccepted.

## Configuration Pass Record

The user coordination handoff identifies the bounded configuration pass as complete. Current source confirms the implemented scope; this records that pass, not full configuration/provider acceptance:

- `src/OpenCode.Core/Config/ConfigLoader.cs`, `LoadConfig`: shared XDG/OpenCode configuration roots, global JSON/JSONC, explicit file, ancestor files, `.opencode` files, content override, and project-disable handling. An explicit method argument retains isolated-file behavior. Discovery still uses the process current directory, not a completed location-scoped service graph.
- `ParseDocument`, `MergeDocument`, `MergeProvider`, and `MergeOverlay`: environment/file substitutions, selected legacy provider normalization, provider/model/variant overlays, atomic model selection, and explicit rejection of unsupported policies/mappings. This is not a complete upstream normalization or live-watch implementation.
- `src/OpenCode.Core/Llm/ProviderResolver.cs`, `ResolveAsync`: explicit/configured provider/model/variant selection, no silent substitute, selected transport-package checks, settings/body/header support checks, and explicit errors for unavailable/unsupported selections. Credential resolution uses supported explicit settings/environment only, not shared database access.
- `src/OpenCode.Schema/OpenCodeChannel.cs`, `SqliteDatabase` default path, and `ServiceDaemon.GetDefaultRegistrationFile`: channel `dotnet`, `opencode-dotnet.db`, and `service-dotnet.json` under upstream-style roots are present. Shared global/project configuration is retained. No production database copy or write is authorized.

Still unsupported or incomplete: OAuth/login/refresh and channel credential-store integration; built-in/remote provider catalog discovery; the full transport set and provider compatibility options; location-scoped reload/watch/domain composition; and end-to-end agent/session default selection. The inspected server prompt endpoint still injects hard-coded defaults before calling the resolver, so B4 remains open there. Do not restore secret-specific fallbacks to make unsupported configurations appear functional.

Build evidence is limited to the isolated Server compilation reported above, which also compiled its referenced Core/Schema projects. No separate configuration-owner build result or runtime configuration scenario is inferred from it. Configuration correctness, provider dispatch, OAuth/catalog behavior, channel lifecycle, and database safety remain distinct fidelity work requiring later authorized validation.

## Foundational Serializer Pass

The entire upstream `session.ts`, `session-message.ts`, `session-fork.ts`, `session-revert.ts`, `session-error.ts`, `token-usage.ts`, and `money.ts` were read, together with the relevant scalar, location, model, agent, prompt, snapshot, ID, metadata, and file-diff definitions. This is a coherent first serializer pass, not completion of those domains. Implementation is confined to the assigned Schema files and `Serialization/`, plus the authorized narrow `Location.cs` optional-workspace annotations. Config, Model, Core, and store implementation remain with their owners.

| Source contract | Implemented .NET boundary | Exact behavior and limit |
| :--- | :--- | :--- |
| `schema.ts`, `DateTimeUtcFromMillis`; session/message time fields | `Serialization/EpochMillisecondsJsonConverter.cs`, `SessionTime`, `MessageTime` | Read finite numeric milliseconds, truncate fractional milliseconds as JavaScript Date does, and write numeric epoch milliseconds instead of ISO text. Optional time fields omit null on write and reject an explicit null on read. `MessageTime.Streamed` is added as a trailing optional argument. DateTimeOffset cannot represent JavaScript's full date range; out-of-range values produce `JsonException`. |
| `token-usage.ts`, `Info` and cache | `TokenUsage.cs`, `Serialization/TokenUsageJsonConverters.cs` | Counts are `double`, matching finite numbers rather than integer-only `long`. Negative/fractional finite counts are not arbitrarily prohibited. Wire reads require input/output/reasoning/cache and both cache fields; missing/null/non-finite values fail. Convenience construction still defaults omitted counts/cache to zero values, never a null cache. Constructors/init setters and writes reject non-finite values. |
| `money.ts`, finite USD | `Money.cs`, `Serialization/FiniteNumberJsonConverter.cs` | Preserve the scalar JSON number and existing construction/conversion/init surface; reject NaN/infinity in construction, mutation, and wire paths. Do not add a nonnegative requirement absent from upstream. |
| `session.ts`, `Info` and outcome | `Session.cs`, `Serialization/SessionOutcomeJsonConverter.cs` | Required wire fields use `JsonRequired`; callbacks reject missing/null location, tokens, time, invalid null model members, and null fork boundary. Session-level agent/model remain optional as upstream specifies. Outcome accepts the exact documented strings, not numeric enum values or alternate casing. Legacy store conveniences `Slug`, `Directory`, and `Version` remain CLR properties/constructor arguments but are ignored on wire. |
| `session-message.ts`, assistant and location switch | `SessionMessage.cs` | Assistant `agent` and `model` are required on decode and validated before serialization, without adding compile-time required initializers to active callers. `LocationSwitchedMessage.Location` is now a `LocationRef`; `MessageLocation`, `Subpath`, and `Previous` model its destination/history envelope. Assistant structured error is added. Remaining variants/fields are not claimed complete. |
| `session-fork.ts`, durable versus request boundary | `SessionFork.cs` | Existing durable before/through boundaries require `messageID`. Separate `ForkRequestBoundaryBefore` and `ForkRequestBoundaryThrough` types reflect that request `through` has no message ID. Existing durable constructors are preserved. |
| `session-revert.ts`, current `Revert` | `SessionRevert.cs` | Required `messageID`, optional property omission. Does not implement legacy `PersistedRevert` migration or repair the separately owned FileDiff contract. |
| `session-error.ts`, `Error` | `SessionError.cs`, `Serialization/OptionalHttpStatusJsonConverter.cs` | Require type/message on JSON boundaries; optional status must be an integer in 100-599, with explicit null rejected and absent status omitted. Empty strings are not prohibited because upstream uses unrestricted strings. |
| Canonical JSON entry point and location | `Serialization/OpenCodeJsonContext.cs`, narrow `Location.cs` annotations | Register foundational roots and enable `AllowOutOfOrderMetadataProperties` so canonical polymorphic decoding does not require the type tag first. Optional workspace ID is omitted even under non-context HTTP/raw serializers. Separate HTTP serializer options still require owner review; enabling the context option does not configure every serializer in the process. |

The converters use typed `Utf8JsonReader`/`Utf8JsonWriter`, UTF-8 property comparisons, and generated context registration, without reflection-based factories, JSON DOM round trips, or per-field dictionaries. No throughput, allocation-count, or AOT-publish claim is made. Out-of-order metadata can require serializer buffering; accepting valid object key orders takes precedence over a speculative optimization.

### Caller Handoff

- `TokenUsageInfo.Input/Output/Reasoning` and `TokenCacheUsage.Read/Write` changed from `long` to `double`. Existing integer constructor arguments convert implicitly. Consumers assigning these properties to integer variables must choose conversion explicitly; no truncation was added to the wire contract.
- `LocationSwitchedMessage.Location` changed from `string` to `LocationRef`. Callers must construct a location object. No construction sites were found in the earlier source scan outside Schema; this remains a source API change.
- `SessionInfo` constructor shape is retained. Wire consumers must use `location.directory`; the ignored top-level `Directory`, `Slug`, and `Version` conveniences are not populated by JSON decoding. `Location` is now required by the wire boundary even though the optional constructor argument remains for source compatibility.
- Assistant construction still compiles without `Agent`/`Model`, but canonical serialization now fails until the actual selected values are present. The user routed engine/server assistant-agent fixes to the configuration owner. The storage owner's new canonical `OpenCodeJsonContext.Default.SessionMessage` path exercises this validation; do not add a fabricated agent or weaken it to accommodate incomplete callers.
- Store ownership is separate. The storage pass now uses canonical serialization and fuller mapped fields; that preserves the fields the schema actually represents, not fields still absent from `SessionMessage`. Direct projection-write/durable-Bus blockers are unchanged.

The later prompt and assistant contract passes supersede the earlier missing user-attachment, tool-state, assistant-metadata, shell-output, and compaction-field findings. Remaining fidelity work includes runtime verification, retained V1 revert decoding, date-range limits, separately owned nested contracts/ID entrypoints, and full event inventory/registration. The global context's null omission alone is not validation; the newer passes add property/list converters at their covered boundaries. No full SessionMessage, Session, or event-manifest runtime acceptance is assigned.

### Latest Build Evidence

`dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors** after the final scalar/init validation changes. The six intermediate `CS0657` token warnings were fixed by putting JSON attributes on the explicit properties instead of positional parameters.

The separate Server build at `C:\tmp\opencode\dotnet-feed-build` compiled Schema, Protocol, and Core but failed with `CS1061` at `src/OpenCode.Server/Endpoints/SessionEndpoints.cs:245`: a concurrent owner change references `OpenCodeConfig.DefaultAgent`, which was not present in that snapshot. Those files were not modified here. This later failed build must not be replaced by the earlier feed-only success when reporting current Server status. Owner reconciliation and a later build remain necessary; no test or runtime validation was performed.

## SSE Shutdown Follow-Up

Ownership was extended to `src/OpenCode.Server/Endpoints/EventEndpoints.cs` for one lifecycle fix. The handler now links request cancellation with `IHostApplicationLifetime.ApplicationStopping` and uses that linked token for the connected-frame write, stream reads, and response writes/flushes. The existing `finally` unregisters/completes the subscriber, and the linked token source is disposed with the request.

This makes an idle queue read cancellable during graceful host drain instead of waiting for feed-service disposal after Kestrel has already waited for the request. It does not dispose the shared feed in an application-stopping callback or introduce a race between early feed disposal and active requests. Application shutdown intentionally terminates request draining; normal overflow/encoding failure still drains accepted frames before surfacing its typed error when the request has not been cancelled. Connected-frame shape and periodic heartbeat fidelity are unchanged.

The isolated Server build attempt is recorded above and is currently blocked by the unrelated concurrent `DefaultAgent` compilation error. Stop latency, request cleanup, and failure draining are source-reviewed but not runtime-proven. No tests, runtime/API/DB calls, process control, benchmarks, or commits were performed.

## Inbox Schema Follow-Up

The complete upstream `packages/schema/src/session-inbox.ts` was compared with `src/OpenCode.Schema/SessionInbox.cs`. The relevant `session-event.ts` enqueue definition was also checked to distinguish the inbox read model from its durable event. Changes are confined to Schema inbox DTOs, supporting serializers, and context registrations; `Core/Event/SessionAdmission.cs` and storage code were read but not edited.

| Upstream contract | Canonical .NET surface | Scope |
| :--- | :--- | :--- |
| `SessionInbox.Item` | `InboxItem(Delivery, Payload)` | Non-enqueued object with top-level `type`, `payload`, and `delivery`. The type is derived from the concrete payload, so a caller cannot independently assign a conflicting discriminator. |
| `SessionInbox.Info`, four enqueued variants | Existing `SessionInboxItem(Id, SessionId, Delivery, Payload, TimeCreated)` | Existing name and five-argument constructor retained for admission callers. Emits the same item fields plus `id`, `sessionID`, and numeric epoch-millisecond `timeCreated`; no ISO timestamp. |
| `Enqueued` in `session-inbox.ts` | Fields on `SessionInboxItem` | Upstream defines this as shared fields, not another exported union or nested envelope. No duplicate public enqueued DTO was introduced. |
| User/synthetic/compaction/move payloads | Existing `InboxPayload` family | Concrete payloads no longer carry a discriminator. User/synthetic require non-null text at JSON boundaries. Compaction writes an empty payload object. Move requires a `LocationRef` and project ID, with optional subpath omitted. All four variants are supported by item encoding/decoding. |
| `Delivery` | `InboxDeliveryMode` and typed converter | Only exact `steer`/`queue` strings are accepted; numeric enum values, alternate casing, null, and unknown values are rejected. |

`Serialization/InboxJsonConverters.cs` dispatches from the outer type, uses generated concrete payload metadata, and rejects missing/null required envelope fields. It buffers one object with a disposed `JsonDocument` on read because the payload can precede its discriminator. It does not require property ordering, inject a payload type, or infer a variant from overlapping payload fields. The writer uses typed UTF-8 JSON primitives and does not encode separately for each field or build an intermediate DOM. The shared epoch converter now exposes an internal numeric conversion path reused by the inbox decoder, retaining its finite-number and DateTimeOffset-range checks.

### Admission Handoff

- Existing `new SessionInboxItem(id, sessionId, delivery, payload, created)` calls and `OpenCodeJsonContext.Default.SessionInboxItem` remain valid. `Type` is now available as a computed property on the DTO.
- `OpenCodeJsonContext.Default.InboxPayload` remains available for polymorphic **writing**, but writes a discriminator-free concrete object. The current `SessionAdmission` removal of `encoded["type"]` is now an unnecessary no-op and can be removed by its owner. Concrete payload type metadata remains registered.
- Bare abstract `InboxPayload` cannot be decoded without a known outer type; use the canonical `InboxItem`/`SessionInboxItem` converter, or concrete payload metadata as admission's `DecodePayload` already does. Do not restore nested discriminator support as a second public format.
- `MoveInboxPayload.Location` changes from `string` to `LocationRef`. This does not enable move admission/execution: the storage boundary intentionally supports prepared text-only user/synthetic input until the control lifecycle is implemented.
- The durable `session.inbox.enqueued` event is separately `{ sessionID, inboxID, item: SessionInbox.Item }`, not the enqueued read model. The later assistant/event contract assignment corrects the existing `SessionInboxEnqueuedEventData` to that shape, using canonical `InboxItem`; it does not add a competing public event envelope. The storage owner can adopt this public data contract when reconciling its internal event-data adapter.

The original inbox pass had incomplete Prompt dependencies; the subsequent prompt-contract pass below corrects their declared shapes and serializers without introducing duplicate attachment models. This still does not establish runtime attachment preparation, complete ID validation outside the Prompt boundary, durable admission semantics, or event-manifest coverage. The admission owner must separately review its unsupported-attachment guard and preparation requirements; a Schema pass alone is not permission to remove lifecycle safeguards.

Build evidence: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors** after this change. No Core/Server build is claimed for this inbox-only follow-up. No tests were added/edited/run; no runtime, API, database, process-control, benchmark, or commit actions were performed. Source-reviewed structure and compilation are not a runtime round-trip or full inbox fidelity proof.

## Prompt Contract Pass

The complete upstream `packages/schema/src/prompt.ts` and `prompt-input.ts` were read, along with `skill.ts`, the model reference contract, and the relevant session request/preparation boundaries. All declared fields, unions, validation rules, and explicit helpers from those two prompt modules now have concrete .NET mappings. This describes source implementation coverage, not runtime acceptance or a completed session engine.

| Upstream symbol | .NET mapping | Implemented contract |
| :--- | :--- | :--- |
| `PromptMention` | `PromptMention` | Required finite `double` start/end and required text. No invented integer, nonnegative, ordering, or text-offset-bound checks. |
| `FileSource` | `PromptFileSource`, `PromptInlineFileSource`, `PromptUriFileSource` | Tagged `inline` with no fields, or `uri` with required string `uri`. Canonical context accepts discriminator metadata after ordinary properties. No URI parsing or absolute-URI restriction is added to a schema that declares a string. |
| `Base64` | `PromptBase64.IsValid/Create`, `PromptBase64JsonConverter` | Span-based standard base64 alphabet and quartet/padding grammar. Empty string is allowed; whitespace, URL-safe alphabet, misplaced padding, and incomplete quartets are rejected. No decode/re-encode, padding insertion, trimming, or stricter pad-bit canonicalization. |
| `FileAttachment` and `create` | `PromptFileAttachment` and `Create` | Required `data`, `mime`, and `source`; optional name/description/mention. Create projects exactly those fields into a validated record. Source is never fabricated as an inline default. |
| `AgentAttachment` | Shared `PromptAgentAttachment` | Required `name`, optional mention; the old `agent` property is removed. Shared by prepared and input prompts. |
| `SkillAttachment` | `PromptSkillAttachment` | Required `id` and `name`; optional text and mention. Skill ID/name retain upstream unrestricted-string meaning: no new prefix or nonempty checks. |
| `Prompt`, `equivalence`, `fromUserMessage` | `Prompt`, `Equivalence`, `FromUserMessage` | Required text; optional prepared file/agent/skill arrays. Equivalence compares ordered attachment contents structurally, not list identity. FromUserMessage selects only text/files/agents/skills, not message identity/time/metadata. Omitted arrays stay omitted; empty arrays stay present. |
| `PromptInput.FileAttachment` and `create` | `PromptInputFileAttachment` and `Create` | Required URI string and optional name/description/mention; no prepared data/mime/source fields. |
| `PromptInput.SkillAttachment` | `PromptInputSkillAttachment` | Required skill ID and optional mention; name/text are produced later by Core preparation, not defaulted in Schema. |
| `PromptInput.Prompt` | `PromptInput` in `PromptInput.cs` | Required text; optional URI-file, shared agent, and input-skill arrays. This replaces the old conflation of prepared prompt and admission settings. |

Neither source module declares a `resume`, `delivery`, model-selection field, text default, attachment default, or URI-to-base64 transform. `packages/protocol/src/groups/session.ts:338-346` adds request ID, metadata, delivery, and resume around `PromptInput.Prompt`; its response is an admitted inbox user item. No request endpoint or Protocol DTO was changed here. `packages/core/src/session/prompt.ts`, `prepare`/`materializeAttachment`, owns plugin hooks, URI reading, byte limits, MIME/image normalization, skill lookup, and conversion to prepared `Prompt`. None of that runtime behavior is claimed by these schema helpers.

### Serialization Boundaries

`Serialization/PromptJsonConverters.cs` supplies typed property-level non-null and attachment-list converters backed by `OpenCodeJsonContext` metadata, without reflection-based converter factories. Required null values fail; present optional null values fail instead of being treated as omission. Missing optional properties remain absent on write. Attachment arrays reject null elements, preserve order and empty arrays, and use the same concrete types throughout.

`UserMessage.Files` is now `IReadOnlyList<PromptFileAttachment>` rather than string paths; `Agents` and `Skills` were added as typed arrays. `UserInboxPayload` uses those same prepared attachment contracts and list converters. The storage owner's canonical serialization can now preserve the modeled source/name/text/mention fields without anonymous replacement objects. This does not itself change the current admission feature guard or provide preparation/execution.

Required prompt values validate through constructors/init setters and JSON properties. Optional values use `null` as the in-memory absence representation, but the JSON converters distinguish missing keys from explicit null input. Base64 checking walks the existing character span without allocating decoded bytes; JSON strings and arrays still require normal managed storage. There are no benchmark, allocation-count, zero-reflection-process, or AOT-publish claims.

### Caller Changes

- New `Prompt` is the prepared canonical type. `PromptInput` is now the unprepared input type and no longer has `Resume`; it uses `PromptInputFileAttachment` and `PromptInputSkillAttachment`. The source scan found no caller constructing the previous conflated DTO, so no speculative compatibility overload was added.
- `PromptFileAttachment` now requires `Source` as its third constructor argument. `PromptAgentAttachment.Agent` becomes `Name`. `PromptSkillAttachment.Skill` becomes required `Id`, plus required `Name` and optional `Text`.
- `PromptMention.Start`/`End` change from `int` to `double`; existing integer constructor arguments convert implicitly. Do not truncate fractional positions in serialization.
- `UserMessage.Files` accepts structured prepared attachments, not string paths. The new agent/skill arrays are optional. No Core, Server, configuration, or model implementation files were edited.
- Owners must use the canonical context or equivalent configured JSON metadata for order-independent union decoding. Standalone abstract source/payload decoding without a discriminator is not a substitute for a canonical envelope.

Remaining dependencies are runtime prompt preparation, Protocol request composition, admission feature support, and verification of the subsequently expanded message contracts. As with other reference DTOs, callers deserializing a top-level prompt or attachment must reject a null root result; property/list converters enforce non-null values inside the mapped object but do not change System.Text.Json's root-reference null behavior. Broad Skill/Model/provider APIs remain separately owned; neither prompt schema has a model attachment field to invent. Structural and JSON-helper implementations require later authorized round-trip/equivalence validation before fidelity acceptance.

Build evidence: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed after this prompt pass in Debug with **0 warnings and 0 errors**. This is Schema-only evidence, not a new Core/Server or runtime result. No tests, runtime/API/database/process actions, benchmarks, or commits were performed.

## Assistant Contract Pass

Read the full upstream `session-message.ts`, `tool.ts`, `llm.ts`, and `session-event.ts`, plus the complete `shell.ts` dependency. Implemented the declared message shapes and the bounded assistant event-data family in `SessionMessage.cs`, `Tool.cs`, `SessionEvent.cs`, and supporting source-generated serialization boundaries. Config/Model and Core/projector files were not edited. The storage agent's assistant projector and internal encoded-data validator remain independently owned and in flight.

### Message Coverage

| Upstream boundary | .NET symbols and behavior |
| :--- | :--- |
| `AssistantContent` | Exactly `AssistantTextContent`, `AssistantReasoningContent`, and `AssistantToolContent`. No standalone tool-call/result message variants were added. Text and reasoning include optional provider `state`; reasoning also has optional created/completed time. |
| `ToolState` | Streaming string input; running object input plus **required** metadata object; completed object input plus nonempty `ToolContent` array; error object input plus structured `SessionStructuredError` and optional nonempty content. Completed/error metadata remains optional. Null entries and empty terminal content arrays are rejected. |
| `Tool.Content` | `ToolTextContent(text)` and `ToolFileContent(uri, mime, name?)`, with required string fields and optional name omission. These are wire content, not the generic runtime tool-result algebra. |
| `AssistantTool` | Required id/name/state and `AssistantToolTime(created, ran?, completed?)`; optional executed/providerState/providerResultState. `executed: false` is retained, not treated as absence. State maps use `IReadOnlyDictionary<string, JsonElement>` at the JSON boundary, not serialized CLR object reflection. |
| `Assistant` | Required agent/model/content; optional snapshot, finish, rawFinish, providerState, cost, tokens, error, and retry. Snapshot has independent optional start/end/files. Finish fields use the exact six `LlmFinishReason` strings. Empty assistant content is valid, unlike terminal tool content. |
| `AssistantRetry` | Positive-integer attempt, epoch-millisecond At, and required structured error. Attempt uses a validated double to retain the upstream JavaScript numeric-integer domain instead of adding an int32 bound. |
| Message time shapes | Existing `MessageTime` CLR constructor is retained. Base messages encode only created; shell and reasoning encode created/completed; assistant encodes created/streamed/completed; tool content has created/ran/completed. Contextual converters discard irrelevant time fields rather than emit them in the wrong variant. All time fields are numeric milliseconds. |
| Shell message | Exact status strings, `sh_` identifier validation, numeric optional exit, and structured `ShellOutput(output, cursor, size, truncated)` with nonnegative-integer cursor/size. The subsequent terminal-contract pass moves this shared output type into Shell.cs and completes ShellInfo/inputs/events, without implementing the Shell runtime. |
| Compaction | Abstract `CompactionMessage` with running/completed/failed records. All require auto/manual reason; running/completed require summary/recent; failed requires structured error. Type and status cannot disagree with the selected concrete variant. |
| Message union | `SessionMessageJsonConverter` dispatches all current variants, including both compaction tags, using a copy of `Utf8JsonReader`. No JSON DOM is allocated for this discriminator probe and no property order is required. Concrete message JSON also requires/checks its type; standalone compaction decoding reuses the same dispatch. Base validation enforces the upstream `msg_` prefix and required time. |

`AssistantContentEncoded` needs no duplicate .NET DTO: these content types already encode their time fields numerically. Optional null values are omitted on writing and rejected by the covered property converters on reading. A JSON dictionary preserves arbitrary JSON values, including null values inside a map; it is not a claim to represent every non-JSON value allowed by an upstream runtime `Schema.Unknown`.

### Event Data Coverage

These are public **data** contracts, not new event envelopes or a replacement durable registry. Core still owns event IDs, created timestamps, versions, aggregate sequence, publication, and projections.

| Event family | Canonical data DTOs |
| :--- | :--- |
| Step | `SessionStepStartedEventData`, `SessionStepStreamedEventData`, `SessionStepEndedEventData`, `SessionStepFailedEventData`. Ended requires finish/cost/tokens; failed requires error, has optional usage, and only permits optional finish `content-filter`. Snapshot/files/providerState/rawFinish follow the source. |
| Text/reasoning | `SessionTextStartedEventData`, `SessionReasoningStartedEventData`, shared `SessionContentDeltaEventData`, and shared `SessionContentEndedEventData`. Ordinal is a nonnegative integer. Delta and ended retain distinct delta/text fields; reasoning start can carry state. Identical text/reasoning data shapes share a type rather than duplicate DTOs. |
| Tool input | `SessionToolInputStartedEventData`, `SessionToolInputDeltaEventData`, `SessionToolInputEndedEventData`. They carry canonical session/assistant/tool identity and name, delta, or text respectively. |
| Tool execution | `SessionToolCalledEventData`, `SessionToolProgressEventData`, `SessionToolSuccessEventData`, `SessionToolFailedEventData`. Called/success/failed require executed; progress requires metadata. Success content is nonempty; failed content is optional but nonempty when present. Event `state`/`resultState` are intentionally distinct from projected message `providerState`/`providerResultState`. |
| Retry/content replacement | `SessionRetryScheduledEventData` stores positive attempt and nonnegative numeric `at`, unlike projected `AssistantRetry.At`'s DateTimeOffset representation. `SessionMessageContentUpdatedEventData` carries the full typed assistant content array, including numeric nested times. |
| Previously incomplete wrappers | `SessionCreatedEventData` gains required location and optional subpath. `SessionInboxEnqueuedEventData` is corrected to sessionID/inboxID/item; `SessionInboxDeliveredEventData` uses inboxID. Other event families/legacy wrappers are not certified by this pass. |

The inspected source marks text/reasoning/tool-input deltas and tool progress as ephemeral. Tool success/failed are durable version 2; the other listed durable assistant step/text/reasoning/tool-input/called/retry/content-replacement events use version 1. These distinctions are handoff requirements for the storage owner, not registration behavior supplied by the DTO declarations.

### Breaking Fields

- `ToolStateRunning` now requires metadata as its second constructor argument. Input/metadata/provider-state maps use `JsonElement` values, not `object`.
- `ToolStateCompleted.Content` and optional `ToolStateError.Content` are typed nonempty `ToolContent` lists; `ToolStateError.Error` is `SessionStructuredError`, not a string.
- `AssistantToolContent` requires `AssistantToolTime` as its fourth argument. Text/reasoning single-string constructors remain valid; new state/time arguments are optional.
- `CompactionMessage` is abstract; construct `CompactionRunningMessage`, `CompactionCompletedMessage`, or `CompactionFailedMessage`. Shell output changes from string to `ShellOutput`, and exit from nullable int to nullable double.
- Existing assistant construction remains source-compatible and still requires valid agent/model at the serialization boundary. `MessageTime` keeps its constructor shape, but context-specific writers now omit fields not present in that variant's upstream contract.
- Created/enqueue/delivered event-data signatures change as listed above. No Core construction sites for these old Schema DTOs were found in the final source scan; concurrent owners must still reconcile their adapters.
- Core's transport `LlmFinishReason` and Schema's wire `LlmFinishReason` are distinct enums with different member ordering. Map by meaning/name, not an ordinal cast. No Config/Model or transport enum file was changed.

Residual fidelity work: adopt these contracts in the separately owned event registry/projectors and runtime runner, validate the remaining SessionEvent families, implement the generic Tool runtime input/output/options algebra where owned, and resolve shared nested contract/ID entrypoint gaps. The full `tool.ts` runtime API is not ported merely because its `Content` union is. DateTimeOffset retains its narrower range than JavaScript Date. Top-level reference DTO null handling outside the canonical SessionMessage/Compaction converters remains caller-owned. No full event manifest, runtime fidelity, or end-to-end session completion claim is made.

Final build: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors** after the discriminator checks. No Core/Server build or runtime result is inferred. No tests, application/API/DB/process actions, benchmarks, or commits were performed.

## Typed Event Definitions

Read the entire upstream `event.ts`, `event-manifest.ts`, and `durable-event-manifest.ts`, plus the already mapped Session families and relevant worktree/status definitions. Added pure Schema metadata/codecs in `EventDefinition.cs`, source-backed selected definitions in `SessionEventDefinitions.cs`, and explicit partial inventories in `EventManifest.cs`. No Core/Server dependency, observer, service layer, projector, filesystem access, or persistence mechanism was added to Schema.

### Integration API

- `DurableEventDefinition<TData>` and `EphemeralEventDefinition<TData>` expose immutable `Type`, `Identifier`, `Durability`, typed `DataType`, and durable metadata (`Version`, `Aggregate`) where applicable. Each uses its generated `JsonTypeInfo<TData>`, not an arbitrary object payload or reflection-based factory.
- `DecodeData(JsonElement)` validates the data object through its concrete codec. `Decode(OpenCodeEvent)` additionally checks the envelope and literal type/version, returning typed data. `Validate` performs those checks without changing the input; `Normalize` re-encodes the declared data fields and drops the durable field for an ephemeral definition.
- `Read(ReadOnlySpan<byte>)` reads one complete UTF-8 event, validates it against the definition, and normalizes it. Its ephemeral path treats a durable field as excess input, matching upstream struct normalization even if that excess value is malformed. The generic untyped envelope decoder validates a durable field when one is supplied because it has no selected durability contract.
- Durable `Create(id, created, data, sequence, location?, metadata?)` validates data, selects the string aggregate field from encoded data, and supplies the definition's version. Ephemeral `Create` has no sequence/durable argument. Neither generates clocks/IDs nor writes or publishes anything. Core must continue to allocate sequence and execute projectors atomically.
- `AggregateId(encodedData)` exposes the durable publication-key selection. General envelope decoding checks aggregateID's string shape but does not invent a cross-field equality rule absent from `event.ts`; the durable creation helper derives it consistently.
- `EventDefinitions.Inventory` returns a copied read-only list. `Latest` preserves insertion order, selects higher durable versions, permits repeated identical definition references, and rejects different definitions competing for the same latest type/version. `DurableMap` excludes ephemeral definitions and rejects every duplicate versioned key, even a repeated identical reference. `VersionedType` produces the type/version key with culture-independent numeric formatting.

`OpenCodeEvent` remains the single generic encoded envelope. Its native reader/writer validates required id/type/created/data, exact `evt_` ID prefix, finite created values, object data, optional metadata object, typed `LocationRef`, and durable aggregate/sequence/version fields. Sequence is an integer at least zero and version an integer at least one. Optional null envelope fields are rejected rather than silently treated as absent. Location directory stays an unrestricted branded string; no path normalization or extra absolute-path restriction is introduced. The reader uses UTF-8 primitives and retains only the payload/metadata JSON values, not a second whole-envelope DOM.

`EncodeData` includes a generated-code decode check after encoding so a manually constructed DTO cannot evade required-field validation merely because null output was omitted. This is a correctness cost, not a measured optimization. Future optimization must retain the same checks; no throughput or allocation-count claim accompanies the API.

### Partial Membership

`SessionEventDefinitions` covers creation, inbox enqueue/delivery, step, text, reasoning, tool input/called/progress/success/failed, retry scheduling, message-content replacement, and internal usage recording. Source versions and aggregate metadata are explicit. Usage recording is intentionally excluded from the implemented public list and included in the durable map.

`EventManifest.IsPublicInventoryComplete`, `IsSharedInventoryComplete`, and `IsDurableInventoryComplete` are all **false**. Public lookup remains `ImplementedPublicDefinitions`/`ImplementedPublicLatest`; the subsequent family pass adds broader `ImplementedDefinitions`/`ImplementedLatest` alongside `ImplementedDurable`. There is deliberately no `IsServer` whitelist: a missing key means unreviewed coverage, not private/invalid data. Existing `EventTypes` constants remain conveniences for callers and are no longer described as a manifest.

The later domain, Form, terminal, and compaction passes add the simple foundation/feature events, canonical Project data, Worktree resolution, installation/VCS, exact MCP/server membership, Form events, all Shell/PTY/persistent-PTY domain events, and the current compaction transitions. Remaining public coverage still includes other Session lifecycle/selection/instruction/revert families, filesystem, permissions, plugins, web search, Session status, and TUI families. Remaining durable coverage is in Session; `Worktree.Event.Resolved` is now included. Connection-local `server.connected` is additionally composed by Protocol and must not be confused with the narrower Schema public manifest. Feed filtering remains unchanged.

### Required Data Fixes

`SessionCreatedEventData` now marks sessionID/projectID/slug/version/location required and validates identity, required strings, model references, location, and optional parent on both decode and encode. Inbox enqueue/delivery now require sessionID as well as inboxID, and callbacks reject default structs; enqueue also rejects a missing item. Content replacement validates its session/message identities. Thus `EventDefinition.DecodeData` no longer accepts the reviewed missing-sessionID case by constructing a default ID. Publication validation is not the only guard.

### Core Handoff

- `OpenCodeEvent.Created` changes from `long` to finite `double`; `DurableEnvelope.Seq` and `Version` change to validated numeric integers represented by `double`. Existing integer constructor arguments convert implicitly, but readers must explicitly reconcile integer-only APIs. This matches the wire number domain without silently truncating fractions or imposing int32 bounds.
- `OpenCodeEvent.Location` changes from string to `LocationRef`. Envelope IDs now enforce the source `evt_` prefix.
- Source inspection identifies required integer-boundary adaptations in `SessionAdmission` and `SessionInboxOperations` calls to `DateTimeOffset.FromUnixTimeMilliseconds`, and the assistant projector's integer timestamp argument. Core must select appropriate checked/range-aware conversions or representations; these files were not edited and no Core build was run for this pass.
- The existing Core internal `DurableEventDefinition<T>` includes a projector callback and remains independently owned. It can compose a Schema definition plus its projector rather than maintaining a second type/version/data contract. Qualify the namespace while both classes exist; Schema definitions never acquire projector callbacks.
- Known-definition feed validation can use `Validate`/`Normalize`, but the incomplete lookup cannot become a rejection/filter policy for other current event types. `PublishRaw` and full canonical feed integration remain separate work.

Remaining shared-ID entrypoint differences still limit exact acceptance for some nested values; source-backed field/definition coverage does not certify every dependent validator or runtime path. No complete public/durable inventory or Bus parity is claimed.

## Credential Configuration

Read the complete upstream `credential.ts` and `form.ts`. `CredentialKey.Configuration` now maps optional `configuration` to the canonical `FormAnswer`, so typed credential import/serialization no longer lacks a field to retain it. The key constructor keeps its existing key/metadata arguments and adds configuration as a trailing optional argument. No provider alias, credential selection, or configuration precedence code changed.

`FormAnswer` is a read-only string-keyed map of `FormValue`, not `object` or extension data. The value union has `Text`, finite `Number`, `Boolean`, and `Strings` variants. JSON encoding emits those native primitives/arrays, without a tag wrapper. Empty maps, empty strings/arrays, false, zero, and fractional/negative finite numbers are retained. Null values, objects, mixed/non-string arrays, and non-finite wire numbers are rejected. Map keys preserve case and spelling; duplicate JSON properties follow last-value object parsing. Constructor copies prevent callers from mutating supplied map/array containers afterward.

Optional configuration is omitted when absent, while `{}` remains present; explicit JSON null is rejected. Generated metadata registers `CredentialValue`, `CredentialKey`, `FormAnswer`, and `FormValue`, with typed streaming converters. This initial answer/value pass is followed by the Form-contract pass below; runtime conditional visibility, answer settlement, and OAuth lifecycle remain outside Schema. Core's existing raw credential validation and provider alias identity remain unchanged.

## Session ID Compatibility

The narrow `Ids/Identifiers.cs` fix now accepts the upstream legacy prefix `ses`, not only generated prefix `ses_`. `Prefix` and generated ID format remain `ses_...`. No other shared validator was tightened to a generated prefix.

The later domain-event pass corrects Project/Credential/Integration ID string acceptance because those scalars are required by its exact event payloads. The generic MessageId/FormId constructor prefix gaps and Session ID generation ordering remain separate follow-up contracts. No loose validator was tightened merely to match its generated prefix.

Final build for the typed event, credential configuration, required-data, and Session ID changes: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors**. This is Schema compilation evidence only. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed; no other owner's server integration files were edited.

## Domain Event Families

The current family pass reread the full public/shared and durable manifests and each listed domain's complete source module before binding its events. `DomainEventDefinitions.cs` now binds exact data contracts, generated metadata, stable source event types/identifiers, and durability. This adds actual contract coverage; it is not a guessed whitelist and does not claim the corresponding runtime domain is implemented.

| Source family | Added definitions and payload | Membership |
| :--- | :--- | :--- |
| `models-dev.ts` | `models-dev.refreshed`, empty object | Public and shared; ephemeral |
| `catalog.ts`, `agent.ts`, `config.ts` | `catalog.updated`, `agent.updated`, `config.updated`, each empty object | Public and shared; ephemeral |
| `credential.ts` | `credential.updated`, empty object; `credential.switched`, required integrationID and required nullable credentialID | Public and shared; ephemeral |
| `integration.ts` | `integration.updated`, empty object | Public and shared; ephemeral |
| `project.ts` | `project.updated`, canonical `ProjectInfo` fields directly, not an info wrapper | Public and shared; ephemeral |
| `worktree.ts` | `worktree.updated`, required projectID; `worktree.resolved`, required projectID/directory/previous and optional adopted project-ID array | Both public and shared; updated ephemeral, resolved durable version **1**, aggregate field **projectID** |
| `skill.ts`, `command.ts`, `reference.ts` | `skill.updated`, `command.updated`, `reference.updated`, each empty object | Public and shared; ephemeral |
| `installation-event.ts` | `installation.updated`, `installation.update-available`, each required version string | Public and shared; ephemeral |
| `vcs-event.ts` | `vcs.branch.updated`, optional branch string | Public and shared; ephemeral |
| `mcp-event.ts` | `mcp.status.changed`, `mcp.resources.changed`, `mcp.tools.changed`, each required server string | Status/resources public and shared; tools shared-only. All ephemeral |
| `server-event.ts` | `server.connected`, `global.disposed`, each empty object | Shared Schema inventory only; ephemeral. Protocol separately permits connected in its stream contract |

Every new definition's `Identifier` is its exact source event type because these modules do not supply an identifier override. Empty payloads reuse `EmptyEventData`; installation events share one version DTO and MCP events reuse their identical server-string DTO. No anonymous dictionary substitutes for a domain payload. Required fields, null behavior, and array contents are validated through the bound codecs.

### Project And Identity

`ProjectInfo` now validates required id/canonical/time/sandboxes, optional icon/commands/name/vcs, the exact lowercase VCS identifier grammar, and optional-key omission. `ProjectTime` uses nonnegative-integer double values for created/updated/initialized, without an invented int64 cap. Empty sandbox arrays and unrestricted branded path strings remain valid. The event uses this canonical DTO instead of a duplicate project-update shape. Worktree resolution uses typed project IDs and an optional typed array that preserves order, duplicates, and empty arrays.

Project, Credential, and Integration ID constructors/converters now accept arbitrary non-null strings as their actual source brands do, including empty/whitespace strings. Generated ID formats were not changed. Null/default struct IDs are still rejected at serialization/required DTO boundaries. In particular, `credential.switched` distinguishes a missing credentialID (invalid) from `credentialID: null` (valid and always emitted); it does not omit that required nullable key under global null-omission settings.

### Inventory Separation

`EventManifest` keeps separate implemented public and broader shared lists, retaining the relative source inventory order. The shared list adds MCP tools and server lifecycle events; it is not reused as the public list. The durable map appends `WorktreeEventDefinitions.Resolved` after the implemented Session durable definitions, matching the two source manifest owners. Internal `session.usage.recorded` remains durable-only and is absent from both public and broader shared lists.

All three completeness flags remain false. Public gaps are the remaining Session families plus filesystem, permission, plugin, web search, Session status, and TUI payloads/definitions. Broader shared coverage additionally lacks LSP, retained legacy/filesystem-V1 events, workspace events, and the separate transitional `worktree-event.ts` family; the shared session.compacted notification is now included by the compaction pass. That transitional worktree module is not the implemented current `Worktree.Event` family from `worktree.ts`. No missing type is classified as private or rejected through this subset, and no feed filtering changed.

The simple MCP event family is represented in full because its source payloads contain only a server string. The subsequent MCP/Integration pass adds the richer server/status/resource/configuration/method contracts, but not OAuth or transport execution. Empty invalidation events alone were never treated as proof that those domain DTOs or runtimes were complete.

### OAuth DTO Correction

`CredentialOAuth` now requires non-null methodID, refresh, and access strings and a required nonnegative-integer expiry. Empty strings remain valid, matching `Schema.String`; there is no trimming, secret-specific fallback, or generated-prefix requirement for method IDs. Expires is double-backed to preserve the source numeric-integer domain. Missing JSON fields, null strings, negative/fractional/non-finite expiry, and invalid constructor/init values are rejected. Optional metadata is omitted when absent and rejects explicit null. Generated metadata explicitly registers the OAuth type.

This closes the reviewed typed OAuth contract gap, not OAuth authorization/refresh behavior. Core raw credential validation and provider alias identity were not modified.

### Handoff And Evidence

- New family APIs are `<Domain>EventDefinitions` with named definitions and a read-only `Definitions` list; they use the existing `EventDefinition<TData>` validation/creation APIs. The metadata definitions contain no Core callback or service reference.
- `ProjectTime` numeric properties and `CredentialOAuth.Expires` change from integer CLR types to validated double values. Existing integer constructor arguments convert implicitly; integer-only consumers must convert deliberately.
- The obsolete `ServerConnectedEventData(version?)` approximation is replaced by shared `EmptyEventData`. No application construction sites for the old type were found in the source scan. Server endpoint/connection-frame behavior was not changed in this task.
- Project/Worktree runtime APIs, worktree adoption logic, runtime Form behavior, and complete public/shared inventories still require their own source-backed implementation and validation. No broad domain or runtime completion label is assigned.

Final isolated Schema build: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors** after the final ProjectTime validation change. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. No Core/Server build result or measured performance is inferred from this compilation.

## Connection Frame Correction

Read the complete `packages/protocol/src/groups/event.ts` and `packages/server/src/handlers/event.ts`. `EventEndpoints.cs` now emits the connection-local frame `{ id, type: "server.connected", data: {} }`, with a fresh `EventId.Create()` for each connection, using explicit `OpenCodeJsonContext.Default.ServerConnectedFrame` metadata.

`ServerConnectedFrame` is an outgoing Protocol frame DTO, separate from generic `OpenCodeEvent` and from the shared `ServerEventDefinitions.Connected` definition. It has no `created`, `durable`, or version field; none is fabricated for the special handshake. Registration still occurs before writing the connected frame and before draining live events. The frame is not published through the feed and does not consume its bounded lag budget.

The linked request/application-stopping cancellation token, response flushes, and `finally` unsubscribe are preserved. No schema-inventory completeness flag or feed filter changed. Periodic heartbeat and remaining HTTP stream/header fidelity remain separate work; this correction does not imply full SSE parity.

The isolated command `dotnet build src/OpenCode.Server/OpenCode.Server.csproj --artifacts-path C:\tmp\opencode\dotnet-feed-build` compiled Schema and Protocol, then failed with **CS0260** at `src/OpenCode.Core/Llm/ProviderResolver.cs:20`: another partial declaration exists but this declaration lacks `partial`. The provider file is owned by another agent and was not changed here. This is the latest Server build attempt for the frame correction, not a successful Server verification. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed.

## Form Contract Pass

Read all of `packages/schema/src/form.ts` and `packages/protocol/src/groups/form.ts`. `Form.cs`, `FormFields.cs`, and `FormEventDefinitions.cs` now represent all declared field, condition, option, info, state, reply, and event shapes, plus the source Protocol create payload. This is contract implementation coverage with build-only evidence, not a form engine, console authorization flow, or permission UI completion claim.

| Source symbol | .NET mapping and validation |
| :--- | :--- |
| `Form.ID`, `create(id?)` | `FormId` requires the exact `frm_` prefix. Existing `Create()` remains; `Create(string? id)` validates a supplied ID or generates only when null. Empty supplied IDs are not silently replaced. Default struct IDs fail at JSON/required-parent boundaries. |
| `Metadata` | Optional `IReadOnlyDictionary<string, JsonElement>` on info/create contracts. Arbitrary host-supplied keys and JSON values are preserved; no built-in metadata whitelist, default values, or runtime interpretation is added. |
| `Option` | `FormOption` requires value and label strings, with optional description. Empty strings are valid, and no label fallback is invented. Its constructor argument shape is retained. |
| `When` | `FormWhen` requires key, exact eq/neq op, and `FormConditionValue` string/number/boolean. Arrays, objects, and null are not condition values. This distinct scalar union does not weaken the existing `FormAnswer` value API. |
| Common fields | `FormField` owns required key and optional title/description. `FormInputField` adds optional required/when only for answerable fields. No required/custom defaults are fabricated. |
| `StringField` | `FormStringField`: optional format limited to email/uri/date/date-time; nonnegative-integer minLength/maxLength; pattern, placeholder, string default, options array, and custom flag. |
| `NumberField`, `IntegerField` | `FormNumberField` and `FormIntegerField` share optional numeric minimum/maximum/default. Integer-field descriptors deliberately allow fractional bounds/defaults because the source uses `Schema.Number`, not `Schema.Int`. |
| `BooleanField` | `FormBooleanField` with optional boolean default; false is preserved rather than omitted. |
| `MultiselectField` | `FormMultiselectField` requires an options array, which may be empty. Optional minItems/maxItems are nonnegative integers; custom and string-array default are optional. Defaults preserve order, duplicates, and empty arrays. |
| `ExternalField` | `FormExternalField` has required key/url and optional title/description only. It does not inherit required/when or other answerable-field properties. URL remains a string, not an invented URL-scheme validation. |
| `Fields`, `Info` | `FormFieldsJsonConverter` enforces a nonempty field array with no null elements. `FormInfo` requires id/sessionID/title/fields and optional metadata. SessionId is a string, including the upstream temporary MCP `global` owner, not a branded SessionId. |
| `State`, `Reply` | `FormPendingState`, `FormAnsweredState(answer)`, `FormCancelledState` form the status-tagged union. `FormReply` requires the existing typed `FormAnswer`. No settlement side effects occur in these DTOs. |
| Protocol `CreatePayload` | `FormCreatePayload` has required title/fields, optional id/metadata. Session identity remains a route parameter; no extra session field or default ID generation is added to the body contract. |

### Numeric Semantics

Length/item constraints use double-backed nonnegative integers without an added int32 cap. Numeric field descriptors use the wider source `Schema.Number` domain. The local Effect reference explicitly documents its JSON number mapping (`Schema.ts:3027-3035`, `SchemaAST.ts:2777-2788`): finite values stay numeric and non-finite values use named strings. The new optional numeric-field converter follows that mapping and does not coerce arbitrary numeric strings.

Condition values preserve their string-first union: a literal string such as a named non-finite number stays a string when read as a condition. Non-finite numeric condition serialization therefore has the source union's overlapping string representation and is not claimed to provide a separately proven lossless type round trip. The existing finite-number `FormAnswer`/credential-configuration API is intentionally unchanged from the earlier explicit requirement; this remains a narrower in-memory answer-number domain than raw `Schema.Number`. No runtime transport equivalence is inferred for these non-finite cases.

### Runtime Boundary

The source comments define AND across when conditions, multiselect eq/neq as membership/non-membership, unanswered references as false for both operations, references to earlier fields, and inactive fields as neither required nor answerable. Those semantics are recorded for the runtime owner; no visibility engine, reference graph, answer matching, or field-settlement service was added to Schema.

In particular, these constructors do not add minimum-less-than-maximum checks, default-within-range checks, option-membership/uniqueness checks, regex compilation, format validation of a default string, key uniqueness, or cross-field reference ordering not enforced by the source schema itself. Such runtime validation requires its own source mapping. Metadata is opaque and echoed, not interpreted as authorization or UI policy here.

### Form Events

`FormEventDefinitions.Created`, `Replied`, and `Cancelled` bind generated codecs with exact types/identifiers `form.created`, `form.replied`, and `form.cancelled`. All are ephemeral and are now in both implemented public and shared feature inventories. Created data is `{ form: FormInfo }`; replied is `{ id, sessionID, answer }`; cancelled is `{ id, sessionID }`. No durable version or aggregate was invented, and the durable inventory is unchanged. All global completeness flags remain false for the other missing families.

### Caller Handoff

`FormAnswer`, its primitive value types, and `CredentialKey.Configuration` are preserved. Existing `FormOption(value, label, description?)` and parameterless `FormId.Create()` calls remain valid, with required-value and ID-prefix validation now enforced. The added records have no prior construction sites to migrate. Use canonical `FormField` and `FormState` generated metadata when serializing their unions so discriminators are included; enclosing info/create/event codecs already use the canonical field-list boundary.

Integration method DTOs now embed canonical optional Form fields through the later MCP/Integration pass. Runtime MCP elicitation, form request/reply endpoints, permissions, and console authentication UI still need owner integration. No Core/Server, configuration, provider, source-guard ledger, or daemon code was edited by the Form pass. Top-level reference deserializers still require caller null-result guards where the parent codec does not supply one.

Build evidence: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors**. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. This does not supersede any separately recorded Server build failure or establish UI/runtime fidelity.

## Model And Provider Metadata

Read the complete upstream `model.ts` and `provider.ts`, plus Money, Integration ID, shared scalar/default semantics, and the current Core catalog projection. `Model.cs` and `Provider.cs` now supply canonical metadata DTOs rather than nullable approximations. The Core catalog, endpoint projection, and configuration files were not edited. This pass does not invent model limits, availability flags, transport support fields, or provider discovery behavior.

| Source boundary | Implemented Schema API |
| :--- | :--- |
| Model/variant/provider IDs and model family | `ModelId`, `VariantId`, `ProviderId`, and `ModelFamily` accept unrestricted non-null strings, including empty/whitespace values. They do not require generated prefixes or membership in a known-provider list. Typed JSON remains a string, and default struct values fail serialization. ProviderId exposes the source's built-in ID constants. |
| `Model.Ref`, `parse` | Existing `ModelRef(providerId, id, variant?)` string-based constructor/properties are retained for real runner/store callers. JSON requires exact id/providerID keys and omits absent variant. Parse keeps the source's first-slash/first-hash rules, allows slashes in model IDs, rejects empty parsed segments and extra variant hashes, and does not normalize or substitute names. Direct Ref field strings retain the looser source brand domain. |
| Compatibility | `ModelCompatibility` has all five optional fields. ReasoningField is any string, not a guessed three-value enum. MaxTokensField permits only max_completion_tokens/max_tokens. No boolean defaults are injected. |
| Capabilities | `ModelCapabilities` requires tools/input/output; responsesWebsockets is optional. String arrays may be empty but cannot contain null. `CreateDefault()` implements only the explicit source helper: tools true, text/image input, text output. |
| Cost | `ModelCost` requires per-million-token input/output prices and nested `ModelCacheCost(read, write)`. `ModelCostTier` has required type context and signed-integer size. `MoneyPerMillionTokens` is a finite-number brand distinct from total USD Money; no nonnegative price constraint is invented. |
| Limits/time | `ModelLimit(context, output, input?)` uses signed finite integers, not an int64 cap or an invented positivity constraint. `ModelTime(released)` requires a finite number, not an ISO date. |
| Variant | `ModelVariant` requires a typed VariantId and includes optional settings, headers, and body at the top level. No overlay field is dropped or nested under a fabricated wrapper. |
| Model info | `ModelInfo` requires id/modelID/providerID/name, capabilities, variants, time, cost, status, enabled, and limit. Optional family/compatibility/package/settings/headers/body are fully represented. Status accepts only alpha/beta/deprecated/active. False and empty arrays remain explicit values. |
| Provider info | `ProviderInfo` requires typed id, name, exact activation, and package, with optional integrationID and all overlays. Activation accepts only auto/enabled/disabled strings, not integer enum values or alternate casing. `Empty(id)` implements the source helper's name=id, activation=auto, package empty, and absent overlays. |

Settings/body maps use `JsonElement` values, preserving arbitrary JSON values, null values inside maps, and key spelling without unsafe CLR-object reflection. They are the wire representation of the source's Any-valued maps, not permission to place callbacks or arbitrary non-JSON objects in browser-safe Schema. Header maps require string values and preserve key casing; no HTTP header filtering or case normalization is done here. Optional map/object fields reject explicit null and omit absent values. The public response owner must continue to sanitize secrets; Schema validation is not a redaction policy.

### Default Discipline

`ModelInfo.CreateDefault(ProviderId, ModelId)` implements the exact upstream helper: matching public/API ID, name from ID, default capabilities, empty variants/cost, release time zero, active/enabled, and the source's explicit context 200000/output 32000 limits. This helper is **not** invoked by ordinary DTO construction or deserialization. Missing live catalog capabilities, time, status, pricing structure, or required limits must not be concealed by calling it automatically. Catalog guards stay with the provider owner until actual source metadata is available.

No default cache prices are supplied for a cost object: cache/read/write are required. There are no automatic empty variant/price lists, enabled=true, or unknown limits on the ordinary ModelInfo constructor. Empty lists or zero values must be explicitly supplied from the chosen source or from an intentional source-helper call.

### Catalog Handoff

The canonical constructor is `ModelInfo(Id, ModelId, ProviderId, Name, Capabilities, Variants, Time, Cost, Status, Enabled, Limit, ...)`. IDs are the typed Schema wrappers; optional arguments include ModelFamily, ModelCompatibility, Package, Settings, Headers, and Body. `ProviderInfo` keeps its leading id/name/activation/package shape but now uses typed ProviderId and optional IntegrationId. The scan found no existing application construction of the old ModelInfo/ProviderInfo approximations; Core's catalog currently uses its own DTO/JSON projection, which its owner can now replace with these types.

Use `OpenCodeJsonContext.Default.ModelInfo` and `.ProviderInfo`, with the registered nested capabilities/compatibility/variant/time/limit/cost codecs. ModelCost's old flat optional CacheRead/CacheWrite shape is replaced by mandatory ModelCacheCost; price values use MoneyPerMillionTokens. ModelLimit numeric members change from long to validated double integers, and gain optional Input. ModelVariant settings change from object to JsonElement values and gain Body. Integer-only consumers and string-to-brand constructor arguments need explicit mapping; ModelRef's existing string API remains intact.

The public models/providers projection must not add internal available, transportSupported, or credential diagnostic flags to these canonical DTOs. This schema pass neither alters the provider owner's sanitization nor authorizes exposing private settings/headers/body contents. It does not change endpoint routing or remove missing-metadata guards.

### Provider Request Scope

The subsequent approved Agent/Request pass moves the single `ProviderRequest` declaration from `Agent.cs` into `Provider.cs` and replaces its nullable/object-valued approximation. This resolves the earlier ownership blocker without creating a second public request type. See the implementation and caller handoff below; source coverage and build evidence are not runtime fidelity acceptance.

The implemented contract requires settings/headers/body on the wire, with a constructor-only empty-object default for settings. Effect's `withConstructorDefault` explicitly applies during make, not JSON decoding (`Schema.ts:5668-5674` in the reference). Headers and body receive no invented defaults. The approved Agent extension also fixes required Request and the default-agent helper, while leaving runtime/configuration services outside Schema.

Latest evidence: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed with **0 warnings and 0 errors** in Debug after the Model/Provider metadata changes. No Core/Server or endpoint round-trip result is claimed. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. Global public/shared/durable inventory completeness flags remain false; no new event names or feed filter were invented for this metadata pass.

## Agent And Request Contracts

Read the complete `provider.ts`, `agent.ts`, and `permission.ts` source contracts and the separate `config/agent.ts` color definition. Approved Schema changes move only the ProviderRequest declaration out of Agent.cs, then align AgentInfo and the permission-rule dependency. No Core catalog, Server endpoint, or configuration implementation was edited.

### Provider Request

`ProviderRequest(Settings, Headers, Body)` now has three non-null required maps. Settings/body use JsonElement values at the wire boundary; headers require string values and preserve their keys. `ProviderRequest(Headers, Body)` supplies a fresh empty settings dictionary and requires both other arguments. The three-argument constructor is explicitly selected for JSON deserialization, and all three properties are marked required with null-rejecting converters.

Consequently, construction can omit settings through the two-argument overload, but JSON missing settings is invalid. Explicit null settings in the full constructor is invalid, as are absent/null headers or body. There is no parameterless request or implicit headers/body default. The public namespace/type name is unchanged and there is exactly one declaration, now in Provider.cs. `OpenCodeJsonContext.Default.ProviderRequest` is registered for catalog/agent owners.

### Agent Info

`AgentInfo` now requires id, name, mode, hidden, permissions, and Request in its constructor and wire contract. Required strings/objects and default struct IDs are validated; permissions are an ordered array which may be empty but cannot contain null entries. Optional model/system/description/color/steps fields are omitted when absent and reject explicit null on decoding through the covered codecs.

Agent ID and name remain unrestricted non-null strings. Color is also an arbitrary string: the hex-only rule belongs to Config.Agent.Color and was not borrowed into Agent.Info. Mode accepts exactly subagent/primary/all, rejecting integer enum values and alternate casing. Optional steps uses a double-backed positive integer with no int32 cap; zero, fractional, non-finite, and negative values are rejected.

`AgentInfo.CreateDefault(id)` now produces the exact source request `{ settings: {}, headers: {}, body: {} }`, name equal to the supplied ID, primary mode, hidden false, and the source permission order: wildcard allow, external-directory ask, environment-file ask, environment-file-pattern ask, then the environment example-file allow exception. Model, color, steps, system, and description are not fabricated by this helper.

The existing `PermissionRule` type is retained, with required non-null action/resource strings and exact allow/deny/ask effect validation. Empty strings are allowed by the source. Rule arrays retain order and do not evaluate globs, merge policy, sort, deduplicate, or add runtime permission behavior. Permission request/reply event inventory remains separately incomplete; this dependency fix does not claim the whole Permission module is ported.

### Caller Handoff

- Catalog owner: canonical ModelInfo, ProviderInfo, and ProviderRequest Schema surfaces and generated metadata are now available. There is no remaining request-declaration ownership blocker. Core projection/sanitization and runtime selection remain with their owners.
- `AgentInfo.Request` is no longer nullable or an optional constructor argument. The source scan found two direct constructions omitting it at `src/OpenCode.Server/Endpoints/AgentEndpoints.cs:15` and `:23`. Those callers must pass a real ProviderRequest, or explicitly start from CreateDefault and override their declared fields. They were not silently given a constructor fallback and were not edited here.
- Existing CreateDefault callers keep the same signature and now receive a valid required request. Existing ProviderRequest settings/body object maps must migrate to JsonElement maps; callers must explicitly provide headers/body. The scan found no application construction of the former parameterless/all-optional request.
- AgentInfo.Steps changes from nullable int to nullable double constrained to positive integers. AgentMode and PermissionEffect enum member identities stay the same, but their JSON parsing is now source-exact.

Verification: isolated Schema build `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors**, including source generation and the constructor selection annotation. No Server build is claimed; the two caller adaptations above are source-identified. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. No global inventory completeness flag changed.

## Terminal Contracts

Read all of `schema/src/shell.ts`, `pty.ts`, `persistent-pty.ts`, and `pty-ticket.ts`, plus their complete Protocol groups and the persistent-PTY server handler. Schema now contains the declared Shell, PTY, persistent-PTY, and connect-token data contracts with generated codecs. This is independent of native process/PTY/daemon execution, which remains unimplemented or separately owned.

| Source boundary | Canonical .NET surface |
| :--- | :--- |
| Shell IDs/status/time | ShellId requires `sh_`; Create and both Ascending overloads preserve generated format and explicit adoption. ShellStatus has exactly running/exited/timeout/killed. ShellTime started/completed are finite numbers, not integers or ISO dates; completed is optional. |
| Shell Info | Required id/status/command/cwd/shell/file/time/metadata. PID is optional nonnegative integer; exit is optional finite number, including negative/fractional values permitted by the source. Metadata is opaque, required on Info, and never interpreted by Schema. |
| Shell input/output | ShellCreateInput requires command and nonnegative-integer timeout, with optional cwd/metadata. ShellOutputInput has optional nonnegative cursor/limit. ShellTimeoutInput maps the Protocol timeout payload. The one existing ShellOutput type moved from SessionMessage.cs into Shell.cs, with unchanged name/shape and continued session-message reuse. |
| PTY IDs/Info | PtyId accepts the legacy `pty` prefix while Create emits `pty_...`; Ascending supports generation or explicit validated adoption. PtyInfo requires all source fields, with running/exited status, nonnegative-integer PID, and optional nonnegative-integer exitCode. |
| PTY input | PtyCreateInput has optional command/args/cwd/title/env without fabricated defaults. PtyUpdateInput has optional title/positive-dimension size. Env uses the existing pure string-map codec, with no provider or HTTP header policy attached. |
| Persistent Info | PersistentPtyInfo extends the canonical PtyInfo fields, adding required sessionID, required nullable foregroundProcess, positive size, and nonnegative head/tail output offsets. A foregroundProcess null is emitted, not omitted; a missing property is invalid. |
| Persistent create/update | Create requires args/title/env and permits optional command/cwd/size. Update requires size and permits optional attachmentID. No attachment ID, environment, command, or viewport default is fabricated. |
| Handoff | Required directory/instanceID/ticket/expiresAt. ExpiresAt retains source Schema.Number semantics, including its named non-finite JSON codec, rather than acquiring an unsupported integer or positivity constraint. |
| Snapshot | PersistentPtySnapshot requires info/text/checkpoint/cursor. TerminalCursor has nonnegative integer x/y. Checkpoint is byte[] in memory and a base64 string in JSON. |
| Read | PersistentPtyReadLines enforces a positive integer at most 65535. PersistentPtyReadResult requires ptyID/title/cwd, required nullable foregroundProcess, and TerminalScreen with text/positive cols/rows/cursor. No default read-line count or current-terminal selection policy is implemented in Schema. |
| Ticket | PtyConnectToken requires ticket and positive-integer expires_in, with no int32 cap or invented ticket-string format. Ticket issuance, consumption, expiry checks, and origin/auth checks remain runtime work. |

Numeric integer fields use validated double values to match the source JavaScript integer domain without added int32/int64 limits. The 65535 upper bound applies only to ReadLines, not to all viewport dimensions. No head/tail ordering, cursor-versus-screen bounds, time ordering, URL/path validation, argument defaults, or foreground-process/status consistency rule was invented. Shell Info metadata is required; the source comment about creator defaulting it to `{}` belongs to the runtime creator, not an implicit DTO default.

### Checkpoint Codec

The byte encoding is source-backed, not inferred from CLR byte[] defaults. Effect's `Schema.Uint8Array` uses a base64 JSON codec (`Schema.ts:11863-11894`), and HTTP API JSON responses apply `Schema.toCodecJson` (`HttpApiEndpoint.ts:1276-1285`). The persistent snapshot route returns this schema directly. The inspected raw WebSocket resize handler also explicitly base64-encodes checkpoint bytes; its binary/control/replay protocols are separate from this HTTP DTO.

`PtyCheckpointJsonConverter` emits base64 directly through Utf8JsonWriter. Decoding strips CR/LF only, then validates the padded standard base64 grammar and uses the native byte decoder, matching Effect `Encoding.decodeBase64` and `stripCrlf`. Other whitespace, URL-safe alphabet, JSON byte arrays, objects, and null checkpoints are rejected. Empty byte arrays/empty base64 are valid. No byte-indexed JSON object, UTF-8 text substitution, or padding normalization is invented.

Snapshot constructors retain the supplied byte-array reference, like the source byte container; callers must provide an appropriately owned, correctly sized array and must not return pooled storage while it is still referenced. No pooling, native pointer ownership, or speculative SIMD was added. The base64 writer avoids an intermediate encoded string; no allocation-count or throughput result is claimed.

### Event Membership

- ShellEventDefinitions binds `shell.created` with `{ info }`, `shell.exited` with `{ id, status, exit? }`, and `shell.deleted` with `{ id }`.
- PtyEventDefinitions binds `pty.created`/`pty.updated` with the shared `{ info }` DTO, `pty.exited` with required `{ id, exitCode }`, and `pty.deleted` with `{ id }`.
- PersistentPtyEventDefinitions binds `persistent-pty.added` with `{ sessionID, terminal }` and `persistent-pty.removed` with `{ sessionID, ptyID }`.

All these domain events are ephemeral, with exact source type/identifier strings, and now appear in both implemented public and shared inventories in source order. The durable inventory is unchanged. Session-level shell events and native execution/projector registration are separate work; this does not invent durable transport events. All global inventory completeness flags remain false.

### Caller Handoff

ShellTime, PID/exit-code/count/dimension fields, output offsets, and connect-token expiry now use double-backed source number domains. Existing integer constructor arguments convert implicitly; integer-only native APIs/readers need explicit range-aware conversion at their boundary. PersistentPtyInfo is now a subtype of PtyInfo, reusing its exact base fields, and requires an explicit ForegroundProcess argument even when null. ShellOutput's namespace/type/API is unchanged; only its declaration moved.

The source scan found no application construction sites for the previous terminal Info/size/time DTOs. No Core, Server handler, Client, native PTY, or daemon implementation was edited. Query-string parsing for `lines` still belongs to Protocol/HTTP binding (`NumberFromString` before ReadLines validation); omitted lines use live height only in the runtime. Raw WebSocket tickets, control framing, takeover, replay, viewport adaptation, and terminal selection are not implemented by these schemas.

Verification: `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors**. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. Constructor/codec source coverage and compilation are not proof of terminal execution, checkpoint round trips, or full client/server interoperability.

## MCP And Integration Contracts

Read the complete `mcp.ts`, `config/mcp.ts`, `integration.ts`, `integration-id.ts`, `connection.ts`, `mcp-event.ts`, and the MCP/Integration Protocol groups. The declared data shapes from these selected modules are now represented in Schema with typed unions, required fields, omission rules, and generated metadata. This is source-contract implementation coverage, not an implemented MCP service, authentication process, or console UI.

### MCP Coverage

| Source contract | Implemented mapping |
| :--- | :--- |
| Timeout/local config | McpTimeoutConfig has optional positive-integer startup/catalog/execution values using double, without an int32 cap. McpLocalConfig requires a command array and includes optional cwd/environment/disabled/codemode/timeout. Empty command arrays remain schema-valid; process-launch validation is not invented here. |
| OAuth config/selection | McpOAuthConfig represents all five optional fields with exact client_id/client_secret/scope/callback_port/redirect_uri names. CallbackPort is an integer in 1-65535. McpOAuthSetting is an object-or-false union; McpOAuthDisabled.Instance emits literal false. True, null, arrays, and strings are rejected at that property. An empty config object is distinct from false or omission. |
| Remote config | McpRemoteConfig requires url and includes headers/oauth/disabled/codemode/timeout. The old leading constructor arguments are retained, with OAuth appended as an optional argument. No URL parsing, timeout value, or codemode=true constructor default is invented from descriptive comments. |
| Config.MCP | McpConfiguration has optional timeout and a string-keyed map of canonical McpServerConfig values. No duplicated local/remote/OAuth config types were introduced. The narrow OpenCodeConfiguration.Mcp property now uses this wrapper, not a flat server map. Legacy normalization remains Core-owned. |
| Protocol add payload | McpAddPayload requires `{ config: McpServerConfig }`, matching the source workaround for a top-level union payload. No endpoint was added or changed. |
| Status/server | McpStatus has exactly connected/pending/disabled/failed/needs_auth variants; failed requires an error string. No closed variant was inferred from an event comment. McpServer requires name and the nested status object and permits an optional integrationID for unambiguous auth routing. |
| Resources | McpResource, McpResourceTemplate, McpResourceCatalog, McpResourceContent, and the text/blob content-part union represent all source fields, including uriTemplate and mimeType casing. Required arrays may be empty, not missing/null. Blob is a source string and is not given an invented base64 constraint. |

Server/header/environment maps preserve names and values and reject null entries where the source requires strings or a server object. Resource names, URLs, URI templates, MIME values, and config strings remain unrestricted strings unless the source supplies a constraint. This pass adds no lookup, discovery cache, process launch, HTTP request, OAuth registration, or resource reading to Schema.

### Integration And Connection

OAuth and command integration methods retain their existing leading constructor signatures and required id/label fields. OAuth gains optional nonempty Form fields. Key methods have only optional label/form, using a parameterless record with explicit property initialization; the former fabricated id is removed. Env methods require only a names array and no id. Arrays preserve order and may be empty where the source uses Array; a present auth form must be nonempty because it uses Form.Fields.

IntegrationRef and IntegrationInfo require id/name; Info additionally requires methods/connections. Opaque JSON metadata is optional and retained without a key whitelist. ConnectionInfo remains the exact credential/env union: credential requires a CredentialId and label, env requires a name. No credential values, environment values, availability flags, or guessed connection states were added to those public metadata shapes.

IntegrationMethodId and IntegrationAttemptId expose non-null unrestricted string brands. AttemptId.Create emits the source con_ prefix, but validation does not require that generated prefix. Existing OAuth/command method Id string properties remain source-compatible and have equivalent unrestricted-string wire validation. IntegrationAttempt includes required attemptID/url/instructions/mode/time, with mode limited to auto/code. IntegrationCommandAttempt contains only attemptID/time.

OAuth attempt status and command attempt status have separate pending/complete/failed/expired unions. All carry time; failed carries a required message. Only command pending has an optional message. No cancelled status, default pending state, or automatic clock/expiry value is invented. IntegrationAttemptTime uses required Schema.Number created/expires values and the existing named-nonfinite JSON number codec, not integer-only values or DateTimeOffset ISO encoding; no expiry-order constraint is added.

### Protocol Payloads

IntegrationInputs.cs provides the exact declared request bodies for wellknown add, key connect, OAuth connect/complete, and command connect. Optional answers reuse the unchanged FormAnswer API, and labels/code omit absent values while rejecting explicit null. OAuth/command connect use typed IntegrationMethodId with the exact methodID key. Route integration/attempt IDs and location query fields are not duplicated into the bodies. Response envelope semantics such as UndefinedOr on integration.get remain Protocol/handler integration work.

### Membership And Handoff

Existing membership remains exact and unchanged: integration.updated is an empty ephemeral public/shared event; mcp.status.changed and mcp.resources.changed are public/shared; mcp.tools.changed is shared-only. Connection defines no events. No OAuth-started/connected/resource-read event was invented, and no global completeness flag or feed whitelist changed.

- Core console owner can now consume generated IntegrationInfo, IntegrationMethod, ConnectionInfo, IntegrationAttempt/CommandAttempt, and both attempt-status union codecs, including canonical Form fields/answers. No Core console or credential-store implementation was edited.
- IntegrationKeyMethod no longer accepts the old id constructor; initialize Label/Form explicitly. IntegrationEnvMethod now takes only Names. No application construction sites for those old signatures were found in the final scan.
- McpRemoteConfig retains its previous leading arguments and accepts optional OAuth last. Use McpOAuthDisabled.Instance or McpOAuthConfig through the McpOAuthSetting contract, rather than bool true or an object approximation. Timeout properties change from nullable int to nullable positive-integer double.
- OpenCodeConfiguration.Mcp changes from a flat dictionary to McpConfiguration(timeout?, servers?). This is the canonical current config shape, not an automatic legacy migration. Other fields of that larger configuration DTO are not certified by this narrow correction.
- Use union-root generated metadata (McpServerConfig, McpOAuthSetting, McpStatus, IntegrationMethod, ConnectionInfo, and attempt-status types) so tagged/false representations are preserved. Parent property/list codecs already select these roots. Top-level reference null-result guards remain required where a parent codec does not supply them.

The transient Client build blocker for missing OpenCodeJsonContext.McpOAuthConfig was resolved by an explicit JsonSerializable registration, not a reflection fallback. The final isolated Schema build, `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build`, passed with **0 warnings and 0 errors**. No Client/CLI/Core/Server build result is inferred. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. Schema source coverage is not runtime MCP, OAuth, form execution, or end-to-end console compatibility proof.

## Canonical Config Contracts

Read the full `packages/schema/src/config.ts` and all 17 current config submodules. The canonical OpenCodeConfiguration root and new config-domain files now map every declared root field and the selected submodule shapes/codecs. Removed the earlier unchecked ConfigDetails approximations. `src/OpenCode.Schema/Config/ConfigModels.cs` was explicitly left unchanged: its OpenCodeConfig/ProviderConfig/ModelConfig types belong to the provider-owned loader and are not represented as the complete canonical config contract.

### Source Mapping

| Source submodule | Canonical implementation |
| :--- | :--- |
| agent | ConfigAgentInfo has model selection, config request overlays, system/description/mode/hidden/color/steps/disabled/permissions. Config color enforces #RRGGBB; this does not change runtime Agent.Info's arbitrary color string. Steps are optional positive integers. |
| command | ConfigCommandInfo requires template and includes optional description/agent/model/subtask. There is no template expansion or command execution in Schema. |
| compaction | Optional auto/keep/buffer with nonnegative keep.tokens/buffer; no implicit auto setting, buffer, or retention count. |
| experimental | Optional portable_shell_scanner, nonnegative subagent_depth, and ordered policy array. Descriptive defaults false/1 are not inserted into DTOs. |
| formatter | ConfigFormatterInfo preserves boolean versus map. Map entries contain the exact optional disabled/command/environment/extensions fields. Entry false is not invented as another valid map-value shape. |
| lsp | ConfigLspInfo preserves boolean versus map. Entries are disabled:true or a required-command server with extensions/disabled/env/initialization. The source's first disabled branch wins when disabled is true, discarding excess server fields during union normalization. |
| media | ConfigMediaInfo/Image includes auto_resize and positive max_width/max_height/max_base64_bytes without resize behavior or fabricated limits. |
| model | ConfigModelSelection decodes shorthand or explicit objects into providerID/model/variant. Encoding emits the explicit object and uses model, not ModelRef.id. Provider segments exclude slash/hash; model/variant exclude hash and cannot be empty. No whitespace trimming, alias substitution, or selected-model default is added. |
| mcp | Reuses McpConfiguration and the existing canonical timeout/local/remote/OAuth/server union types. No parallel MCP DTO family was introduced. |
| policy | ConfigPolicyInfo requires action provider.use, string resource, and allow/deny effect. Ask is not valid here, unlike Permission.Effect. No policy evaluation is performed. |
| plugin | Ordered ConfigPlugin entries retain either a string or a package/options object. Options preserve opaque JSON values. No package resolution, directive interpretation, sorting, or deduplication is added. |
| provider | Canonical ConfigProviderInfo/ConfigModelInfo/ConfigModelVariant represent every declared field and JSON settings/body plus string headers. ConfigProviderRequest has only optional headers/body, not runtime ProviderRequest's required maps/settings. Optional model limits and cost/cache members remain optional. Cost preserves single-object versus array shape. |
| reference | ConfigReferenceEntry preserves string, Git, and Local shapes without a runtime type tag. Git is the first object branch; full Git-field validity is considered before falling back to Local. No path/git resolution or migration is performed. |
| tool-output | ConfigToolOutputInfo max_lines/max_bytes are positive integers, not unchecked nullable ints or nonnegative values. |
| warming | ConfigWarming preserves boolean or prompt/interval/duration object. ConfigDuration implements the source duration-string representation and normalization; no keep-alive scheduler or described interval/duration defaults are inserted. |
| watcher | ConfigWatcherInfo preserves the optional ignore string array, including order and empty arrays. |
| websearch | ConfigWebSearchSelection permits literal false or a required provider object. True is rejected; provider accepts the source unrestricted ID string, including random, without an invented provider whitelist. |

### Root And Entries

OpenCodeConfiguration now includes enterprise, formatter, lsp, media, skills, commands, references, websearch, plugins, and warming in addition to the previously represented fields. Autoupdate is the exact boolean-or-notify union; share is manual/auto/disabled. Providers use canonical ConfigProviderInfo rather than the loader's partial ProviderConfig. The legacy singular provider field is not declared in the current root; compatibility normalization must translate legacy input before canonical decoding.

All current root properties remain optional. Missing and empty values are not conflated, false is retained, optional explicit null is rejected by the mapped codecs, and map/array entries cannot silently become null. Strings and JSON values are not constrained beyond their source contracts. The document/directory/agents/claude ConfigEntry union is also represented, with required info or path and optional document path. These records do not discover files or create a producer registry.

### Important Distinctions

- ConfigModelSelection is not ModelRef: config uses model and validates selection segments; runtime Ref uses id and unrestricted string brands. Shorthand normalization is explicit in the config codec.
- Config provider limits are optional signed integers; canonical runtime Model.Info requires its limits. Config cost cache/read/write are optional; runtime ModelCost requires cache/read/write. No runtime defaults are copied into config to hide absent metadata.
- Config agent request permits only optional headers/body. Runtime ProviderRequest requires all maps and has a settings-only construction default. These source-defined contracts are not interchangeable.
- Formatter/LSP booleans, LSP disabled objects, plugin strings, reference strings, cost arrays, websearch false, and warming booleans have dedicated typed codecs. They are not stored as anonymous object approximations.
- LSP and reference object unions preserve declared branch precedence. Other unknown fields follow source struct behavior rather than acquiring compatibility aliases inside the canonical schema.

### Duration Mapping

ConfigDuration parses the complete source string unit set from nanos through weeks and signed infinity. The Effect Duration reference and DurationFromString transformation were inspected: whole nano/micro values use arbitrary-precision integers; fractional inputs round ties away from zero; millisecond-based units retain the source Millis/Nanos representation; encoding uses the source normalized millis/nanos/infinity strings. It does not impose TimeSpan's range or positive-duration restrictions.

The duration grammar is source-based, including the allowed decimal/unit syntax and ECMAScript whitespace set. No exponent/abbreviated-unit input syntax or runtime interval default was invented. Extreme-number formatting and normalization follow the inspected source implementation rather than claiming a separately proven lossless round trip for every extreme source duration; no runtime checks were authorized. Generated regex and native numeric/string primitives are used without a dynamic converter factory, parser process, or scheduler.

### Loader Handoff

Use `OpenCodeJsonContext.Default.OpenCodeConfiguration` for the complete current root after source-compatible normalization. Config/ConfigModels.cs remains a separate partial loader model, with raw JsonElement model selection, legacy provider aliases, and missing canonical producer fields. Switching the runtime loader is still an owner task; this pass does not claim that the running application now consumes every new canonical field.

New canonical provider/model types are deliberately named ConfigProviderInfo, ConfigModelInfo, ConfigModelVariant, ConfigModelCost/CacheCost/Limit, and ConfigProviderRequest to avoid modifying or masquerading as the protected loader DTOs. Runtime conversion must retain all supported producer fields and preserve existing normalization/precedence rather than silently dropping them.

OpenCodeConfiguration and several former ConfigDetails records now use property-based initialization. The source scan found no application constructor calls for their old positional signatures. Existing names are retained where the source concept is unchanged; Model, Autoupdate, provider maps, numeric option types, and the new unions require deliberate caller mapping. No Core loader, provider-owned ConfigModels.cs, runtime config producer, Server endpoint, or daemon/guard ledger was edited.

Verification: isolated Schema build `dotnet build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed in Debug with **0 warnings and 0 errors** after the canonical root, codecs, and generated metadata were added. No tests, runtime/API/DB/process actions, benchmarks, or commits were performed. Config source coverage and build success do not establish full V1 normalization, runtime producer support, reload/discovery behavior, or overall port completeness. Public/shared/durable event inventory flags remain false; config.updated membership is unchanged.

## Compaction Event Handoff

Read `src/OpenCode.Core/Session/COMPACTION.md`, Core's internal CompactionProjector definitions, the full upstream `session-event.ts` and `session-compaction-event.ts`, and the relevant SQL, migration, message-updater, projector, and instruction-state sources. This pass adds canonical Schema contracts only; Core's summary implementation, SQL operations, internal adapters, and recovery guards were not edited or executed.

| Definition | Canonical DTO | Exact data and membership |
| :--- | :--- | :--- |
| `SessionEventDefinitions.Compaction.Started` | `SessionCompactionStartedEventData` | sessionID, reason auto/manual, required recent, optional inputID. Durable version 1, aggregate sessionID; public and shared. |
| `SessionEventDefinitions.Compaction.Delta` | `SessionCompactionDeltaEventData` | sessionID and text only. Ephemeral; public and shared, no durable envelope. |
| `SessionEventDefinitions.Compaction.Ended` | `SessionCompactionEndedEventData` | sessionID, reason auto/manual, required text and recent. Durable version 1, aggregate sessionID; public and shared. |
| `SessionEventDefinitions.Compaction.Failed` | `SessionCompactionFailedEventData` | sessionID, reason auto/manual, canonical SessionStructuredError, optional inputID. Durable version 1, aggregate sessionID; public and shared. |
| `SessionCompactionEventDefinitions.Compacted` | `SessionCompactedEventData` | sessionID only. Ephemeral, broader shared inventory only; not a public ServerDefinitions member and not a replacement for ended. |

Required fields use generated metadata and constructor/callback validation. Optional inputID is omitted when absent and rejects explicit null, default IDs, or a non-msg_ prefix. Session identity retains source legacy ses-prefix validation. Strings remain source strings: recent and text may be empty at the schema layer; Core's empty-summary failure rule is not silently added to the DTO. No reason, checkpoint ID, text, timestamp, usage, or epoch value is fabricated during decoding.

The existing canonical definition instances are referenced by the implemented Session/public/shared lists and by the versioned durable map. The three stored keys are session.compaction.started.1, session.compaction.ended.1, and session.compaction.failed.1. Source event SQL stores the versioned type in its text type column and sequence separately; no database schema migration or new durable version is required for these matching payloads. Deltas and session.compacted are excluded from durable lookup. Global inventory completeness flags remain false.

### Epoch Boundary

Upstream `session/projector.ts` projects ended and calls `InstructionState.advanceEpoch` with `event.durable.seq` in the same projection path. `session/instruction-state.ts` sets epoch_start and through_seq to that sequence and copies current_values into initial_values. The instruction_state columns are also present in migration `20260804233008_loose_psylocke.ts`. These are projected state, not missing event payload fields.

Accordingly, ended data remains only sessionID/reason/text/recent. The running checkpoint supplies its identity and original time; message updater fallback identity comes from the event when needed. Started/failed retain optional inputID for the source correlation cases. The event envelope owns ID/created/sequence/version; it would be incorrect to repeat epoch_start, through_seq, a full instruction snapshot, or derived checkpoint status in these DTOs. The existing separate session.usage.recorded definition remains the internal durable cost/token fact.

### Core Adoption

Core can replace its internal CompactionStartedData/EndedData/FailedData/DeltaData and private JSON context with the corresponding canonical DTOs and `OpenCodeJsonContext.Default.SessionCompaction*EventData` metadata. Constructor argument order matches the handoff's internal records. Register projector callbacks against the shared `SessionEventDefinitions.Compaction` instances instead of recreating Schema definitions from matching strings. The Core adapter may still pair those metadata instances with its transaction callback; no callback belongs in Schema.

This source/compile handoff does not verify manual/auto/overflow execution, one-rebuild behavior, replay, atomic epoch movement, or crash settlement. The Core handoff's pending-control/compaction-history restart guards remain necessary and were not relaxed. Configuration work was not reopened for review.

Build on 2026-08-31: `& .\.dotnet\dotnet.exe build src/OpenCode.Schema/OpenCode.Schema.csproj --artifacts-path C:\tmp\opencode\dotnet-schema-build` passed for net11.0 with **0 warnings and 0 errors**. global.json pins **11.0.100-preview.7.26381.103**, roll-forward disabled; the build used that repository SDK and emitted only the informational preview-SDK notice. No tests, runtime/model/tool/API/database/process-control actions, or commits were performed. No Core/Server build result is inferred.
