namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(CredentialIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct CredentialId
{
    public const string Prefix = "cred_";
    public static CredentialId FromExisting(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return From(value);
    }

    public static CredentialId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(CredentialId id) => id.Value;
    public static explicit operator CredentialId(string value) => FromExisting(value);
}

public sealed class CredentialIdJsonConverter() : ScalarJsonConverter<CredentialId, string>(CredentialId.FromExisting, static value => value.Value)
{
    public override CredentialId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected credential ID string.");
        return CredentialId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, CredentialId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CredentialOAuth), "oauth")]
[JsonDerivedType(typeof(CredentialKey), "key")]
public abstract record CredentialValue;

public sealed record CredentialOAuth(
    string MethodId,
    string Refresh,
    string Access,
    double Expires,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : CredentialValue
{
    [JsonPropertyName("methodID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string MethodId { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(MethodId);
    [JsonPropertyName("refresh"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Refresh { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Refresh);
    [JsonPropertyName("access"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Access { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Access);
    [JsonPropertyName("expires"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Expires { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Expires, 0);
}

public sealed record CredentialKey(
    string Key,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("configuration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(FormAnswerJsonConverter))] FormAnswer? Configuration = null
) : CredentialValue
{
    [JsonPropertyName("key"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Key { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Key);
}

/// <summary>
/// 1:1 port of Credential.Info from packages/schema/src/credential.ts
/// </summary>
public sealed record CredentialInfo(
    [property: JsonPropertyName("id")] CredentialId Id,
    [property: JsonPropertyName("integrationID")] string IntegrationId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] CredentialValue Value
);
