namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(InboxDeliveryModeJsonConverter))]
public enum InboxDeliveryMode
{
    [JsonStringEnumMemberName("steer")]
    Steer,
    [JsonStringEnumMemberName("queue")]
    Queue
}

[JsonDerivedType(typeof(UserInboxPayload))]
[JsonDerivedType(typeof(SyntheticInboxPayload))]
[JsonDerivedType(typeof(CompactionInboxPayload))]
[JsonDerivedType(typeof(MoveInboxPayload))]
public abstract record InboxPayload;

public sealed record UserInboxPayload(
    [property: JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Text,
    [property: JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptFileAttachment>))] IReadOnlyList<PromptFileAttachment>? Files = null,
    [property: JsonPropertyName("agents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptAgentAttachment>))] IReadOnlyList<PromptAgentAttachment>? Agents = null,
    [property: JsonPropertyName("skills"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptSkillAttachment>))] IReadOnlyList<PromptSkillAttachment>? Skills = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : InboxPayload, IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    private void Validate()
    {
        if (Text is null) throw new JsonException("User inbox payload requires text.");
    }
}

public sealed record SyntheticInboxPayload(
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : InboxPayload, IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    private void Validate()
    {
        if (Text is null) throw new JsonException("Synthetic inbox payload requires text.");
    }
}

public sealed record CompactionInboxPayload : InboxPayload;

public sealed record MoveInboxPayload(
    [property: JsonPropertyName("location"), JsonRequired] LocationRef Location,
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("subpath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subpath = null
) : InboxPayload, IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    private void Validate()
    {
        if (Location?.Directory is null || !ProjectId.IsInitialized())
            throw new JsonException("Move inbox payload requires location and projectID.");
    }
}

/// <summary>Session.Inbox.Item: a tagged payload and delivery mode, before admission.</summary>
[JsonConverter(typeof(InboxItemJsonConverter))]
public sealed record InboxItem(InboxDeliveryMode Delivery, InboxPayload Payload)
{
    public string Type => InboxJson.Type(Payload);
}

/// <summary>
/// Session.Inbox.Info: Item plus the Enqueued fields. Name retained for admission callers.
/// </summary>
[JsonConverter(typeof(SessionInboxItemJsonConverter))]
public sealed record SessionInboxItem(
    [property: JsonPropertyName("id")] MessageId Id,
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("delivery")] InboxDeliveryMode Delivery,
    [property: JsonPropertyName("payload")] InboxPayload Payload,
    [property: JsonPropertyName("timeCreated"), JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset TimeCreated
)
{
    public string Type => InboxJson.Type(Payload);
}
