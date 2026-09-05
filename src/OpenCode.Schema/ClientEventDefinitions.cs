namespace OpenCode.Schema;

using System.Text.Json.Serialization;

// Shared transitional contracts explicitly included in upstream ServerDefinitions
// for current TUI consumers. They are not durable Session aggregate events.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStatusIdle), "idle")]
[JsonDerivedType(typeof(SessionStatusRetry), "retry")]
[JsonDerivedType(typeof(SessionStatusBusy), "busy")]
public abstract record SessionStatusInfo;

public sealed record SessionStatusIdle : SessionStatusInfo;
public sealed record SessionStatusBusy : SessionStatusInfo;
public sealed record SessionStatusRetry(
    [property: JsonPropertyName("attempt"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))] double Attempt,
    [property: JsonPropertyName("message"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Message,
    [property: JsonPropertyName("next"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))] double Next,
    [property: JsonPropertyName("action"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<SessionRetryAction>))] SessionRetryAction? Action = null
) : SessionStatusInfo, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Message);
}

public sealed record SessionRetryAction(
    [property: JsonPropertyName("reason"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Reason,
    [property: JsonPropertyName("provider"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Provider,
    [property: JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Title,
    [property: JsonPropertyName("message"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Message,
    [property: JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Label,
    [property: JsonPropertyName("link"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Link = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Reason, Provider, Title, Message, Label);
}

public sealed record SessionStatusEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("status"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStatusInfo>))] SessionStatusInfo Status
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Status);
}

// The pre-existing SessionIdleEventData includes an execution outcome for native
// callers. The public transitional notification contains only sessionID.
public sealed record SessionStatusIdleEventData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId);
public sealed record TuiSessionSelectEventData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId);
public sealed record TuiPromptAppendEventData(
    [property: JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Text
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Text);
}
public sealed record TuiCommandExecuteEventData(
    // The source union permits arbitrary command IDs, not only its suggested literals.
    [property: JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Command
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Command);
}

[JsonConverter(typeof(SourceStringEnumJsonConverter<TuiToastVariant>))]
public enum TuiToastVariant
{
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("success")] Success,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error
}

[JsonConverter(typeof(TuiToastEventJsonConverter))]
public sealed record TuiToastShowEventData(string Message, TuiToastVariant Variant, double Duration, string? Title = null);

public static class SessionStatusEventDefinitions
{
    public static readonly EphemeralEventDefinition<SessionStatusEventData> Status = new("session.status", OpenCodeJsonContext.Default.SessionStatusEventData, "SessionStatusUpdated");
    public static readonly EphemeralEventDefinition<SessionStatusIdleEventData> Idle = new("session.idle", OpenCodeJsonContext.Default.SessionStatusIdleEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Status, Idle);
}

public static class TuiEventDefinitions
{
    public static readonly EphemeralEventDefinition<TuiPromptAppendEventData> PromptAppend = new("tui.prompt.append", OpenCodeJsonContext.Default.TuiPromptAppendEventData);
    public static readonly EphemeralEventDefinition<TuiCommandExecuteEventData> CommandExecute = new("tui.command.execute", OpenCodeJsonContext.Default.TuiCommandExecuteEventData);
    public static readonly EphemeralEventDefinition<TuiToastShowEventData> ToastShow = new("tui.toast.show", OpenCodeJsonContext.Default.TuiToastShowEventData);
    public static readonly EphemeralEventDefinition<TuiSessionSelectEventData> SessionSelect = new("tui.session.select", OpenCodeJsonContext.Default.TuiSessionSelectEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(PromptAppend, CommandExecute, ToastShow, SessionSelect);
}
