namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class OptionalHttpStatusJsonConverter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = FiniteNumberJsonConverter.ReadValue(ref reader);
        if (value < 100 || value > 599 || value != Math.Truncate(value))
            throw new JsonException("HTTP status must be an integer between 100 and 599.");
        return (int)value;
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null or < 100 or > 599)
            throw new JsonException("HTTP status must be omitted or an integer between 100 and 599.");
        writer.WriteNumberValue(value.Value);
    }
}
