namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssistantTextContent), "text")]
[JsonDerivedType(typeof(AssistantReasoningContent), "reasoning")]
[JsonDerivedType(typeof(AssistantToolContent), "tool")]
public abstract record AssistantContent;

public sealed record AssistantTextContent(
    [property: JsonPropertyName("text")] string Text
) : AssistantContent;

public sealed record AssistantReasoningContent(
    [property: JsonPropertyName("text")] string Text
) : AssistantContent;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(ToolStateStreaming), "streaming")]
[JsonDerivedType(typeof(ToolStateRunning), "running")]
[JsonDerivedType(typeof(ToolStateCompleted), "completed")]
[JsonDerivedType(typeof(ToolStateError), "error")]
public abstract record ToolState;

public sealed record ToolStateStreaming(
    [property: JsonPropertyName("input")] string Input
) : ToolState;

public sealed record ToolStateRunning(
    [property: JsonPropertyName("input")] IReadOnlyDictionary<string, object> Input,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object>? Metadata = null
) : ToolState;

public sealed record ToolStateCompleted(
    [property: JsonPropertyName("input")] IReadOnlyDictionary<string, object> Input,
    [property: JsonPropertyName("content")] IReadOnlyList<string> Content,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object>? Metadata = null
) : ToolState;

public sealed record ToolStateError(
    [property: JsonPropertyName("input")] IReadOnlyDictionary<string, object> Input,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("content")] IReadOnlyList<string>? Content = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object>? Metadata = null
) : ToolState;

public sealed record AssistantToolContent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] ToolState State
) : AssistantContent;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentSelectedMessage), "agent-switched")]
[JsonDerivedType(typeof(ModelSelectedMessage), "model-switched")]
[JsonDerivedType(typeof(LocationSwitchedMessage), "location-switched")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(SyntheticMessage), "synthetic")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(SkillMessage), "skill")]
[JsonDerivedType(typeof(ShellMessage), "shell")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(CompactionMessage), "compaction")]
public abstract record SessionMessage
{
    [JsonPropertyName("id")]
    public required MessageId Id { get; init; }

    [JsonPropertyName("time")]
    public required MessageTime Time { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}

public sealed record MessageTime(
    [property: JsonPropertyName("created")] DateTimeOffset Created,
    [property: JsonPropertyName("completed")] DateTimeOffset? Completed = null
);

public sealed record AgentSelectedMessage : SessionMessage
{
    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("previous")]
    public string? Previous { get; init; }
}

public sealed record ModelSelectedMessage : SessionMessage
{
    [JsonPropertyName("model")]
    public required ModelRef Model { get; init; }

    [JsonPropertyName("previous")]
    public ModelRef? Previous { get; init; }
}

public sealed record LocationSwitchedMessage : SessionMessage
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("projectID")]
    public ProjectId? ProjectId { get; init; }
}

public sealed record UserMessage : SessionMessage
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<string>? Files { get; init; }
}

public sealed record SyntheticMessage : SessionMessage
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record SystemMessage : SessionMessage
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record SkillMessage : SessionMessage
{
    [JsonPropertyName("skill")]
    public required string SkillId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record ShellMessage : SessionMessage
{
    [JsonPropertyName("shellID")]
    public required string ShellId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("exit")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("output")]
    public string? Output { get; init; }
}

public sealed record AssistantMessage : SessionMessage
{
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("model")]
    public ModelRef? Model { get; init; }

    [JsonPropertyName("content")]
    public required IReadOnlyList<AssistantContent> Content { get; init; }

    [JsonPropertyName("cost")]
    public Money? Cost { get; init; }

    [JsonPropertyName("tokens")]
    public TokenUsageInfo? Tokens { get; init; }
}

public sealed record CompactionMessage : SessionMessage
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
