namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

internal static class FormNumberJson
{
    internal static double Read(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetDouble();
        if (reader.TokenType == JsonTokenType.String)
        {
            if (reader.ValueTextEquals("NaN"u8)) return double.NaN;
            if (reader.ValueTextEquals("Infinity"u8)) return double.PositiveInfinity;
            if (reader.ValueTextEquals("-Infinity"u8)) return double.NegativeInfinity;
        }
        throw new JsonException("Expected a Schema.Number JSON value.");
    }
    internal static void Write(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteStringValue(double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
    }
}

public sealed class OptionalFormNumberJsonConverter : JsonConverter<double?>
{
    public override bool HandleNull => true;
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => FormNumberJson.Read(ref reader);
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Optional form number must be omitted, not null.");
        FormNumberJson.Write(writer, value.Value);
    }
}

public sealed class FormConditionValueJsonConverter : JsonConverter<FormConditionValue>
{
    public override bool HandleNull => true;
    public override FormConditionValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => new FormConditionValue.Text(reader.GetString()!),
        JsonTokenType.Number => new FormConditionValue.Number(FormNumberJson.Read(ref reader)),
        JsonTokenType.True => new FormConditionValue.Boolean(true),
        JsonTokenType.False => new FormConditionValue.Boolean(false),
        _ => throw new JsonException("Form condition value must be a string, number, or boolean.")
    };
    public override void Write(Utf8JsonWriter writer, FormConditionValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case FormConditionValue.Text text: writer.WriteStringValue(text.Value); break;
            case FormConditionValue.Number number: FormNumberJson.Write(writer, number.Value); break;
            case FormConditionValue.Boolean boolean: writer.WriteBooleanValue(boolean.Value); break;
            default: throw new JsonException("Unknown or null form condition value.");
        }
    }
}

public sealed class FormFieldsJsonConverter : JsonConverter<IReadOnlyList<FormField>>
{
    private static readonly PromptAttachmentListJsonConverter<FormField> Fields = new();
    public override bool HandleNull => true;
    public override IReadOnlyList<FormField> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageContract.NonEmpty(Fields.Read(ref reader, typeToConvert, options));
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<FormField> value, JsonSerializerOptions options) =>
        Fields.Write(writer, MessageContract.NonEmpty(value), options);
}
