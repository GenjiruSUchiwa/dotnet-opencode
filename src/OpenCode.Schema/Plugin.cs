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
    [property: JsonPropertyName("package"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Package
) : PluginSource, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Package);
}

public sealed record PluginSourceLocal(
    [property: JsonPropertyName("path"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Path
) : PluginSource, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Path);
}

public sealed record PluginSourceSdk : PluginSource;

/// <summary>
/// 1:1 port of Plugin.Info from packages/schema/src/plugin.ts
/// </summary>
[JsonConverter(typeof(PluginInfoJsonConverter))]
public sealed record PluginInfo(
    [property: JsonPropertyName("source"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<PluginSource>))] PluginSource Source,
    [property: JsonPropertyName("status"), JsonRequired] string Status,
    [property: JsonPropertyName("tui"), JsonRequired] bool Tui,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<PluginId>))] PluginId? Id = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    private void Validate()
    {
        SourceObjectContract.Required(Source);
        if (Status is not ("active" or "failed")) throw new JsonException("Plugin status must be active or failed.");
        if (Status == "active" && Id is null) throw new JsonException("Active plugin requires id.");
        if (Status == "failed" && Error is null) throw new JsonException("Failed plugin requires error.");
    }
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
}
