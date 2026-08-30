# Package Mapping: TypeScript Monorepo to .NET 10 Solution

This document defines the 1:1 mapping between TypeScript packages and .NET 10 C# projects.

---

## 1. Project Mapping Table

| TypeScript Package | .NET Project | Target Framework | Key Dependencies | Role |
| :--- | :--- | :--- | :--- | :--- |
| `@opencode-ai/schema` | `OpenCode.Schema` | `net10.0` | None (Bcl only) | Domain contracts, strongly-typed IDs, JSON converters |
| `@opencode-ai/protocol` | `OpenCode.Protocol` | `net10.0` | `OpenCode.Schema` | Route contracts, API input/output models, event definitions |
| `@opencode-ai/core` | `OpenCode.Core` | `net10.0` | `OpenCode.Schema`, `Microsoft.Data.Sqlite`, `Dapper`, `Microsoft.Extensions.AI` | Execution engine, inbox, coordinator, LLM streaming, SQLite persistence, VCS |
| `@opencode-ai/server` | `OpenCode.Server` | `net10.0` | `OpenCode.Core`, `OpenCode.Protocol`, `Microsoft.AspNetCore.App` | Kestrel Minimal APIs, SSE EventFeed, WebSocket PTY |
| `@opencode-ai/client` | `OpenCode.Client` | `net10.0` | `OpenCode.Schema`, `OpenCode.Protocol`, `System.Net.Http` | Typed HTTP & SSE client for remote or local server |
| `@opencode-ai/sdk` | `OpenCode.Sdk` | `net10.0` | `OpenCode.Client`, `OpenCode.Server`, `OpenCode.Core` | Embedded in-process host, high-level developer SDK |
| `@opencode-ai/cli` | `OpenCode.Cli` | `net10.0` | `OpenCode.Sdk`, `System.CommandLine` | CLI entry point, background service supervisor |
| `@opentui/core` (native) | `OpenTui.Native` | `net10.0` | Native Zig DLL | Source-generated `[LibraryImport]` P/Invoke bindings |
| `@opentui/solid` + UI | `OpenTui.Components` | `net10.0` | `OpenTui.Native` | Native C# reactive terminal component model |

---

## 2. Granular Directory Mappings

### `packages/schema` -> `src/OpenCode.Schema`
- `src/session-id.ts` -> `Ids/SessionId.cs`
- `src/project-id.ts` -> `Ids/ProjectId.cs`
- `src/agent.ts` -> `Agents/AgentInfo.cs`
- `src/session.ts` -> `Sessions/SessionInfo.cs`
- `src/session-message.ts` -> `Sessions/SessionMessage.cs`
- `src/session-inbox.ts` -> `Sessions/SessionInboxItem.cs`
- `src/event.ts` -> `Events/OpenCodeEvent.cs`
- `src/tool.ts` -> `Tools/ToolDefinition.cs`

### `packages/core` -> `src/OpenCode.Core`
- `src/database/sqlite.ts` -> `Database/SqliteDatabase.cs`
- `src/session/sql.ts` -> `Database/Schema/SessionTable.cs`
- `src/session/run-coordinator.ts` -> `Session/SessionRunCoordinator.cs`
- `src/session/session.ts` -> `Session/SessionOperations.cs`
- `src/session/execution.ts` -> `Session/SessionExecution.cs`
- `src/session/runner/index.ts` -> `Session/Runner/SessionRunner.cs`
- `src/session/runner/step.ts` -> `Session/Runner/StepRunner.cs`
- `src/session/inbox.ts` -> `Session/SessionInboxService.cs`
- `src/tool/runtime.ts` -> `Tools/ToolExecutionRuntime.cs`
- `src/location.ts` -> `Location/LocationServices.cs`
- `src/location-service-map.ts` -> `Location/LocationServiceMap.cs`

### `packages/server` -> `src/OpenCode.Server`
- `src/process.ts` -> `Hosting/ServerProcess.cs`
- `src/routes.ts` -> `Routing/RouteRegistry.cs`
- `src/event-feed.ts` -> `Services/EventFeedService.cs`
- `src/handlers/session.ts` -> `Endpoints/SessionEndpoints.cs`
- `src/handlers/event.ts` -> `Endpoints/EventEndpoints.cs`
- `src/auth.ts` -> `Middleware/AuthenticationHandler.cs`

### `packages/sdk` -> `src/OpenCode.Sdk`
- `src/index.ts` -> `OpenCodeSdk.cs`
- `src/promise.ts` -> `EmbeddedHost.cs`
- `src/internal/host.ts` -> `Internal/InProcessHost.cs`
