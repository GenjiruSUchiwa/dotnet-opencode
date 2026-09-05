namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public abstract record SessionCompactionEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => SessionEventValidation.Session(SessionId);
    void IJsonOnDeserialized.OnDeserialized() => SessionEventValidation.Session(SessionId);
}

public abstract record SessionCompactionTransitionEventData(SessionId SessionId, string Reason)
    : SessionCompactionEventData(SessionId)
{
    [JsonPropertyName("reason"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Reason { get; init => field = ValidateReason(value); } = ValidateReason(Reason);

    private static string ValidateReason(string value) => value is "auto" or "manual"
        ? value : throw new JsonException("Compaction reason must be auto or manual.");

    protected static MessageId? ValidateInput(MessageId? value)
    {
        if (value is { } id && (!id.IsInitialized() || !id.Value.StartsWith("msg_", StringComparison.Ordinal)))
            throw new JsonException("Compaction inputID must start with msg_.");
        return value;
    }
}

public sealed record SessionCompactionStartedEventData(SessionId SessionId, string Reason, string Recent, MessageId? InputId = null)
    : SessionCompactionTransitionEventData(SessionId, Reason)
{
    [JsonPropertyName("recent"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Recent { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Recent);
    [JsonPropertyName("inputID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<MessageId>))]
    public MessageId? InputId { get; init => field = ValidateInput(value); } = ValidateInput(InputId);
}

public sealed record SessionCompactionDeltaEventData(SessionId SessionId, string Text) : SessionCompactionEventData(SessionId)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

public sealed record SessionCompactionEndedEventData(SessionId SessionId, string Reason, string Text, string Recent)
    : SessionCompactionTransitionEventData(SessionId, Reason)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
    [JsonPropertyName("recent"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Recent { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Recent);
}

public sealed record SessionCompactionFailedEventData(SessionId SessionId, string Reason, SessionStructuredError Error, MessageId? InputId = null)
    : SessionCompactionTransitionEventData(SessionId, Reason)
{
    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);
    [JsonPropertyName("inputID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<MessageId>))]
    public MessageId? InputId { get; init => field = ValidateInput(value); } = ValidateInput(InputId);
}

/// <summary>Shared session-compaction-event.ts notification, not the durable ended event.</summary>
public sealed record SessionCompactedEventData(SessionId SessionId) : SessionCompactionEventData(SessionId);

public static class SessionCompactionEventDefinitions
{
    public static readonly EphemeralEventDefinition<SessionCompactedEventData> Compacted = new(
        "session.compacted", OpenCodeJsonContext.Default.SessionCompactedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Compacted);
}
