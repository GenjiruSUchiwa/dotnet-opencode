namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PluginIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct PluginId
{
    public const string Prefix = "plg_";
    public static PluginId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;

    public override string ToString() => Value;
    public static implicit operator string(PluginId id) => id.Value;
    public static explicit operator PluginId(string value) => FromExisting(value);
}

public sealed class PluginIdJsonConverter() : ScalarJsonConverter<PluginId, string>(PluginId.FromExisting, static value => value.Value)
{
    public override PluginId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected plugin ID string.");
        return PluginId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, PluginId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsInitialized() ? value.Value : throw new JsonException("Expected initialized plugin ID string."));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PluginSourceBuiltin), "builtin")]
[JsonDerivedType(typeof(PluginSourcePackage), "package")]
[JsonDerivedType(typeof(PluginSourceLocal), "local")]
[JsonDerivedType(typeof(PluginSourceSdk), "sdk")]
public abstract record PluginSource;

public sealed record PluginSourceBuiltin : PluginSource;

public sealed record PluginSourcePackage(
    [property: JsonPropertyName("package")] string Package
) : PluginSource;

public sealed record PluginSourceLocal(
    [property: JsonPropertyName("path")] string Path
) : PluginSource;

public sealed record PluginSourceSdk : PluginSource;

/// <summary>
/// 1:1 port of Plugin.Info from packages/schema/src/plugin.ts
/// </summary>
public sealed record PluginInfo(
    [property: JsonPropertyName("source")] PluginSource Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tui")] bool Tui,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<PluginId>))] PluginId? Id = null,
    [property: JsonPropertyName("error")] string? Error = null
);
