namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text.Json;

public enum LlmRole { System, User, Assistant, Tool }

public enum LlmCacheKind { Ephemeral, Persistent }
public sealed record LlmCacheHint(LlmCacheKind Kind, double? TtlSeconds = null);
public sealed record LlmSystemPart(string Text)
{
    public LlmCacheHint? Cache { get; init; }
    public ImmutableDictionary<string, JsonElement>? Metadata { get; init; }
}

public abstract record LlmContent
{
    private protected LlmContent() { }
    // Generic annotations are distinct from provider-owned replay state.
    public ImmutableDictionary<string, JsonElement>? Metadata { get; init; }
    public ImmutableDictionary<string, JsonElement> ProviderMetadata { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public LlmCacheHint? Cache { get; init; }

    public sealed record Text(string Value) : LlmContent;
    public sealed record Reasoning(string Value, string? Encrypted = null) : LlmContent;
    // Base64 only: URL fetching and media normalization belong outside this boundary.
    public sealed record Media(string MediaType, string Base64, string? Filename = null) : LlmContent;
    public sealed record ToolCall(string Id, string Name, JsonElement Input, bool ProviderExecuted = false) : LlmContent;
    public sealed record ToolResult(string Id, string Name, LlmToolResult Result, bool ProviderExecuted = false) : LlmContent;
}

public abstract record LlmToolResult
{
    private protected LlmToolResult() { }
    public sealed record Text(string Value) : LlmToolResult;
    public sealed record Json(JsonElement Value) : LlmToolResult;
    public sealed record Error(JsonElement Value) : LlmToolResult;
    public sealed record Content(ImmutableArray<LlmContent> Value) : LlmToolResult;
}

public sealed record LlmMessage(LlmRole Role, ImmutableArray<LlmContent> Content)
{
    public string? Id { get; init; }
    public ImmutableDictionary<string, JsonElement>? Metadata { get; init; }
}
public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema)
{
    public LlmCacheHint? Cache { get; init; }
}

public abstract record LlmToolChoice
{
    private protected LlmToolChoice() { }
    public bool? DisableParallelToolUse { get; init; }
    public sealed record Auto : LlmToolChoice;
    public sealed record None : LlmToolChoice;
    public sealed record Required : LlmToolChoice;
    public sealed record Named(string Name) : LlmToolChoice;
}

public sealed record LlmGeneration(
    double? MaxTokens = null, double? Temperature = null, double? TopP = null, double? TopK = null,
    double? FrequencyPenalty = null, double? PresencePenalty = null, double? Seed = null,
    ImmutableArray<string> Stop = default);

// Explicit compatibility, not guessed capabilities from a provider or model name.
public sealed record LlmCompatibility(
    string? ReasoningField = null, bool RequireReasoning = false, string MaxTokensField = "max_completion_tokens",
    bool RequireFinishReason = true, bool RequireAssistantAfterTool = false,
    bool? SupportsStore = null, bool SupportsUsageInStreaming = true, bool SupportsStrictMode = true,
    bool? RequireSignature = null)
{
    internal static LlmCompatibility Default { get; } = new();
}

public sealed record LlmHttpOptions
{
    public ImmutableDictionary<string, JsonElement> Body { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public ImmutableDictionary<string, string> Headers { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Query { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>One physical provider attempt. ModelId is the exact API ID, not the catalog reference.</summary>
public sealed record LlmRequest(string ModelId, ImmutableArray<LlmMessage> Messages)
{
    public ImmutableArray<LlmSystemPart> System { get; init; } = [];
    public ImmutableArray<LlmToolDefinition> Tools { get; init; } = [];
    public LlmToolChoice? ToolChoice { get; init; }
    public LlmGeneration? Generation { get; init; }
    public ImmutableDictionary<string, JsonElement> ProviderOptions { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public LlmHttpOptions Http { get; init; } = new();
    // An explicitly supplied compatibility record is a complete per-request override.
    public LlmCompatibility Compatibility { get; init; } = LlmCompatibility.Default;
    /// <summary>Generic lineage hint. Chat/Responses lower up to 64 code points;
    /// Anthropic/Gemini accept it without a wire field or a fabricated cache resource.</summary>
    public string? PromptCacheKey { get; init; }
}
