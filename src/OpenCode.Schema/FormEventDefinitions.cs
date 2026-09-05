namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record FormCreatedEventData(FormInfo Form)
{
    [JsonPropertyName("form"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<FormInfo>))]
    public FormInfo Form { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Form);
}

public abstract record FormReferenceEventData([property: JsonPropertyName("id"), JsonRequired] FormId Id, string SessionId)
    : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("sessionID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string SessionId { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(SessionId);
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        if (!Id.IsInitialized() || !Id.Value.StartsWith(FormId.Prefix, StringComparison.Ordinal)) throw new JsonException("Form event requires a frm_ ID.");
    }
}

public sealed record FormRepliedEventData(FormId Id, string SessionId, FormAnswer Answer) : FormReferenceEventData(Id, SessionId)
{
    [JsonPropertyName("answer"), JsonRequired, JsonConverter(typeof(FormAnswerJsonConverter))]
    public FormAnswer Answer { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Answer);
}

public sealed record FormCancelledEventData(FormId Id, string SessionId) : FormReferenceEventData(Id, SessionId);

public static class FormEventDefinitions
{
    public static readonly EphemeralEventDefinition<FormCreatedEventData> Created = new("form.created", OpenCodeJsonContext.Default.FormCreatedEventData);
    public static readonly EphemeralEventDefinition<FormRepliedEventData> Replied = new("form.replied", OpenCodeJsonContext.Default.FormRepliedEventData);
    public static readonly EphemeralEventDefinition<FormCancelledEventData> Cancelled = new("form.cancelled", OpenCodeJsonContext.Default.FormCancelledEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Created, Replied, Cancelled);
}
