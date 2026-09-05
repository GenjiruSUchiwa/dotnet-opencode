namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Toast duration has a decoding-only default; callers still provide a duration.</summary>
public sealed class TuiToastEventJsonConverter : JsonConverter<TuiToastShowEventData>
{
    private static readonly SourceStringEnumJsonConverter<TuiToastVariant> Variant = new();
    public override bool HandleNull => true;

    public override TuiToastShowEventData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected toast object.");
        string? message = null, title = null;
        TuiToastVariant? variant = null;
        double duration = 5000;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new(message ?? throw new JsonException("Toast requires message."),
                    variant ?? throw new JsonException("Toast requires variant."), duration, title);
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.GetString();
            if (!reader.Read()) throw new JsonException();
            switch (field)
            {
                case "message":
                case "title":
                    if (reader.TokenType != JsonTokenType.String) throw new JsonException("Toast text must be a string.");
                    if (field == "message") message = reader.GetString();
                    else title = reader.GetString();
                    break;
                case "variant": variant = Variant.Read(ref reader, typeof(TuiToastVariant), options); break;
                case "duration": duration = MessageContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader), 1); break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Unterminated toast object.");
    }

    public override void Write(Utf8JsonWriter writer, TuiToastShowEventData value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Expected toast object.");
        SourceObjectContract.Required(value.Message);
        writer.WriteStartObject();
        if (value.Title is not null) writer.WriteString("title", value.Title);
        writer.WriteString("message", value.Message);
        writer.WritePropertyName("variant");
        Variant.Write(writer, value.Variant, options);
        writer.WriteNumber("duration", MessageContract.Integer(value.Duration, 1));
        writer.WriteEndObject();
    }
}
