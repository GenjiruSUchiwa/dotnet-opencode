namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Form.When's scalar union excludes the string-array branch of Form.Value.</summary>
[JsonConverter(typeof(FormConditionValueJsonConverter))]
public abstract record FormConditionValue
{
    private protected FormConditionValue() { }
    public sealed record Text(string Value) : FormConditionValue
    {
        public string Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    }
    public sealed record Number(double Value) : FormConditionValue;
    public sealed record Boolean(bool Value) : FormConditionValue;
}

/// <summary>Describes one eq/neq condition. Reference ordering and visibility evaluation are runtime concerns.</summary>
public sealed record FormWhen(string Key, string Op, FormConditionValue Value)
{
    [JsonPropertyName("key"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Key { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Key);
    [JsonPropertyName("op"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Op { get; init => field = ValidateOp(value); } = ValidateOp(Op);
    [JsonPropertyName("value"), JsonRequired, JsonConverter(typeof(FormConditionValueJsonConverter))]
    public FormConditionValue Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    private static string ValidateOp(string value) => value is "eq" or "neq" ? value : throw new JsonException("Form condition op must be eq or neq.");
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FormStringField), "string")]
[JsonDerivedType(typeof(FormNumberField), "number")]
[JsonDerivedType(typeof(FormIntegerField), "integer")]
[JsonDerivedType(typeof(FormBooleanField), "boolean")]
[JsonDerivedType(typeof(FormMultiselectField), "multiselect")]
[JsonDerivedType(typeof(FormExternalField), "external")]
public abstract record FormField
{
    [JsonPropertyName("key"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Key { get; init => field = PromptValidation.Required(value); }
    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Title { get; init; }
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Description { get; init; }
}

public abstract record FormInputField : FormField
{
    [JsonPropertyName("required"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))]
    public bool? Required { get; init; }
    [JsonPropertyName("when"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<FormWhen>))]
    public IReadOnlyList<FormWhen>? When { get; init => field = PromptValidation.Attachments(value); }
}

public sealed record FormStringField : FormInputField
{
    [JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Format { get; init => field = value is null or "email" or "uri" or "date" or "date-time" ? value : throw new JsonException("Unknown form string format."); }
    [JsonPropertyName("minLength"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? MinLength { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
    [JsonPropertyName("maxLength"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? MaxLength { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
    [JsonPropertyName("pattern"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Pattern { get; init; }
    [JsonPropertyName("placeholder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Placeholder { get; init; }
    [JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Default { get; init; }
    [JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<FormOption>))]
    public IReadOnlyList<FormOption>? Options { get; init => field = PromptValidation.Attachments(value); }
    [JsonPropertyName("custom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))]
    public bool? Custom { get; init; }
}

public abstract record FormNumericField : FormInputField
{
    // Integer-field descriptor bounds/defaults are Schema.Number too, not Schema.Int.
    [JsonPropertyName("minimum"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFormNumberJsonConverter))]
    public double? Minimum { get; init; }
    [JsonPropertyName("maximum"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFormNumberJsonConverter))]
    public double? Maximum { get; init; }
    [JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFormNumberJsonConverter))]
    public double? Default { get; init; }
}

public sealed record FormNumberField : FormNumericField;
public sealed record FormIntegerField : FormNumericField;

public sealed record FormBooleanField : FormInputField
{
    [JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))]
    public bool? Default { get; init; }
}

public sealed record FormMultiselectField : FormInputField
{
    [JsonPropertyName("options"), JsonConverter(typeof(PromptAttachmentListJsonConverter<FormOption>))]
    public required IReadOnlyList<FormOption> Options { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); }
    [JsonPropertyName("minItems"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? MinItems { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
    [JsonPropertyName("maxItems"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? MaxItems { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
    [JsonPropertyName("custom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))]
    public bool? Custom { get; init; }
    [JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string>? Default { get; init => field = PromptValidation.Attachments(value); }
}

public sealed record FormExternalField : FormField
{
    [JsonPropertyName("url"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Url { get; init => field = PromptValidation.Required(value); }
}
