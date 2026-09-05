namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SessionOutcomeJsonConverter))]
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
    [property: JsonPropertyName("created"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset Created,
    [property: JsonPropertyName("updated"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset Updated,
    [property: JsonPropertyName("idle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Idle = null,
    [property: JsonPropertyName("viewed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Viewed = null,
    [property: JsonPropertyName("archived"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Archived = null
);

public sealed record SessionForkInfo(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("boundary"), JsonRequired] ForkBoundary Boundary
);

/// <summary>
/// Session wire fields; legacy store convenience fields are not emitted.
/// </summary>
public sealed record SessionInfo(
    [property: JsonPropertyName("id"), JsonRequired] SessionId Id,
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("cost"), JsonRequired] Money Cost,
    [property: JsonPropertyName("tokens"), JsonRequired] TokenUsageInfo Tokens,
    [property: JsonPropertyName("time"), JsonRequired] SessionTime Time,
    [property: JsonIgnore] string? Slug = null,
    [property: JsonIgnore] string? Directory = null,
    [property: JsonIgnore] string Version = "2",
    [property: JsonPropertyName("parentID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionId? ParentId = null,
    [property: JsonPropertyName("fork"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionForkInfo? Fork = null,
    [property: JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModelRef? Model = null,
    [property: JsonPropertyName("outcome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionOutcome? Outcome = null,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonPropertyName("location"), JsonRequired] LocationRef Location = default!,
    [property: JsonPropertyName("subpath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subpath = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("revert"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SessionRevert? Revert = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    private void Validate()
    {
        if (!Id.IsInitialized() || !ProjectId.IsInitialized())
            throw new JsonException("Session requires id and projectID.");
        if (Location?.Directory is null || Tokens is null || Time is null)
            throw new JsonException("Session requires location, tokens, and time.");
        if (Fork is not null && Fork.Boundary is null)
            throw new JsonException("Session fork requires a boundary.");
        if (Model is not null && (Model.Id is null || Model.ProviderId is null))
            throw new JsonException("Model reference requires id and providerID.");
    }
}
