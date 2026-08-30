# Agent Fleet Workplan: Automated Mechanical Translation

This document specifies the operational procedure for coordinating a fleet of autonomous AI agents to port files from the TypeScript repository into C# (.NET 10).

---

## 1. Operational Principles

To avoid architectural drift across dozens of files, each agent must operate under strict constraints:
1. **Never Invent New Architecture**: Translate 1:1 following the rulesets in `/port/csharp/ruleset/`.
2. **Strict Package Dependency Order**: Never start a downstream package before upstream contracts compile and pass tests.
3. **Automated Verification Gates**: An agent task is not complete until `dotnet build` and `dotnet test` succeed with zero warnings.
4. **Preserve Wire Compatibility**: Wire JSON keys, event strings, route paths, and table column names must match the TypeScript implementation exactly.

---

## 2. Phased Execution Order

```text
Phase 1: Foundation
  └─ Agent 1: OpenCode.Schema (IDs, Models, JSON Context)
       ▲
       │
Phase 2: Protocol & Persistence
  ├─ Agent 2: OpenCode.Protocol (Request/Response Models, Route Contracts)
  └─ Agent 3: OpenCode.Core.Database (SQLite Schema, Migrations, Connection Pool)
       ▲
       │
Phase 3: Core Domain Engine
  ├─ Agent 4: OpenCode.Core.Session (Inbox, Coordinator, Store)
  ├─ Agent 5: OpenCode.Core.Runner (Step execution, LLM streaming, Retry)
  └─ Agent 6: OpenCode.Core.Tools (Tool definitions, Context, Registry)
       ▲
       │
Phase 4: Server and API
  ├─ Agent 7: OpenCode.Server.Routes (Minimal APIs, Route Groups)
  └─ Agent 8: OpenCode.Server.EventFeed (SSE Dropping Queue)
       ▲
       │
Phase 5: SDK and Verification
  ├─ Agent 9: OpenCode.Client & OpenCode.Sdk (In-Process Host & Facade)
  └─ Agent 10: Integration Testing & TUI verification with bun run dev:live
```

---

## 3. Subagent Prompt Template

When dispatching an agent to port a specific module, inject the following context into the subagent prompt:

```markdown
You are an expert C# .NET 10 developer executing an idiomatic port of an OpenCode module.

### Target Module
- Source TypeScript: `packages/{pkg}/src/{file}.ts`
- Target C#: `src/OpenCode.{Proj}/{Folder}/{File}.cs`

### Applicable Rulesets
- Read `/port/csharp/ruleset/01-type-system-and-contracts.md`
- Read `/port/csharp/ruleset/02-concurrency-and-effects.md`
- Read `/port/csharp/ruleset/03-dependency-injection-and-services.md`
- Read `/port/csharp/ruleset/04-persistence-and-storage.md`

### Invariants
1. Use modern C# (C# 13/14, .NET 10, primary constructors, collection expressions, pattern matching).
2. All records must use System.Text.Json source generation annotations.
3. Do not import third-party functional monad libraries; use Task, CancellationToken, and Channels.
4. Run `dotnet build` and ensure zero errors and zero warnings before finishing.
```

---

## 4. Verification Checklists for Reviewer Agents

Before merging or committing any ported file, verify:

- [ ] **Contract Fidelity**: Are all fields from the TypeScript Schema present with identical JSON property names?
- [ ] **ID Safety**: Are branded strings converted to `readonly record struct` IDs?
- [ ] **Cancellation**: Does every async method accept a `CancellationToken ct = default`?
- [ ] **Resource Cleanup**: Are streams, connections, and background fibers wrapped in `IAsyncDisposable` / `await using`?
- [ ] **Native AOT**: Are there any reflection-based serialization calls (`JsonSerializer.Serialize(obj)`) without a `JsonTypeInfo` or `JsonSerializerContext`?
- [ ] **Tests**: Does a corresponding test file exist in `test/OpenCode.{Proj}.Tests` validating the ported logic?
