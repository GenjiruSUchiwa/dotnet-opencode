namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SnapshotIdJsonConverter))]
public readonly record struct SnapshotId(string Value) : IEquatable<SnapshotId>
{
    public const string Prefix = "snp_";

    public override string ToString() => Value;
    public static implicit operator string(SnapshotId id) => id.Value;
    public static explicit operator SnapshotId(string value) => new(value);
}

public sealed class SnapshotIdJsonConverter : JsonConverter<SnapshotId>
{
    public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
