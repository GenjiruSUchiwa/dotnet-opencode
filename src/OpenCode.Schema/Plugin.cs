namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PluginIdJsonConverter))]
public readonly record struct PluginId(string Value) : IEquatable<PluginId>
{
    public const string Prefix = "plg_";

    public override string ToString() => Value;
    public static implicit operator string(PluginId id) => id.Value;
    public static explicit operator PluginId(string value) => new(value);
}

public sealed class PluginIdJsonConverter : JsonConverter<PluginId>
{
    public override PluginId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, PluginId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
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
    [property: JsonPropertyName("id")] PluginId? Id = null,
    [property: JsonPropertyName("error")] string? Error = null
);
