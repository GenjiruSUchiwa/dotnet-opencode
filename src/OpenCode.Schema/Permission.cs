namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PermissionIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct PermissionId
{
    public const string Prefix = "per_";
    public static PermissionId FromExisting(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return From(value);
    }

    public static PermissionId Create(string? id = null) => FromExisting(id ?? $"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => !string.IsNullOrWhiteSpace(value)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("PermissionId cannot be empty or whitespace.");
    public override string ToString() => Value;
    public static implicit operator string(PermissionId id) => id.Value;
    public static explicit operator PermissionId(string value) => FromExisting(value);
}

public sealed class PermissionIdJsonConverter() : ScalarJsonConverter<PermissionId, string>(PermissionId.FromExisting, static value => value.Value)
{
    public override PermissionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PermissionId.FromExisting(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, PermissionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(PermissionEffectJsonConverter))]
public enum PermissionEffect
{
    [JsonStringEnumMemberName("allow")]
    Allow,
    [JsonStringEnumMemberName("deny")]
    Deny,
    [JsonStringEnumMemberName("ask")]
    Ask
}

[JsonConverter(typeof(SourceStringEnumJsonConverter<PermissionReply>))]
public enum PermissionReply
{
    [JsonStringEnumMemberName("once")]
    Once,
    [JsonStringEnumMemberName("always")]
    Always,
    [JsonStringEnumMemberName("reject")]
    Reject
}

public sealed record PermissionRule(
    string Action,
    string Resource,
    PermissionEffect Effect
)
{
    [JsonPropertyName("action"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Action { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Action);
    [JsonPropertyName("resource"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Resource { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Resource);
    [JsonPropertyName("effect"), JsonRequired]
    public PermissionEffect Effect { get; init => field = PermissionEffectJsonConverter.Validate(value); } = PermissionEffectJsonConverter.Validate(Effect);
}

public sealed record PermissionSource(
    [property: JsonPropertyName("type"), JsonRequired] string Type,
    [property: JsonPropertyName("messageID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string MessageId,
    [property: JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Id
) : IJsonOnSerializing, IJsonOnDeserialized
{
    private void Validate()
    {
        SourceObjectContract.Required(MessageId, Id);
        if (Type != "tool") throw new JsonException("Permission source type must be tool.");
    }
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
}

public sealed record PermissionRequest(
    [property: JsonPropertyName("id"), JsonRequired] PermissionId Id,
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("action"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Action,
    [property: JsonPropertyName("resources"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string> Resources,
    [property: JsonPropertyName("save"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Save = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PermissionSource>))] PermissionSource? Source = null,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Message = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Action, Resources);
}
