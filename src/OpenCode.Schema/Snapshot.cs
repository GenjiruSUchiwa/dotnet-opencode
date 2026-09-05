namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SnapshotIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct SnapshotId
{
    public const string Prefix = "snp_";
    public static SnapshotId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;

    public override string ToString() => Value;
    public static implicit operator string(SnapshotId id) => id.Value;
    public static explicit operator SnapshotId(string value) => FromExisting(value);
}

public sealed class SnapshotIdJsonConverter() : ScalarJsonConverter<SnapshotId, string>(SnapshotId.FromExisting, static value => value.Value)
{
    public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected snapshot ID string.");
        return SnapshotId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsInitialized() ? value.Value : throw new JsonException("Expected initialized snapshot ID string."));
}
