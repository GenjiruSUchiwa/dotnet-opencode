namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SkillIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct SkillId
{
    public const string Prefix = "skl_";
    public static SkillId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;

    public override string ToString() => Value;
    public static implicit operator string(SkillId id) => id.Value;
    public static explicit operator SkillId(string value) => FromExisting(value);
}

public sealed class SkillIdJsonConverter() : ScalarJsonConverter<SkillId, string>(SkillId.FromExisting, static value => value.Value)
{
    public override SkillId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected skill ID string.");
        return SkillId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, SkillId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsInitialized() ? value.Value : throw new JsonException("Expected initialized skill ID string."));
}

/// <summary>
/// 1:1 port of Skill.Info from packages/schema/src/skill.ts
/// </summary>
public sealed record SkillInfo(
    [property: JsonPropertyName("id"), JsonRequired] SkillId Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("slash")] bool? Slash = null,
    [property: JsonPropertyName("autoinvoke")] bool? Autoinvoke = null
);
