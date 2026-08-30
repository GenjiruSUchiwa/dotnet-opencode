# opencode-dotnet: Modern .NET 10 Port of OpenCode

This document outlines the strategy for an idiomatic port of OpenCode (V2) to modern C# on .NET 10 (`opencode-dotnet`).

## 1. Background and Motivation

The TypeScript LSP and compiler team demonstrated the power of a faithful, idiomatic port with `tsgo` (porting TypeScript to Go). The objective was not an incompatible rewrite with novel abstractions, but a 1:1 structural and behavioral translation into the idioms of the target language.

This project applies the same philosophy to OpenCode:
- **Project Name**: `opencode-dotnet`
- **CLI Executable / Global Tool**: `opencode-dotnet` (or `dotnet-opencode`)
- **Target Platform**: Modern .NET 10 (C# 13/14).
- **Core Philosophy**: Idiomatic C#. Do not force foreign paradigms (such as heavy functional monad libraries) onto C#. Instead, map TypeScript and Effect v4 concepts directly to first-class .NET constructs: `Task`/`ValueTask`, `System.Threading.Channels`, `IAsyncEnumerable<T>`, `CancellationToken`, pattern matching, primary constructors, record types, and `Microsoft.Extensions.*` dependency injection.
- **Performance Profile**: Native AOT compatible by design (using `System.Text.Json` source generation, zero reflection, `[LibraryImport]` source-generated P/Invoke, and zero-allocation span operations).
- **Ecosystem Integration**: Seamless integration with Microsoft's AI stack (`Microsoft.Extensions.AI`, `SemanticKernel`), ASP.NET Core Kestrel, and native cross-platform terminal APIs.

---

## 2. Target Solution Architecture

The .NET solution mirrors the modular package boundaries of OpenCode while adhering to standard .NET solution layout:

```text
opencode-dotnet.sln
├── src/
│   ├── OpenCode.Schema/               # Data contracts, records, strongly-typed IDs, JSON serialization
│   ├── OpenCode.Protocol/             # API request/response definitions, route manifests, event contracts
│   ├── OpenCode.Core/                 # Domain logic: Session execution, inbox, agents, tools, SQLite, git
│   ├── OpenCode.Server/               # ASP.NET Core Minimal APIs, Kestrel hosting, SSE EventFeed, WebSockets
│   ├── OpenCode.Client/               # Typed HTTP + SSE client for OpenCode server
│   ├── OpenCode.Sdk/                  # Embedded in-process host + high-level client facade
│   ├── OpenCode.Cli/                  # Command-line interface, background service coordinator
│   └── OpenCode.OpenTui/              # [LibraryImport] P/Invoke bindings to Zig OpenTUI native library
└── test/
    ├── OpenCode.Schema.Tests/
    ├── OpenCode.Core.Tests/
    ├── OpenCode.Server.Tests/
    └── OpenCode.Sdk.Tests/
```

### Dependency Graph

The runtime dependency direction follows the established architecture:

```text
OpenCode.Schema
       ▲
       │
OpenCode.Protocol
       ▲
       │
OpenCode.Core
       ▲
       │
OpenCode.Server ◄─── OpenCode.Client
       ▲                    ▲
       │                    │
  OpenCode.Sdk ─────────────┘
       ▲
       │
  OpenCode.Cli
```

- `OpenCode.Schema` has zero dependencies beyond the standard library.
- `OpenCode.Client` depends on `OpenCode.Schema` and `OpenCode.Protocol`, never on `Core` or `Server`.
- `OpenCode.Sdk` composes `Client`, `Core`, and `Server` for embedded in-process execution.

---

## 3. High-Level Concept Translation Matrix

| TypeScript / Effect v4 Concept | Modern C# (.NET 10) Equivalent |
| :--- | :--- |
| `Effect.Effect<A, E, R>` | `Task<A>` / `ValueTask<A>` + `CancellationToken` + DI injected services |
| `Schema.Struct({ ... })` | `public sealed record ...` with primary constructors |
| `Schema.Literals([...])` | `enum` or discriminated union records |
| Branded scalar IDs (`Session.ID`) | `readonly record struct SessionId(string Value)` with prefix verification |
| Discriminated unions (`SessionMessage`) | `abstract record` hierarchy with `[JsonPolymorphic]` & switch expressions |
| `Queue.Queue<T>` / `Hub<T>` | `System.Threading.Channels.Channel<T>` (bounded / unbounded) |
| `Stream.Stream<T, E>` | `IAsyncEnumerable<T>` |
| `Scope` / `addFinalizer` | `IAsyncDisposable`, `await using`, `CancellationTokenRegistration` |
| `Deferred<A, E>` | `TaskCompletionSource<A>` |
| `Fiber<A>` | `Task<A>` managed via `TaskCompletionSource` or background loop |
| `Context.Tag` / `Layer` | `Microsoft.Extensions.DependencyInjection` (`IServiceCollection`, `IServiceProvider`) |
| Drizzle ORM + SQLite | `Microsoft.Data.Sqlite` + Dapper / Source-Generated Dapper AOT |
| Effect `HttpApi` | ASP.NET Core Minimal APIs on Kestrel |
| SSE Event Stream (`EventFeed`) | `IAsyncEnumerable<string>` or `Response.BodyWriter` SSE formatting |
| PTY (`node-pty` / `bun-pty`) | Windows ConPTY API via P/Invoke (`[LibraryImport]`), Unix `openpty` |
| Native OpenTUI (Zig) | `[LibraryImport]` source-generated C ABI bindings |

---

## 4. Two Project Outcomes

This porting initiative delivers two primary outcomes:

### Outcome 1: The Porting Ruleset (`/port/csharp/ruleset/`)
A comprehensive, deterministic set of translation rules. Each rulebook provides concrete TypeScript-to-C# code comparisons so human engineers and AI agent fleets can port files mechanically without architectural drift:
- `01-type-system-and-contracts.md`: Schemas, structs, unions, branded IDs, and System.Text.Json source generation.
- `02-concurrency-and-effects.md`: Tasks, channels, tokens, scopes, and coordinator patterns.
- `03-dependency-injection-and-services.md`: Service lifecycles, location scoping, and host layers.
- `04-persistence-and-storage.md`: SQLite schema, migrations, transactions, and JSON column handling.
- `05-process-and-system-apis.md`: Modern .NET 10 Process APIs, process locking, and ConPTY integration.
- `06-http-server-and-sse.md`: Kestrel Minimal APIs, SSE streaming feed, and persistent WebSocket PTY.

### Outcome 2: The First Vertical Slice (`/port/csharp/vertical-slice/`)
A practical, minimal-scope end-to-end slice proving the architecture:
- **Scope**: An embedded `OpenCode.Sdk` in C# that can create an in-memory session, admit prompt inputs, run a step loop, invoke a tool, and emit SSE events.
- **UI Decoupling Strategy**: The existing OpenCode TUI is a decoupled HTTP client. By implementing the Server protocol in C#, the existing TypeScript OpenTUI frontend connects directly to the C# server over HTTP/SSE without waiting for a full TUI port.
- **Future OpenTUI Ecosystem**: OpenTUI's native Zig core is bound via C# P/Invoke, laying the foundation for a first-class native .NET terminal UI framework (`OpenTui.NET`).
