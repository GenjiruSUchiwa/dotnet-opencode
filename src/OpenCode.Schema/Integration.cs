namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(IntegrationIdJsonConverter))]
public readonly record struct IntegrationId : IEquatable<IntegrationId>
{
    public const string Prefix = "int_";
    public string Value { get; }

    public IntegrationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static IntegrationId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(IntegrationId id) => id.Value;
    public static explicit operator IntegrationId(string value) => new(value);
}

public sealed class IntegrationIdJsonConverter : JsonConverter<IntegrationId>
{
    public override IntegrationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, IntegrationId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IntegrationOAuthMethod), "oauth")]
[JsonDerivedType(typeof(IntegrationCommandMethod), "command")]
[JsonDerivedType(typeof(IntegrationKeyMethod), "key")]
[JsonDerivedType(typeof(IntegrationEnvMethod), "env")]
public abstract record IntegrationMethod;

public sealed record IntegrationOAuthMethod(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label
) : IntegrationMethod;

public sealed record IntegrationCommandMethod(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("command")] IReadOnlyList<string> Command
) : IntegrationMethod;

public sealed record IntegrationKeyMethod(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string? Label = null
) : IntegrationMethod;

public sealed record IntegrationEnvMethod(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("names")] IReadOnlyList<string> Names
) : IntegrationMethod;

/// <summary>
/// 1:1 port of Integration.Info from packages/schema/src/integration.ts
/// </summary>
public sealed record IntegrationInfo(
    [property: JsonPropertyName("id")] IntegrationId Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("methods")] IReadOnlyList<IntegrationMethod> Methods,
    [property: JsonPropertyName("connections")] IReadOnlyList<ConnectionInfo> Connections,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
);
