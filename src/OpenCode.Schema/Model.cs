namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(ModelIdJsonConverter))]
public readonly record struct ModelId(string Value) : IEquatable<ModelId>
{
    public override string ToString() => Value;
    public static implicit operator string(ModelId id) => id.Value;
    public static explicit operator ModelId(string value) => new(value);
}

public sealed class ModelIdJsonConverter : JsonConverter<ModelId>
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(VariantIdJsonConverter))]
public readonly record struct VariantId(string Value) : IEquatable<VariantId>
{
    public override string ToString() => Value;
    public static implicit operator string(VariantId id) => id.Value;
    public static explicit operator VariantId(string value) => new(value);
}

public sealed class VariantIdJsonConverter : JsonConverter<VariantId>
{
    public override VariantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, VariantId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// 1:1 port of Model.Ref from packages/schema/src/model.ts
/// </summary>
public sealed record ModelRef(
    [property: JsonPropertyName("providerID")] string ProviderId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("variant")] string? Variant = null
)
{
    public static ModelRef Parse(string input)
    {
        int providerEnd = input.IndexOf('/');
        if (providerEnd <= 0)
        {
            throw new ArgumentException($"Invalid model reference: {input}", nameof(input));
        }

        string providerId = input[..providerEnd];
        int variantStart = input.IndexOf('#', providerEnd + 1);

        string id = variantStart == -1
            ? input[(providerEnd + 1)..]
            : input[(providerEnd + 1)..variantStart];

        string? variant = variantStart == -1
            ? null
            : input[(variantStart + 1)..];

        if (string.IsNullOrEmpty(id) || providerId.Contains('#') || (variant is not null && (string.IsNullOrEmpty(variant) || variant.Contains('#'))))
        {
            throw new ArgumentException($"Invalid model reference: {input}", nameof(input));
        }

        return new ModelRef(providerId, id, variant);
    }

    public override string ToString() =>
        Variant is not null ? $"{ProviderId}/{Id}#{Variant}" : $"{ProviderId}/{Id}";
}

public sealed record ModelCapabilities(
    bool Tools,
    IReadOnlyList<string> Input,
    IReadOnlyList<string> Output,
    bool? ResponsesWebsockets = null
);

public sealed record ModelCost(
    double Input,
    double Output,
    double? CacheRead = null,
    double? CacheWrite = null
);

public sealed record ModelLimit(
    long Context,
    long Output
);

public sealed record ModelVariant(
    string Id,
    IReadOnlyDictionary<string, object>? Settings = null,
    IReadOnlyDictionary<string, string>? Headers = null
);

public sealed record ModelInfo(
    string Id,
    string ModelId,
    string ProviderId,
    string Name,
    string? Family = null,
    string? Package = null,
    ModelCapabilities? Capabilities = null,
    IReadOnlyList<ModelVariant>? Variants = null,
    ModelLimit? Limit = null,
    IReadOnlyList<ModelCost>? Cost = null,
    IReadOnlyDictionary<string, object>? Settings = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool Enabled = true
);
