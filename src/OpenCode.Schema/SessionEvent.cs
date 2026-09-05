namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record SessionCreatedEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("slug"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Slug,
    [property: JsonPropertyName("version"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Version,
    [property: JsonPropertyName("location"), JsonRequired, JsonConverter(typeof(EventLocationJsonConverter))] LocationRef Location,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Title = null,
    [property: JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Agent = null,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))] ModelRef? Model = null,
    [property: JsonPropertyName("parentID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SessionId>))] SessionId? ParentId = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("subpath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Subpath = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        SessionEventValidation.Session(SessionId);
        PromptValidation.Required(ProjectId.Value);
        PromptValidation.Required(Slug);
        PromptValidation.Required(Version);
        EventLocationJsonConverter.Validate(Location);
        if (Model is not null) MessageContract.Model(Model);
        if (ParentId is { } parent) SessionEventValidation.Session(parent);
    }
}

public sealed record SessionInboxEnqueuedEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("inboxID"), JsonRequired] MessageId InboxId,
    [property: JsonPropertyName("item"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<InboxItem>))] InboxItem Item
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        SessionEventValidation.Reference(SessionId, InboxId);
        PromptValidation.Required(Item);
    }
}

public sealed record SessionInboxDeliveredEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("inboxID"), JsonRequired] MessageId InboxId
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => SessionEventValidation.Reference(SessionId, InboxId);
    void IJsonOnDeserialized.OnDeserialized() => SessionEventValidation.Reference(SessionId, InboxId);
}

public sealed record SessionIdleEventData(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("outcome")] SessionOutcome Outcome
);

// Data contracts only. Durable registration, versions, publication, and projection belong to Core.
public abstract record SessionAssistantEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("assistantMessageID"), JsonRequired] MessageId AssistantMessageId
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    protected virtual void Validate()
    {
        if (!SessionId.IsInitialized() || !SessionId.Value.StartsWith("ses", StringComparison.Ordinal)
            || !AssistantMessageId.IsInitialized() || !AssistantMessageId.Value.StartsWith("msg_", StringComparison.Ordinal))
            throw new JsonException("Assistant event requires sessionID and assistantMessageID.");
    }
}

