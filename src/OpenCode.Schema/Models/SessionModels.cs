namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record Money(double Usd);

public sealed record TokenUsageInfo(
    long Input = 0,
    long Output = 0,
    long Reasoning = 0,
    long CacheRead = 0,
    long CacheWrite = 0
);

[JsonConverter(typeof(JsonStringEnumConverter<SessionOutcome>))]
public enum SessionOutcome
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("interrupted")]
    Interrupted
}

public sealed record SessionTime(
    DateTimeOffset Created,
    DateTimeOffset Updated,
    DateTimeOffset? Idle = null,
    DateTimeOffset? Viewed = null,
    DateTimeOffset? Archived = null
);

public sealed record ModelRef(
    string ProviderId,
    string ModelId,
    string? Variant = null
);

public sealed record SessionInfo(
    SessionId Id,
    ProjectId ProjectId,
    string Slug,
    string Directory,
    SessionTime Time,
    TokenUsageInfo Tokens,
    Money Cost,
    string Version = "2",
    SessionId? ParentId = null,
    string? Title = null,
    ModelRef? Model = null,
    string? Agent = null,
    SessionOutcome? Outcome = null
);

public sealed record PromptInput(
    string Text,
    IReadOnlyList<string>? Attachments = null,
    bool Resume = true
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserPromptMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolCallMessage), "tool-call")]
[JsonDerivedType(typeof(ToolResultMessage), "tool-result")]
public abstract record SessionMessage
{
    public required MessageId Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record UserPromptMessage : SessionMessage
{
    public required string Text { get; init; }
    public IReadOnlyList<string>? Attachments { get; init; }
}

public sealed record AssistantMessage : SessionMessage
{
    public required string Text { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    public TokenUsageInfo? Tokens { get; init; }
}

public sealed record ToolCallMessage : SessionMessage
{
    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
}

public sealed record ToolResultMessage : SessionMessage
{
    public required string CallId { get; init; }
    public required string Output { get; init; }
    public bool IsError { get; init; }
}
