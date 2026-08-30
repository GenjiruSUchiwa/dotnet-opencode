namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<InboxDeliveryMode>))]
public enum InboxDeliveryMode
{
    [JsonStringEnumMemberName("steer")]
    Steer,
    [JsonStringEnumMemberName("queue")]
    Queue
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UserInboxPayload), "user")]
[JsonDerivedType(typeof(SyntheticInboxPayload), "synthetic")]
[JsonDerivedType(typeof(CompactionInboxPayload), "compaction")]
[JsonDerivedType(typeof(MoveInboxPayload), "move")]
public abstract record InboxPayload;

public sealed record UserInboxPayload(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptFileAttachment>? Files = null,
    [property: JsonPropertyName("agents")] IReadOnlyList<PromptAgentAttachment>? Agents = null,
    [property: JsonPropertyName("skills")] IReadOnlyList<PromptSkillAttachment>? Skills = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : InboxPayload;

public sealed record SyntheticInboxPayload(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : InboxPayload;

public sealed record CompactionInboxPayload : InboxPayload;

public sealed record MoveInboxPayload(
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("projectID")] ProjectId ProjectId,
    [property: JsonPropertyName("subpath")] string? Subpath = null
) : InboxPayload;

/// <summary>
/// 1:1 port of Session.Inbox from packages/schema/src/session-inbox.ts
/// </summary>
public sealed record SessionInboxItem(
    [property: JsonPropertyName("id")] MessageId Id,
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("delivery")] InboxDeliveryMode Delivery,
    [property: JsonPropertyName("payload")] InboxPayload Payload,
    [property: JsonPropertyName("timeCreated")] DateTimeOffset TimeCreated
);
