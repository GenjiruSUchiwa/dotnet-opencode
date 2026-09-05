namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(IntegrationIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct IntegrationId
{
    public const string Prefix = "int_";
    public static IntegrationId FromExisting(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return From(value);
    }

    public static IntegrationId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(IntegrationId id) => id.Value;
    public static explicit operator IntegrationId(string value) => FromExisting(value);
}

public sealed class IntegrationIdJsonConverter() : ScalarJsonConverter<IntegrationId, string>(IntegrationId.FromExisting, static value => value.Value)
{
    public override IntegrationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected integration ID string.");
        return IntegrationId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, IntegrationId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(IntegrationMethodIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct IntegrationMethodId
{
    public static IntegrationMethodId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public override string ToString() => Value;
    public static implicit operator string(IntegrationMethodId id) => id.Value;
    public static explicit operator IntegrationMethodId(string value) => FromExisting(value);
}

public sealed class IntegrationMethodIdJsonConverter() : ScalarJsonConverter<IntegrationMethodId, string>(IntegrationMethodId.FromExisting, static value => value.Value)
{
    public override IntegrationMethodId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected integration method ID string.");
        return IntegrationMethodId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, IntegrationMethodId value, JsonSerializerOptions options) => writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(IntegrationAttemptIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct IntegrationAttemptId
{
    public static IntegrationAttemptId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public static IntegrationAttemptId Create() => FromExisting("con_" + Identifier.Ascending());
    public override string ToString() => Value;
    public static implicit operator string(IntegrationAttemptId id) => id.Value;
    public static explicit operator IntegrationAttemptId(string value) => FromExisting(value);
}

public sealed class IntegrationAttemptIdJsonConverter() : ScalarJsonConverter<IntegrationAttemptId, string>(IntegrationAttemptId.FromExisting, static value => value.Value)
{
    public override IntegrationAttemptId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected integration attempt ID string.");
        return IntegrationAttemptId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, IntegrationAttemptId value, JsonSerializerOptions options) => writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IntegrationOAuthMethod), "oauth")]
[JsonDerivedType(typeof(IntegrationCommandMethod), "command")]
[JsonDerivedType(typeof(IntegrationKeyMethod), "key")]
[JsonDerivedType(typeof(IntegrationEnvMethod), "env")]
public abstract record IntegrationMethod;

public sealed record IntegrationOAuthMethod(
    string Id, string Label, IReadOnlyList<FormField>? Form = null
) : IntegrationMethod
{
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Id { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Id);
    [JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Label { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Label);
    [JsonPropertyName("form"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(FormFieldsJsonConverter))]
    public IReadOnlyList<FormField>? Form { get; init => field = value is null ? null : MessageContract.NonEmpty(value); } = Form is null ? null : MessageContract.NonEmpty(Form);
}

public sealed record IntegrationCommandMethod(
    string Id, string Label, IReadOnlyList<string> Command
) : IntegrationMethod
{
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Id { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Id);
    [JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Label { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Label);
    [JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Command { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Command));
}

public sealed record IntegrationKeyMethod : IntegrationMethod
{
    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Label { get; init; }
    [JsonPropertyName("form"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(FormFieldsJsonConverter))]
    public IReadOnlyList<FormField>? Form { get; init => field = value is null ? null : MessageContract.NonEmpty(value); }
}

public sealed record IntegrationEnvMethod(IReadOnlyList<string> Names) : IntegrationMethod
{
    [JsonPropertyName("names"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Names { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Names));
}

public sealed record IntegrationRef(
    IntegrationId Id, string Name,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null)
{
    [JsonPropertyName("id"), JsonRequired]
    public IntegrationId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
}

/// <summary>
/// Canonical Integration.Info. Discovery and authentication execution remain runtime-owned.
/// </summary>
public sealed record IntegrationInfo(
    IntegrationId Id, string Name, IReadOnlyList<IntegrationMethod> Methods, IReadOnlyList<ConnectionInfo> Connections,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public IntegrationId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("methods"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<IntegrationMethod>))]
    public IReadOnlyList<IntegrationMethod> Methods { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Methods));
    [JsonPropertyName("connections"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<ConnectionInfo>))]
    public IReadOnlyList<ConnectionInfo> Connections { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Connections));
}

