namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class FiniteNumberJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader);

    internal static double ReadValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value) || !double.IsFinite(value))
            throw new JsonException("Expected a finite JSON number.");
        return value;
    }

    internal static double Validate(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Value must be finite.");
        return value;
    }

    internal static void WriteValue(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value)) throw new JsonException("Expected a finite JSON number.");
        writer.WriteNumberValue(value);
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        WriteValue(writer, value);
}
