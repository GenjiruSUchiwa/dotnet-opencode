namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PermissionSavedIdJsonConverter))]
public readonly record struct PermissionSavedId : IEquatable<PermissionSavedId>
{
    public const string Prefix = "psv_";
    public string Value { get; }

    public PermissionSavedId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static PermissionSavedId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(PermissionSavedId id) => id.Value;
    public static explicit operator PermissionSavedId(string value) => new(value);
}

public sealed class PermissionSavedIdJsonConverter : JsonConverter<PermissionSavedId>
{
    public override PermissionSavedId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, PermissionSavedId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// 1:1 port of PermissionSaved.Info from packages/schema/src/permission-saved.ts
/// </summary>
public sealed record PermissionSavedInfo(
    [property: JsonPropertyName("id")] PermissionSavedId Id,
    [property: JsonPropertyName("projectID")] ProjectId ProjectId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resource")] string Resource
);
