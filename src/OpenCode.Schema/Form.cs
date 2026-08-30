namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(FormIdJsonConverter))]
public readonly record struct FormId : IEquatable<FormId>
{
    public const string Prefix = "frm_";
    public string Value { get; }

    public FormId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static FormId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(FormId id) => id.Value;
    public static explicit operator FormId(string value) => new(value);
}

public sealed class FormIdJsonConverter : JsonConverter<FormId>
{
    public override FormId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, FormId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed record FormOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description = null
);