public sealed record IntegrationAttemptTime(
    [property: JsonPropertyName("created"), JsonRequired, JsonConverter(typeof(SchemaNumberJsonConverter))] double Created,
    [property: JsonPropertyName("expires"), JsonRequired, JsonConverter(typeof(SchemaNumberJsonConverter))] double Expires
);

public sealed record IntegrationAttempt(IntegrationAttemptId AttemptId, string Url, string Instructions, string Mode, IntegrationAttemptTime Time)
{
    [JsonPropertyName("attemptID"), JsonRequired]
    public IntegrationAttemptId AttemptId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(AttemptId, AttemptId.IsInitialized());
    [JsonPropertyName("url"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Url { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Url);
    [JsonPropertyName("instructions"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Instructions { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Instructions);
    [JsonPropertyName("mode"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Mode { get; init => field = ValidateMode(value); } = ValidateMode(Mode);
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IntegrationAttemptTime>))]
    public IntegrationAttemptTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
    private static string ValidateMode(string value) => value is "auto" or "code" ? value : throw new JsonException("Unknown integration attempt mode.");
}

public sealed record IntegrationCommandAttempt(IntegrationAttemptId AttemptId, IntegrationAttemptTime Time)
{
    [JsonPropertyName("attemptID"), JsonRequired]
    public IntegrationAttemptId AttemptId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(AttemptId, AttemptId.IsInitialized());
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IntegrationAttemptTime>))]
    public IntegrationAttemptTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
}

public abstract record IntegrationTimedStatus(IntegrationAttemptTime Time)
{
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IntegrationAttemptTime>))]
    public IntegrationAttemptTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(IntegrationPendingAttemptStatus), "pending")]
[JsonDerivedType(typeof(IntegrationCompleteAttemptStatus), "complete")]
[JsonDerivedType(typeof(IntegrationFailedAttemptStatus), "failed")]
[JsonDerivedType(typeof(IntegrationExpiredAttemptStatus), "expired")]
public abstract record IntegrationAttemptStatus(IntegrationAttemptTime Time) : IntegrationTimedStatus(Time);
public sealed record IntegrationPendingAttemptStatus(IntegrationAttemptTime Time) : IntegrationAttemptStatus(Time);
public sealed record IntegrationCompleteAttemptStatus(IntegrationAttemptTime Time) : IntegrationAttemptStatus(Time);
public sealed record IntegrationExpiredAttemptStatus(IntegrationAttemptTime Time) : IntegrationAttemptStatus(Time);
public sealed record IntegrationFailedAttemptStatus(IntegrationAttemptTime Time, string Message) : IntegrationAttemptStatus(Time)
{
    [JsonPropertyName("message"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Message { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Message);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(IntegrationPendingCommandStatus), "pending")]
[JsonDerivedType(typeof(IntegrationCompleteCommandStatus), "complete")]
[JsonDerivedType(typeof(IntegrationFailedCommandStatus), "failed")]
[JsonDerivedType(typeof(IntegrationExpiredCommandStatus), "expired")]
public abstract record IntegrationCommandAttemptStatus(IntegrationAttemptTime Time) : IntegrationTimedStatus(Time);
public sealed record IntegrationPendingCommandStatus(IntegrationAttemptTime Time,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Message = null)
    : IntegrationCommandAttemptStatus(Time);
public sealed record IntegrationCompleteCommandStatus(IntegrationAttemptTime Time) : IntegrationCommandAttemptStatus(Time);
public sealed record IntegrationExpiredCommandStatus(IntegrationAttemptTime Time) : IntegrationCommandAttemptStatus(Time);
public sealed record IntegrationFailedCommandStatus(IntegrationAttemptTime Time, string Message) : IntegrationCommandAttemptStatus(Time)
{
    [JsonPropertyName("message"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Message { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Message);
}
