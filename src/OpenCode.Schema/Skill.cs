namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SkillIdJsonConverter))]
public readonly record struct SkillId(string Value) : IEquatable<SkillId>
{
    public const string Prefix = "skl_";

    public override string ToString() => Value;
    public static implicit operator string(SkillId id) => id.Value;
    public static explicit operator SkillId(string value) => new(value);
}

public sealed class SkillIdJsonConverter : JsonConverter<SkillId>
{
    public override SkillId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SkillId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// 1:1 port of Skill.Info from packages/schema/src/skill.ts
/// </summary>
public sealed record SkillInfo(
    [property: JsonPropertyName("id")] SkillId Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("slash")] bool? Slash = null,
    [property: JsonPropertyName("autoinvoke")] bool? Autoinvoke = null
);
