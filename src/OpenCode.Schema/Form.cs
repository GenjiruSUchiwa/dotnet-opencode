namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

[JsonConverter(typeof(FormIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct FormId
{
    public const string Prefix = "frm_";
    public static FormId FromExisting(string value)
    {
        if (value?.StartsWith(Prefix, StringComparison.Ordinal) != true) throw new JsonException("Form ID must start with frm_.");
        return From(value);
    }

    public static FormId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    public static FormId Create(string? id) => id is null ? Create() : FromExisting(id);
    private static Vogen.Validation Validate(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("Form ID must start with frm_.");
    public override string ToString() => Value;
    public static implicit operator string(FormId id) => id.Value;
    public static explicit operator FormId(string value) => FromExisting(value);
}

public sealed class FormIdJsonConverter() : ScalarJsonConverter<FormId, string>(FormId.FromExisting, static value => value.Value)
{
    public override FormId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected form ID string.");
        return FormId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, FormId value, JsonSerializerOptions options)
    {
        if (!value.IsInitialized() || !value.Value.StartsWith(FormId.Prefix, StringComparison.Ordinal)) throw new JsonException("Form ID must start with frm_.");
        writer.WriteStringValue(value.Value);
    }
}

public sealed record FormOption(
    string Value,
    string Label,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null
)
{
    [JsonPropertyName("value"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    [JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Label { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Label);
}

/// <summary>Form.Value is an untagged JSON primitive or string array, not an arbitrary object.</summary>
[JsonConverter(typeof(FormValueJsonConverter))]
public abstract record FormValue
{
    private protected FormValue() { }

    public sealed record Text(string Value) : FormValue
    {
        public string Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    }

    public sealed record Number(double Value) : FormValue
    {
        public double Value { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Value);
    }

    public sealed record Boolean(bool Value) : FormValue;

    public sealed record Strings : FormValue
    {
        public IReadOnlyList<string> Value { get; }

        public Strings(IReadOnlyList<string> value)
        {
            PromptValidation.Required(PromptValidation.Attachments(value));
            Value = Array.AsReadOnly(value.ToArray());
        }
    }
}

/// <summary>Form.Answer: arbitrary string keys mapped only to Form.Value.</summary>
[JsonConverter(typeof(FormAnswerJsonConverter))]
public sealed class FormAnswer : ReadOnlyDictionary<string, FormValue>
{
    public FormAnswer(IReadOnlyDictionary<string, FormValue> values)
        : base(PromptValidation.Required(values).ToDictionary(pair => pair.Key,
            pair => PromptValidation.Required(pair.Value), StringComparer.Ordinal)) { }
}

public sealed record FormInfo(
    [property: JsonPropertyName("id"), JsonRequired] FormId Id,
    string SessionId,
    string Title,
    IReadOnlyList<FormField> Fields,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    // A string, not SessionId: upstream temporarily permits the MCP "global" owner.
    [JsonPropertyName("sessionID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string SessionId { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(SessionId);
    [JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Title { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Title);
    [JsonPropertyName("fields"), JsonRequired, JsonConverter(typeof(FormFieldsJsonConverter))]
    public IReadOnlyList<FormField> Fields { get; init => field = MessageContract.NonEmpty(value); } = MessageContract.NonEmpty(Fields);

    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    private void Validate()
    {
        if (!Id.IsInitialized() || !Id.Value.StartsWith(FormId.Prefix, StringComparison.Ordinal)) throw new JsonException("Form requires a frm_ ID.");
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(FormPendingState), "pending")]
[JsonDerivedType(typeof(FormAnsweredState), "answered")]
[JsonDerivedType(typeof(FormCancelledState), "cancelled")]
public abstract record FormState;

public sealed record FormPendingState : FormState;
public sealed record FormCancelledState : FormState;
public sealed record FormAnsweredState(FormAnswer Answer) : FormState
{
    [JsonPropertyName("answer"), JsonRequired, JsonConverter(typeof(FormAnswerJsonConverter))]
    public FormAnswer Answer { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Answer);
}

public sealed record FormReply(FormAnswer Answer)
{
    [JsonPropertyName("answer"), JsonRequired, JsonConverter(typeof(FormAnswerJsonConverter))]
    public FormAnswer Answer { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Answer);
}

/// <summary>Protocol Form.CreatePayload; sessionID is a route parameter, not a payload field.</summary>
public sealed record FormCreatePayload(
    string Title,
    IReadOnlyList<FormField> Fields,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<FormId>))] FormId? Id = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
)
{
    [JsonPropertyName("title"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Title { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Title);
    [JsonPropertyName("fields"), JsonRequired, JsonConverter(typeof(FormFieldsJsonConverter))]
    public IReadOnlyList<FormField> Fields { get; init => field = MessageContract.NonEmpty(value); } = MessageContract.NonEmpty(Fields);
}
