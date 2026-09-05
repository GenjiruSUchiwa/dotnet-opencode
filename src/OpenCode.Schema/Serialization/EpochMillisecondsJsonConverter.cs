namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class EpochMillisecondsJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader);

    internal static DateTimeOffset ReadValue(ref Utf8JsonReader reader) =>
        FromMilliseconds(FiniteNumberJsonConverter.ReadValue(ref reader));

    internal static DateTimeOffset FromMilliseconds(double milliseconds)
    {
        if (!double.IsFinite(milliseconds)) throw new JsonException("Expected finite epoch milliseconds.");
        // JavaScript Date truncates fractional milliseconds. DateTimeOffset has a narrower range.
        var truncated = Math.Truncate(milliseconds);
        if (truncated < -62135596800000d || truncated > 253402300799999d)
            throw new JsonException("Epoch milliseconds are outside the DateTimeOffset range.");
        return DateTimeOffset.FromUnixTimeMilliseconds((long)truncated);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
}

public sealed class OptionalEpochMillisecondsJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EpochMillisecondsJsonConverter.ReadValue(ref reader);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("An absent timestamp must be omitted, not encoded as null.");
        writer.WriteNumberValue(value.Value.ToUnixTimeMilliseconds());
    }
}