public sealed record SessionStepStartedEventData(
    SessionId SessionId, MessageId AssistantMessageId,
    string Agent, ModelRef Model,
    [property: JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SnapshotId>))] SnapshotId? Snapshot = null
) : SessionAssistantEventData(SessionId, AssistantMessageId)
{
    [JsonPropertyName("agent"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Agent { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Agent);
    [JsonPropertyName("model"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))]
    public ModelRef Model { get; init => field = MessageContract.Model(value); } = MessageContract.Model(Model);
}

public sealed record SessionStepStreamedEventData(SessionId SessionId, MessageId AssistantMessageId)
    : SessionAssistantEventData(SessionId, AssistantMessageId);

public sealed record SessionStepEndedEventData(
    SessionId SessionId, MessageId AssistantMessageId,
    [property: JsonPropertyName("finish"), JsonRequired, JsonConverter(typeof(LlmFinishReasonJsonConverter))] LlmFinishReason Finish,
    [property: JsonPropertyName("cost"), JsonRequired] Money Cost,
    TokenUsageInfo Tokens,
    [property: JsonPropertyName("rawFinish"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? RawFinish = null,
    [property: JsonPropertyName("providerState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? ProviderState = null,
    [property: JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SnapshotId>))] SnapshotId? Snapshot = null,
    [property: JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Files = null
) : SessionAssistantEventData(SessionId, AssistantMessageId)
{
    [JsonPropertyName("tokens"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TokenUsageInfo>))]
    public TokenUsageInfo Tokens { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Tokens);
}

public sealed record SessionStepFailedEventData(
    SessionId SessionId, MessageId AssistantMessageId, SessionStructuredError Error,
    string? Finish = null,
    [property: JsonPropertyName("rawFinish"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? RawFinish = null,
    [property: JsonPropertyName("providerState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? ProviderState = null,
    [property: JsonPropertyName("cost"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<Money>))] Money? Cost = null,
    [property: JsonPropertyName("tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<TokenUsageInfo>))] TokenUsageInfo? Tokens = null,
    [property: JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SnapshotId>))] SnapshotId? Snapshot = null,
    [property: JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Files = null
) : SessionAssistantEventData(SessionId, AssistantMessageId)
{
    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);
    [JsonPropertyName("finish"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Finish { get; init => field = Filter(value); } = Filter(Finish);
    private static string? Filter(string? value) => value is null or "content-filter" ? value : throw new JsonException("Failed step finish must be content-filter.");
}

public abstract record SessionOrdinalEventData(SessionId SessionId, MessageId AssistantMessageId, double Ordinal)
    : SessionAssistantEventData(SessionId, AssistantMessageId)
{
    [JsonPropertyName("ordinal"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Ordinal { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Ordinal, 0);
}

public sealed record SessionTextStartedEventData(SessionId SessionId, MessageId AssistantMessageId, double Ordinal)
    : SessionOrdinalEventData(SessionId, AssistantMessageId, Ordinal);

/// <summary>Shared data shape for live-only text.delta and reasoning.delta.</summary>
public sealed record SessionContentDeltaEventData(SessionId SessionId, MessageId AssistantMessageId, double Ordinal, string Delta)
    : SessionOrdinalEventData(SessionId, AssistantMessageId, Ordinal)
{
    [JsonPropertyName("delta"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Delta { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Delta);
}

/// <summary>Shared full-value data shape for durable text.ended and reasoning.ended.</summary>
public sealed record SessionContentEndedEventData(
    SessionId SessionId, MessageId AssistantMessageId, double Ordinal, string Text,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? State = null
) : SessionOrdinalEventData(SessionId, AssistantMessageId, Ordinal)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

public sealed record SessionReasoningStartedEventData(
    SessionId SessionId, MessageId AssistantMessageId, double Ordinal,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? State = null
) : SessionOrdinalEventData(SessionId, AssistantMessageId, Ordinal);

public abstract record SessionToolEventData(SessionId SessionId, MessageId AssistantMessageId, string Id)
    : SessionAssistantEventData(SessionId, AssistantMessageId)
{
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Id { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Id);
}

public sealed record SessionToolInputStartedEventData(SessionId SessionId, MessageId AssistantMessageId, string Id, string Name)
    : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
}

public sealed record SessionToolInputDeltaEventData(SessionId SessionId, MessageId AssistantMessageId, string Id, string Delta)
    : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("delta"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Delta { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Delta);
}

public sealed record SessionToolInputEndedEventData(SessionId SessionId, MessageId AssistantMessageId, string Id, string Text)
    : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

public sealed record SessionToolCalledEventData(
    SessionId SessionId, MessageId AssistantMessageId, string Id, IReadOnlyDictionary<string, JsonElement> Input,
    [property: JsonPropertyName("executed"), JsonRequired] bool Executed,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? State = null
) : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("input"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Input { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Input);
}

public sealed record SessionToolProgressEventData(SessionId SessionId, MessageId AssistantMessageId, string Id, IReadOnlyDictionary<string, JsonElement> Metadata)
    : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("metadata"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Metadata);
}

public sealed record SessionToolSuccessEventData(
    SessionId SessionId, MessageId AssistantMessageId, string Id, IReadOnlyList<ToolContent> Content,
    [property: JsonPropertyName("executed"), JsonRequired] bool Executed,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("resultState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? ResultState = null
) : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("content"), JsonRequired, JsonConverter(typeof(NonEmptyToolContentJsonConverter))]
    public IReadOnlyList<ToolContent> Content { get; init => field = MessageContract.NonEmpty(value); } = MessageContract.NonEmpty(Content);
}

public sealed record SessionToolFailedEventData(
    SessionId SessionId, MessageId AssistantMessageId, string Id, SessionStructuredError Error,
    [property: JsonPropertyName("executed"), JsonRequired] bool Executed,
    IReadOnlyList<ToolContent>? Content = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("resultState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? ResultState = null
) : SessionToolEventData(SessionId, AssistantMessageId, Id)
{
    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);
    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonEmptyToolContentJsonConverter))]
    public IReadOnlyList<ToolContent>? Content { get; init => field = value is null ? null : MessageContract.NonEmpty(value); } = Content is null ? null : MessageContract.NonEmpty(Content);
}

public sealed record SessionRetryScheduledEventData(SessionId SessionId, MessageId AssistantMessageId, double Attempt, double At, SessionStructuredError Error)
    : SessionAssistantEventData(SessionId, AssistantMessageId)
{
    [JsonPropertyName("attempt"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Attempt { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Attempt, 1);
    // The event stores a nonnegative integer; projected AssistantRetry.At is a DateTime value.
    [JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double At { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(At, 0);
    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);
}

public sealed record SessionMessageContentUpdatedEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId,
    IReadOnlyList<AssistantContent> Content
) : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("content"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<AssistantContent>))]
    public IReadOnlyList<AssistantContent> Content { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Content));
    void IJsonOnSerializing.OnSerializing() => SessionEventValidation.Reference(SessionId, MessageId);
    void IJsonOnDeserialized.OnDeserialized() => SessionEventValidation.Reference(SessionId, MessageId);
}

public sealed record SessionUsageRecordedEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    string Source,
    [property: JsonPropertyName("cost"), JsonRequired] Money Cost,
    TokenUsageInfo Tokens
) : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("source"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Source { get; init => field = ValidateSource(value); } = ValidateSource(Source);
    [JsonPropertyName("tokens"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TokenUsageInfo>))]
    public TokenUsageInfo Tokens { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Tokens);
    private static string ValidateSource(string value) => value is "title" or "compaction" ? value : throw new JsonException("Unknown usage source.");
    void IJsonOnSerializing.OnSerializing() => SessionEventValidation.Session(SessionId);
    void IJsonOnDeserialized.OnDeserialized() => SessionEventValidation.Session(SessionId);
}

/// <summary>Projected usage totals; an ephemeral notification, not a durable usage fact.</summary>
public sealed record SessionUsageUpdatedEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("cost"), JsonRequired] Money Cost,
    TokenUsageInfo Tokens
) : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("tokens"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<TokenUsageInfo>))]
    public TokenUsageInfo Tokens { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Tokens);
    void IJsonOnSerializing.OnSerializing() => SessionEventValidation.Session(SessionId);
    void IJsonOnDeserialized.OnDeserialized() => SessionEventValidation.Session(SessionId);
}

internal static class SessionEventValidation
{
    internal static void Session(SessionId id)
    {
        if (!id.IsInitialized() || !id.Value.StartsWith("ses", StringComparison.Ordinal))
            throw new JsonException("Event data requires sessionID.");
    }

    internal static void Reference(SessionId sessionId, MessageId messageId)
    {
        Session(sessionId);
        if (!messageId.IsInitialized() || !messageId.Value.StartsWith("msg_", StringComparison.Ordinal))
            throw new JsonException("Event data requires a msg_ message or inbox ID.");
    }
}
