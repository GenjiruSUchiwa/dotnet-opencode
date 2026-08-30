namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record SessionCreatedEventData(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("projectID")] ProjectId ProjectId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("model")] ModelRef? Model = null,
    [property: JsonPropertyName("parentID")] SessionId? ParentId = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
);

public sealed record SessionInboxEnqueuedEventData(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("id")] MessageId Id,
    [property: JsonPropertyName("delivery")] InboxDeliveryMode Delivery,
    [property: JsonPropertyName("payload")] InboxPayload Payload
);

public sealed record SessionInboxDeliveredEventData(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("id")] MessageId Id
);

public sealed record SessionIdleEventData(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("outcome")] SessionOutcome Outcome
);
