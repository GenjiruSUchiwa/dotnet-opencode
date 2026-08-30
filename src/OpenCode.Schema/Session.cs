namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("created")] DateTimeOffset Created,
    [property: JsonPropertyName("updated")] DateTimeOffset Updated,
    [property: JsonPropertyName("idle")] DateTimeOffset? Idle = null,
    [property: JsonPropertyName("viewed")] DateTimeOffset? Viewed = null,
    [property: JsonPropertyName("archived")] DateTimeOffset? Archived = null
);

public sealed record SessionForkInfo(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("boundary")] JsonElement Boundary
);

/// <summary>
/// 1:1 port of Session.Info from packages/schema/src/session.ts
/// </summary>
public sealed record SessionInfo(
    [property: JsonPropertyName("id")] SessionId Id,
    [property: JsonPropertyName("projectID")] ProjectId ProjectId,
    [property: JsonPropertyName("cost")] Money Cost,
    [property: JsonPropertyName("tokens")] TokenUsageInfo Tokens,
    [property: JsonPropertyName("time")] SessionTime Time,
    [property: JsonPropertyName("slug")] string? Slug = null,
    [property: JsonPropertyName("directory")] string? Directory = null,
    [property: JsonPropertyName("version")] string Version = "2",
    [property: JsonPropertyName("parentID")] SessionId? ParentId = null,
    [property: JsonPropertyName("fork")] SessionForkInfo? Fork = null,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("model")] ModelRef? Model = null,
    [property: JsonPropertyName("outcome")] SessionOutcome? Outcome = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("location")] string? Location = null,
    [property: JsonPropertyName("subpath")] string? Subpath = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
);
