# Modern .NET 10 Ecosystem Strategy

This document describes how to design the OpenCode C# port so it feels like a native .NET library and integrates deeply with the Microsoft ecosystem.

---

## 1. Native AOT (Ahead-of-Time Compilation)

A primary strength of modern .NET 10 is Native AOT. It compiles C# directly into native machine code (no CLR JIT runtime overhead).

### Benefits for OpenCode
- **Instant Startup**: Launches in under 15ms (essential for CLI developer tools).
- **Minimal Memory**: Base memory footprint under 30MB.
- **Single Executable**: Self-contained single binary (`opencode.exe` or `opencode`) with zero external dependencies (no Node, Bun, or .NET runtime installation required on the machine).

### Guardrails for Native AOT
1. Use `System.Text.Json` source generation (`JsonSerializerContext`). Avoid reflection-based serialization.
2. Use `[LibraryImport]` for native P/Invoke (ConPTY, OpenTUI, LibC).
3. Avoid dynamic assembly loading (`Assembly.LoadFile`). Use compile-time DI registration and source-generated plugins.

---

## 2. Integration with `Microsoft.Extensions.AI`

Microsoft has standardized AI abstractions across .NET with `Microsoft.Extensions.AI`.

Instead of hand-rolling custom provider clients for every LLM, OpenCode Core can implement its model execution against **`IChatClient`**:

```csharp
namespace OpenCode.Core.Llm;

using Microsoft.Extensions.AI;

public sealed class OpenCodeLlmRunner(IChatClient chatClient)
{
    public async IAsyncEnumerable<StreamingChatCompletionUpdate> StreamAttemptAsync(
        IList<ChatMessage> history,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in chatClient.CompleteStreamingAsync(history, options, ct))
        {
            yield return update;
        }
    }
}
```

### Out-of-the-Box Provider Support
Because `Microsoft.Extensions.AI` has official adapters for:
- OpenAI & Azure OpenAI
- Anthropic (via community/official connector)
- Ollama
- AWS Bedrock
- Mistral
- Local models via ONNX Runtime GenAI

OpenCode in C# automatically gains access to all enterprise and local models with zero custom transport code.

---

## 3. Idiomatic C# 13/14 Features

Take full advantage of modern C# syntax to write concise, expressive code:

### Collection Expressions (`[...]`)
```csharp
// Instead of new List<string> { "a", "b" }
IReadOnlyList<string> args = [.. baseArgs, "--verbose", path];
```

### Pattern Matching and Switch Expressions
```csharp
public static bool IsTerminal(SessionOutcome? outcome) => outcome switch
{
    SessionOutcome.Succeeded or SessionOutcome.Failed or SessionOutcome.Interrupted => true,
    null => false,
    _ => false
};
```

### Primary Constructors
```csharp
public sealed class SessionExecutionService(
    ISessionStore store,
    ILocationServiceMap locations,
    ISessionRunCoordinator coordinator,
    ILogger<SessionExecutionService> logger)
{
    // Injected fields are directly available in all member methods
}
```

### `System.Threading.Channels` for Event Feeds
Replaces custom async queues with high-performance ring-buffered channels optimized with zero allocations.
