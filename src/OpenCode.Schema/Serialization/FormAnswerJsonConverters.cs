namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class FormValueJsonConverter : JsonConverter<FormValue>
{
    public override bool HandleNull => true;

    public override FormValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return new FormValue.Text(reader.GetString()!);
            case JsonTokenType.Number: return new FormValue.Number(FiniteNumberJsonConverter.ReadValue(ref reader));
            case JsonTokenType.True: return new FormValue.Boolean(true);
            case JsonTokenType.False: return new FormValue.Boolean(false);
            case JsonTokenType.StartArray:
                var values = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) return new FormValue.Strings(values);
                    if (reader.TokenType != JsonTokenType.String) throw new JsonException("Form arrays contain only strings.");
                    values.Add(reader.GetString()!);
                }
                throw new JsonException("Unterminated form string array.");
            default: throw new JsonException("Form value must be a string, finite number, boolean, or string array.");
        }
    }

    public override void Write(Utf8JsonWriter writer, FormValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case FormValue.Text text: writer.WriteStringValue(text.Value); break;
            case FormValue.Number number: FiniteNumberJsonConverter.WriteValue(writer, number.Value); break;
            case FormValue.Boolean boolean: writer.WriteBooleanValue(boolean.Value); break;
            case FormValue.Strings strings:
                writer.WriteStartArray();
                foreach (var item in strings.Value) writer.WriteStringValue(item);
                writer.WriteEndArray();
                break;
            default: throw new JsonException("Unknown or null form value.");
        }
    }
}

public sealed class FormAnswerJsonConverter : JsonConverter<FormAnswer>
{
    private static readonly FormValueJsonConverter Value = new();
    public override bool HandleNull => true;

    public override FormAnswer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected form answer object.");
        var values = new Dictionary<string, FormValue>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return new FormAnswer(values);
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var key = reader.GetString()!;
            if (!reader.Read()) throw new JsonException();
            values[key] = Value.Read(ref reader, typeof(FormValue), options);
        }
        throw new JsonException("Unterminated form answer object.");
    }

    public override void Write(Utf8JsonWriter writer, FormAnswer value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Form answer must be omitted rather than null.");
        writer.WriteStartObject();
        foreach (var pair in value)
        {
            writer.WritePropertyName(pair.Key);
            Value.Write(writer, pair.Value, options);
        }
        writer.WriteEndObject();
    }
}
