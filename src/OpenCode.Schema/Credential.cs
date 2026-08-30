namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(CredentialIdJsonConverter))]
public readonly record struct CredentialId : IEquatable<CredentialId>
{
    public const string Prefix = "cred_";
    public string Value { get; }

    public CredentialId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static CredentialId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(CredentialId id) => id.Value;
    public static explicit operator CredentialId(string value) => new(value);
}

public sealed class CredentialIdJsonConverter : JsonConverter<CredentialId>
{
    public override CredentialId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, CredentialId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CredentialOAuth), "oauth")]
[JsonDerivedType(typeof(CredentialKey), "key")]
public abstract record CredentialValue;

public sealed record CredentialOAuth(
    [property: JsonPropertyName("methodID")] string MethodId,
    [property: JsonPropertyName("refresh")] string Refresh,
    [property: JsonPropertyName("access")] string Access,
    [property: JsonPropertyName("expires")] long Expires,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : CredentialValue;

public sealed record CredentialKey(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : CredentialValue;

/// <summary>
/// 1:1 port of Credential.Info from packages/schema/src/credential.ts
/// </summary>
public sealed record CredentialInfo(
    [property: JsonPropertyName("id")] CredentialId Id,
    [property: JsonPropertyName("integrationID")] string IntegrationId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] CredentialValue Value
);
