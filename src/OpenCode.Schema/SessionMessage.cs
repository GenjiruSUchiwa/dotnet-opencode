namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssistantTextContent), "text")]
[JsonDerivedType(typeof(AssistantReasoningContent), "reasoning")]
[JsonDerivedType(typeof(AssistantToolContent), "tool")]
public abstract record AssistantContent;

public sealed record AssistantTextContent(
    string Text,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? State = null
) : AssistantContent
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

public sealed record AssistantReasoningContent(
    string Text,
    [property: JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? State = null,
    [property: JsonPropertyName("time"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(CompletedMessageTimeJsonConverter))] MessageTime? Time = null
) : AssistantContent
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(ToolStateStreaming), "streaming")]
[JsonDerivedType(typeof(ToolStateRunning), "running")]
[JsonDerivedType(typeof(ToolStateCompleted), "completed")]
[JsonDerivedType(typeof(ToolStateError), "error")]
public abstract record ToolState;

public sealed record ToolStateStreaming(string Input) : ToolState
{
    [JsonPropertyName("input"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Input { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Input);
}

public sealed record ToolStateRunning(
    IReadOnlyDictionary<string, JsonElement> Input,
    IReadOnlyDictionary<string, JsonElement> Metadata
) : ToolState
{
    [JsonPropertyName("input"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Input { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Input);

    [JsonPropertyName("metadata"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Metadata);
}

public sealed record ToolStateCompleted(
    IReadOnlyDictionary<string, JsonElement> Input,
    IReadOnlyList<ToolContent> Content,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : ToolState
{
    [JsonPropertyName("input"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Input { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Input);

    [JsonPropertyName("content"), JsonRequired, JsonConverter(typeof(NonEmptyToolContentJsonConverter))]
    public IReadOnlyList<ToolContent> Content { get; init => field = MessageContract.NonEmpty(value); } = MessageContract.NonEmpty(Content);
}

public sealed record ToolStateError(
    IReadOnlyDictionary<string, JsonElement> Input,
    SessionStructuredError Error,
    IReadOnlyList<ToolContent>? Content = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : ToolState
{
    [JsonPropertyName("input"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Input { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Input);

    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);

    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonEmptyToolContentJsonConverter))]
    public IReadOnlyList<ToolContent>? Content { get; init => field = value is null ? null : MessageContract.NonEmpty(value); } = Content is null ? null : MessageContract.NonEmpty(Content);
}

public sealed record AssistantToolContent(
    string Id,
    string Name,
    ToolState State,
    AssistantToolTime Time,
    [property: JsonPropertyName("executed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Executed = null,
    [property: JsonPropertyName("providerState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? ProviderState = null,
    [property: JsonPropertyName("providerResultState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? ProviderResultState = null
) : AssistantContent
{
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Id { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Id);
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("state"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ToolState>))]
    public ToolState State { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(State);
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<AssistantToolTime>))]
    public AssistantToolTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
}

public sealed record AssistantToolTime(
    [property: JsonPropertyName("created"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset Created,
    [property: JsonPropertyName("ran"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Ran = null,
    [property: JsonPropertyName("completed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Completed = null
);

public sealed record AssistantRetry(
    double Attempt,
    [property: JsonPropertyName("at"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset At,
    SessionStructuredError Error
)
{
    [JsonPropertyName("attempt"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Attempt { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Attempt, 1);
    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);
}

public sealed record AssistantSnapshot(
    [property: JsonPropertyName("start"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SnapshotId>))] SnapshotId? Start = null,
    [property: JsonPropertyName("end"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SnapshotId>))] SnapshotId? End = null,
    [property: JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Files = null
);

[JsonConverter(typeof(SessionMessageJsonConverter))]
public abstract record SessionMessage : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("type"), JsonRequired]
    public string Type
    {
        get => this switch
        {
            AgentSelectedMessage => "agent-switched", ModelSelectedMessage => "model-switched",
            LocationSwitchedMessage => "location-switched", UserMessage => "user", SyntheticMessage => "synthetic",
            SystemMessage => "system", SkillMessage => "skill", ShellMessage => "shell", AssistantMessage => "assistant",
            CompactionMessage => "compaction", _ => throw new JsonException("Unknown session message type.")
        };
        init
        {
            if (value != Type) throw new JsonException("Message type does not match its concrete variant.");
        }
    }

    [JsonPropertyName("id")]
    public required MessageId Id { get; init; }

    [JsonPropertyName("time")]
    [JsonConverter(typeof(CreatedMessageTimeJsonConverter))]
    public virtual required MessageTime Time { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    protected virtual void Validate()
    {
        if (!Id.IsInitialized() || !Id.Value.StartsWith("msg_", StringComparison.Ordinal) || Time is null)
            throw new JsonException("Session message requires a msg_ id and time.");
    }
}

public sealed record MessageTime(
    [property: JsonPropertyName("created"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset Created,
    [property: JsonPropertyName("completed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Completed = null,
    [property: JsonPropertyName("streamed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalEpochMillisecondsJsonConverter))] DateTimeOffset? Streamed = null
);

public sealed record AgentSelectedMessage : SessionMessage
{
    [JsonPropertyName("agent")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Agent { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("previous")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Previous { get; init; }
}

public sealed record ModelSelectedMessage : SessionMessage
{
    [JsonPropertyName("model")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))]
    public required ModelRef Model { get; init => field = MessageContract.Model(value); }

    [JsonPropertyName("previous")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))]
    public ModelRef? Previous { get; init => field = value is null ? null : MessageContract.Model(value); }
}

public sealed record LocationSwitchedMessage : SessionMessage
{
    [JsonPropertyName("location")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<LocationRef>))]
    public required LocationRef Location { get; init; }

    [JsonPropertyName("projectID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(OptionalValueJsonConverter<ProjectId>))]
    public ProjectId? ProjectId { get; init; }

    [JsonPropertyName("subpath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Subpath { get; init; }

    [JsonPropertyName("previous")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<MessageLocation>))]
    public MessageLocation? Previous { get; init; }

    protected override void Validate()
    {
        base.Validate();
        if (Location?.Directory is null || (Previous is not null && Previous.Location?.Directory is null))
            throw new JsonException("Location switch requires a location reference.");
    }
}

public sealed record MessageLocation(
    [property: JsonPropertyName("location"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<LocationRef>))] LocationRef Location,
    [property: JsonPropertyName("projectID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<ProjectId>))] ProjectId? ProjectId = null,
    [property: JsonPropertyName("subpath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Subpath = null
);

public sealed record UserMessage : SessionMessage
{
    [JsonPropertyName("text")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Text { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptFileAttachment>))]
    public IReadOnlyList<PromptFileAttachment>? Files { get; init => field = PromptValidation.Attachments(value); }

    [JsonPropertyName("agents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptAgentAttachment>))]
    public IReadOnlyList<PromptAgentAttachment>? Agents { get; init => field = PromptValidation.Attachments(value); }

    [JsonPropertyName("skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptSkillAttachment>))]
    public IReadOnlyList<PromptSkillAttachment>? Skills { get; init => field = PromptValidation.Attachments(value); }
}

public sealed record SyntheticMessage : SessionMessage
{
    [JsonPropertyName("text")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Text { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Description { get; init; }
}

public sealed record SystemMessage : SessionMessage
{
    [JsonPropertyName("text")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Text { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Description { get; init; }
}

public sealed record SkillMessage : SessionMessage
{
    [JsonPropertyName("skill")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string SkillId { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Name { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("text")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Text { get; init => field = PromptValidation.Required(value); }
}

public sealed record ShellMessage : SessionMessage
{
    [JsonPropertyName("time"), JsonConverter(typeof(CompletedMessageTimeJsonConverter))]
    public override required MessageTime Time { get; init; }

    [JsonPropertyName("shellID")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string ShellId { get; init => field = value?.StartsWith("sh_", StringComparison.Ordinal) == true ? value : throw new JsonException("Shell ID requires sh_ prefix."); }

    [JsonPropertyName("command")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Command { get; init => field = PromptValidation.Required(value); }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Status { get; init => field = value is "running" or "exited" or "timeout" or "killed" ? value : throw new JsonException("Unknown shell status."); }

    [JsonPropertyName("exit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(OptionalValueJsonConverter<double>))]
    public double? ExitCode { get; init; }

    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<ShellOutput>))]
    public ShellOutput? Output { get; init; }
}

public sealed record AssistantMessage : SessionMessage
{
    [JsonPropertyName("time")]
    [JsonConverter(typeof(NonNullPromptJsonConverter<MessageTime>))]
    public override required MessageTime Time { get; init; }

    [JsonPropertyName("agent")]
    [JsonRequired]
    [JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Agent { get; init; }

    [JsonPropertyName("model")]
    [JsonRequired]
    [JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))]
    public ModelRef? Model { get; init; }

    [JsonPropertyName("content")]
    [JsonConverter(typeof(PromptAttachmentListJsonConverter<AssistantContent>))]
    public required IReadOnlyList<AssistantContent> Content { get; init; }

    [JsonPropertyName("cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(OptionalValueJsonConverter<Money>))]
    public Money? Cost { get; init; }

    [JsonPropertyName("tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<TokenUsageInfo>))]
    public TokenUsageInfo? Tokens { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public SessionStructuredError? Error { get; init; }

    [JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<AssistantSnapshot>))]
    public AssistantSnapshot? Snapshot { get; init; }
    [JsonPropertyName("finish"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalLlmFinishReasonJsonConverter))]
    public LlmFinishReason? Finish { get; init; }
    [JsonPropertyName("rawFinish"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? RawFinish { get; init; }
    [JsonPropertyName("providerState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement>? ProviderState { get; init; }
    [JsonPropertyName("retry"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<AssistantRetry>))]
    public AssistantRetry? Retry { get; init; }

    protected override void Validate()
    {
        base.Validate();
        if (Agent is null || Model?.Id is null || Model.ProviderId is null || Content is null || Time is null)
            throw new JsonException("Assistant message requires agent, model, content, and time.");
    }
}

[JsonConverter(typeof(CompactionMessageJsonConverter))]
public abstract record CompactionMessage : SessionMessage
{
    [JsonPropertyName("status")]
    public abstract string Status { get; init; }
    [JsonPropertyName("reason"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Reason { get; init => field = value is "auto" or "manual" ? value : throw new JsonException("Unknown compaction reason."); }
}

public sealed record CompactionRunningMessage : CompactionMessage
{
    [JsonPropertyName("status"), JsonRequired]
    public override string Status { get; init => field = value == "running" ? value : throw new JsonException("Expected running compaction status."); } = "running";
    [JsonPropertyName("summary"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Summary { get; init => field = PromptValidation.Required(value); }
    [JsonPropertyName("recent"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Recent { get; init => field = PromptValidation.Required(value); }
}

public sealed record CompactionCompletedMessage : CompactionMessage
{
    [JsonPropertyName("status"), JsonRequired]
    public override string Status { get; init => field = value == "completed" ? value : throw new JsonException("Expected completed compaction status."); } = "completed";
    [JsonPropertyName("summary"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Summary { get; init => field = PromptValidation.Required(value); }
    [JsonPropertyName("recent"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Recent { get; init => field = PromptValidation.Required(value); }
}

public sealed record CompactionFailedMessage : CompactionMessage
{
    [JsonPropertyName("status"), JsonRequired]
    public override string Status { get; init => field = value == "failed" ? value : throw new JsonException("Expected failed compaction status."); } = "failed";
    [JsonPropertyName("error"), JsonConverter(typeof(NonNullPromptJsonConverter<SessionStructuredError>))]
    public required SessionStructuredError Error { get; init => field = PromptValidation.Required(value); }
}
