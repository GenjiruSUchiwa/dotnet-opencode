# Vertical Slice 1: Embedded SDK and In-Process Server

This document details the plan and acceptance criteria for the first functional vertical slice of the C# port.

---

## 1. Goal and Definition of Done

The goal of Vertical Slice 1 is to achieve an operational, testable core engine in C# without needing the entire repository ported upfront.

### Definition of Done
1. **In-Memory Embedded Host**: `OpenCode.Sdk.CreateAsync()` boots an in-memory SQLite database, configures dependency injection, runs migrations, and starts an in-process HTTP/SSE server.
2. **Session Lifecycle**:
   - Create a session (`POST /api/session`).
   - Admit a prompt into the inbox (`POST /api/session/{id}/prompt`).
   - Run the execution loop (consume inbox item, emit message, invoke a tool).
3. **Tool Execution**: Execute at least one built-in tool (e.g. `read_file` or `echo`).
4. **Live SSE Streaming**: Subscribe to `/api/event` and receive `session.created`, `message.updated`, and `session.idle` events.
5. **Frontend Interoperability**: Point the existing TypeScript TUI (`bun run dev:live`) at the C# server and observe session rendering in the terminal.

---

## 2. Component Scope for Slice 1

```text
┌─────────────────────────────────────────────────────────┐
│ Existing TUI (bun run dev:live) or C# Console Client    │
└───────────────────────────┬─────────────────────────────┘
                            │ HTTP + SSE (/api/...)
┌───────────────────────────▼─────────────────────────────┐
│ OpenCode.Server (Kestrel Minimal APIs)                  │
│ • /api/health                                           │
│ • /api/session                                          │
│ • /api/session/{id}/prompt                              │
│ • /api/event (SSE dropping queue feed)                  │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│ OpenCode.Core (Domain Execution)                        │
│ • SqliteDatabase (in-memory mode)                       │
│ • SessionInboxService (admission & atomic delivery)     │
│ • SessionRunCoordinator (drain loop & concurrency)      │
│ • Mock / IChatClient LLM Step Runner                    │
│ • Builtin Tool Registry (ReadFile, Echo)                │
└───────────────────────────┬─────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────┐
│ OpenCode.Schema (Contracts)                             │
│ • SessionId, MessageId, ProjectId                       │
│ • SessionInfo, SessionMessage, PromptInput              │
│ • System.Text.Json Source Generator                     │
└─────────────────────────────────────────────────────────┘
```

---

## 3. Step-by-Step Implementation Sequence

### Milestone 1: Contracts and Serialization (`OpenCode.Schema`)
- Implement strongly-typed IDs: `SessionId`, `ProjectId`, `MessageId`.
- Implement core data contracts: `SessionInfo`, `PromptInput`, `SessionMessage`, `TokenUsageInfo`.
- Configure `OpenCodeJsonContext` with `[JsonSerializable]` and verify round-trip JSON serialization.

### Milestone 2: Persistence (`OpenCode.Core.Database`)
- Implement `SqliteDatabase` using `Microsoft.Data.Sqlite`.
- Execute initial DDL migration (`session_v2`, `session_inbox`, `project`).
- Verify atomic transaction helper: `RunInTransactionAsync`.

### Milestone 3: Execution Coordinator and Inbox (`OpenCode.Core.Session`)
- Implement `SessionInboxService`:
  - `EnqueuePromptAsync(sessionId, input)`
  - `DeliverNextItemAsync(sessionId)`
- Implement `SessionRunCoordinator`:
  - Per-session drain loop.
  - Wake coalescing and interruption via `CancellationToken`.
- Implement `StepRunner`:
  - Executes one model turn via `IChatClient` (or mock streaming client).
  - Handles tool calls and tool responses.

### Milestone 4: Kestrel Server and SSE Feed (`OpenCode.Server`)
- Implement `EventFeedService` with `Channel<string>` bounded dropping queues.
- Map routes:
  - `GET /api/health` -> returns `{ "status": "ok", "version": "1.0.0" }`
  - `GET /api/event` -> returns `text/event-stream`
  - `POST /api/session` -> creates session and returns `201 Created`
  - `POST /api/session/{id}/prompt` -> enqueues prompt and wakes coordinator.

### Milestone 5: Embedded SDK Facade (`OpenCode.Sdk`)
- Implement `OpenCodeSdk.CreateAsync(SdkOptions options)`:
  - Starts Kestrel in-process on an ephemeral or loopback port.
  - Returns `IOpenCodeClient` configured to target the local host.
  - Implements `IAsyncDisposable` for clean teardown.

---

## 4. Verification Test (C# Integration Test)

```csharp
[Fact]
public async Task EmbeddedHost_ExecutesSessionPromptEndToEnd()
{
    // 1. Boot embedded host
    await using var sdk = await OpenCodeSdk.CreateAsync(new SdkOptions
    {
        UseInMemoryDatabase = true
    });

    // 2. Create session
    var session = await sdk.Sessions.CreateAsync(new CreateSessionRequest
    {
        Title = "Slice 1 Test Session"
    });
    Assert.NotNull(session.Id);

    // 3. Subscribe to events
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var eventChannel = Channel.CreateUnbounded<OpenCodeEvent>();
    _ = Task.Run(async () =>
    {
        await foreach (var evt in sdk.Events.SubscribeAsync(cts.Token))
        {
            await eventChannel.Writer.WriteAsync(evt);
        }
    });

    // 4. Send prompt
    await sdk.Sessions.PromptAsync(session.Id, new PromptInput
    {
        Text = "Hello OpenCode C#!"
    });

    // 5. Verify delivered events
    var receivedEvent = await eventChannel.Reader.ReadAsync(cts.Token);
    Assert.Equal("session.inbox.delivered", receivedEvent.Type);
}
```
