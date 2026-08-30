# OpenTUI and Terminal UI Strategy in C#

This document analyzes OpenTUI, explains how the frontend integrates with the C# backend, and outlines a roadmap for a first-class C# / .NET terminal ecosystem.

---

## 1. Deconstructing OpenTUI

OpenTUI is architected into three distinct layers:

```text
┌─────────────────────────────────────────────────────────┐
│ SolidJS Component Layer (@opentui/solid)               │
│ <box>, <text>, <input>, createSignal, createMemo       │
└───────────────────────────┬─────────────────────────────┘
                            │ reconciler tree updates
┌───────────────────────────▼─────────────────────────────┐
│ High-Level JS Bindings (@opentui/core)                 │
│ BoxRenderable, TextRenderable, CliRenderer             │
└───────────────────────────┬─────────────────────────────┘
                            │ FFI / C ABI calls
┌───────────────────────────▼─────────────────────────────┐
│ OpenTUI Native Core (Zig)                              │
│ opentui.dll / libopentui.so / libopentui.dylib          │
│ • Terminal escape sequences & ConPTY / Termios          │
│ • Double-buffered dirty grid renderer                   │
│ • Flexbox layout engine (Yoga-like)                     │
│ • Tree-sitter syntax highlighting                       │
└─────────────────────────────────────────────────────────┘
```

The native core is written in **Zig**. It compiles to a native shared dynamic library exposing a clean C ABI. It is fast, lightweight, and completely decoupled from JavaScript or Node.js.

---

## 2. Decoupled Architecture in OpenCode

In OpenCode, **the TUI is an HTTP and SSE client**.

Inspect `packages/tui/src/context/client.tsx`:
- The TUI communicates with the server through `OpenCodeClient` (REST API).
- It receives live state updates through `/api/event` (SSE).
- It runs interactive shells via `/api/pty` (WebSocket).

The TUI has **zero direct in-process coupling to `@opencode-ai/core`**.

### The Phase 1 Advantage
Because the client and server communicate via standard HTTP/SSE contracts:
1. We can implement `OpenCode.Server` in C# (.NET 10).
2. We can run the existing TypeScript OpenTUI frontend (`bun run dev:live`) against the C# server.
3. The existing UI functions identically with zero changes to JSX or SolidJS components.

This decouples the backend port from the frontend port, allowing immediate validation of the C# core and server.

---

## 3. Can We Use SolidJS from C#?

There are two potential ways to run the existing SolidJS UI in C#:

### Option A: Embedded JavaScript Engine (ClearScript V8 or QuickJS)
- Use Microsoft ClearScript (V8) or a lightweight QuickJS .NET wrapper to execute the compiled SolidJS bundle inside the C# process.
- The C# host injects native OpenTUI FFI bindings into the JS environment.
- **Trade-offs**:
  - *Pros*: Reuses 100% of existing TSX components.
  - *Cons*: Heavy runtime overhead (V8 is large), complex debugging across the C#/JS boundary, and breaks **Native AOT** compilation for the single-binary CLI.

### Option B: Decoupled Process Hosting (Recommended for Hybrid Mode)
- The C# CLI launcher bundles or resolves Bun/Node and launches the TUI frontend as a child process, connecting via localhost HTTP and named pipes/loopback.
- Clean separation, zero interop overhead, and simple diagnostics.

---

## 4. Building the C# OpenTUI Native Ecosystem (`OpenTui.NET`)

OpenTUI's native Zig engine is a great asset for the Microsoft/.NET ecosystem. Currently, .NET developers have `Spectre.Console` and `Terminal.Gui`, but neither matches OpenTUI's Zig-accelerated flexbox rendering and tree-sitter integration.

We can establish `OpenTui.NET` as a premier .NET terminal UI engine:

### 1. `OpenTui.Native` (Source-Generated P/Invoke)
Bind OpenTUI's C ABI using .NET 10 `[LibraryImport]`:

```csharp
namespace OpenTui.Native;

using System.Runtime.InteropServices;

public static partial class OpenTuiAbi
{
    private const string LibName = "opentui";

    [LibraryImport(LibName, EntryPoint = "opentui_renderer_create")]
    public static partial IntPtr CreateRenderer(ref RendererConfig config);

    [LibraryImport(LibName, EntryPoint = "opentui_renderer_destroy")]
    public static partial void DestroyRenderer(IntPtr renderer);

    [LibraryImport(LibName, EntryPoint = "opentui_node_create_box")]
    public static partial IntPtr CreateBoxNode(IntPtr renderer);

    [LibraryImport(LibName, EntryPoint = "opentui_node_set_style")]
    public static partial void SetNodeStyle(IntPtr node, ref NodeStyle style);
}
```

### 2. Declarative C# Component Model (`OpenTui.Markup` / `OpenTui.Reactive`)
Provide a native, type-safe C# declarative DSL inspired by SwiftUI / MAUI:

```csharp
public sealed class ChatView : Component
{
    [State] private string _input = "";
    [State] private List<ChatMessage> _messages = [];

    public override Node Render() =>
        Box(direction: Direction.Column,
            Header("OpenCode C# .NET 10"),
            ScrollArea(
                ForEach(_messages, msg => MessageView(msg))
            ),
            Input(
                Value: _input,
                Placeholder: "Ask a question...",
                OnSubmit: text => SendMessage(text)
            )
        );
}
```

### 3. Benefits to the Ecosystem
- **True Native AOT**: Single standalone binary with instant startup (<15ms) and tiny memory footprint (<30MB).
- **High Performance**: Native Zig rendering engine with direct memory access from C# spans.
- **Enterprise Appeal**: Native C# toolchain without requiring Bun or Node.js on corporate developer machines.
