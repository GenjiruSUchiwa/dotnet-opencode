namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PermissionIdJsonConverter))]
public readonly record struct PermissionId : IEquatable<PermissionId>
{
    public const string Prefix = "per_";
    public string Value { get; }

    public PermissionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static PermissionId Create(string? id = null) => new(id ?? $"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(PermissionId id) => id.Value;
    public static explicit operator PermissionId(string value) => new(value);
}

public sealed class PermissionIdJsonConverter : JsonConverter<PermissionId>
{
    public override PermissionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, PermissionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(JsonStringEnumConverter<PermissionEffect>))]
public enum PermissionEffect
{
    [JsonStringEnumMemberName("allow")]
    Allow,
    [JsonStringEnumMemberName("deny")]
    Deny,
    [JsonStringEnumMemberName("ask")]
    Ask
}

[JsonConverter(typeof(JsonStringEnumConverter<PermissionReply>))]
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
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("effect")] PermissionEffect Effect
);

public sealed record PermissionSource(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("messageID")] string MessageId,
    [property: JsonPropertyName("id")] string Id
);

public sealed record PermissionRequest(
    [property: JsonPropertyName("id")] PermissionId Id,
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resources")] IReadOnlyList<string> Resources,
    [property: JsonPropertyName("save")] IReadOnlyList<string>? Save = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("source")] PermissionSource? Source = null,
    [property: JsonPropertyName("message")] string? Message = null
);
