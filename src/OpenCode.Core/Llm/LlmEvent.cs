namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text.Json;

public enum LlmFinishReason { Stop, Length, ContentFilter, ToolCalls, Error, Unknown }
public sealed record LlmFinish(LlmFinishReason Normalized, string? Raw = null);

/// <summary>Inclusive input/output totals with independent, non-overlapping input breakdowns.</summary>
public sealed record LlmUsage(
    double? InputTokens = null, double? OutputTokens = null, double? NonCachedInputTokens = null,
    double? CacheReadInputTokens = null, double? CacheWriteInputTokens = null, double? ReasoningTokens = null,
    double? TotalTokens = null)
{
    public ImmutableDictionary<string, JsonElement> ProviderMetadata { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public double VisibleOutputTokens => Math.Max(0, (OutputTokens ?? 0) - (ReasoningTokens ?? 0));
}

public abstract record LlmEvent
{
    private protected LlmEvent() { }
    public ImmutableDictionary<string, JsonElement> ProviderMetadata { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    public sealed record StepStart(int Index = 0) : LlmEvent;
    public sealed record TextStart(string Id) : LlmEvent;
    public sealed record TextDelta(string Id, string Text) : LlmEvent;
    public sealed record TextEnd(string Id, string? Text = null) : LlmEvent;
    public sealed record ReasoningStart(string Id) : LlmEvent;
    public sealed record ReasoningDelta(string Id, string Text) : LlmEvent;
    public sealed record ReasoningEnd(string Id, string? Text = null) : LlmEvent;
    public sealed record ToolInputStart(string Id, string Name, bool ProviderExecuted = false) : LlmEvent;
    public sealed record ToolInputDelta(string Id, string Name, string Text, JsonElement? Input = null) : LlmEvent;
    // InputEnd closes the input lifecycle; only ToolCall confirms executable input.
    public sealed record ToolInputEnd(string Id, string Name) : LlmEvent;
    // Raw input is retained for malformed arguments and truncated/filtered calls.
    public sealed record ToolInputError(string Id, string Name, string Raw) : LlmEvent;
    public sealed record ToolCall(string Id, string Name, JsonElement Input, bool ProviderExecuted = false) : LlmEvent;
    public sealed record ToolResult(string Id, string Name, LlmToolResult Result, bool ProviderExecuted = false) : LlmEvent;
    public sealed record ToolError(string Id, string Name, string Message) : LlmEvent;
    public sealed record StepFinish(LlmFinish Reason, LlmUsage? Usage = null, int Index = 0) : LlmEvent;
    public sealed record Finish(LlmFinish Reason, LlmUsage? Usage = null) : LlmEvent;
    public sealed record ProviderError(LlmFailure Reason) : LlmEvent;
    // Preserve provider output that has no canonical content equivalent instead of dropping it.
    public sealed record ProviderState(JsonElement Value) : LlmEvent;
}
