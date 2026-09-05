namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

public sealed class OpenCodeEventJsonConverter : JsonConverter<OpenCodeEvent>
{
    private static readonly EventLocationJsonConverter Location = new();
    public override bool HandleNull => true;

    public override OpenCodeEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader, true);

    internal static OpenCodeEvent ReadValue(ref Utf8JsonReader reader, bool includeDurable)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected event object.");
        EventId? id = null;
        string? type = null;
        double created = 0;
        JsonElement data = default;
        LocationRef? location = null;
        IReadOnlyDictionary<string, JsonElement>? metadata = null;
        DurableEnvelope? durable = null;
        var fields = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (fields != 15) throw new JsonException("Event requires id, type, created, and data.");
                var result = new OpenCodeEvent(id ?? throw new JsonException("Event requires id, type, created, and data."), type!, created, data, location, metadata, durable);
                result.Validate();
                return result;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.ValueTextEquals("id"u8) ? 1 : reader.ValueTextEquals("type"u8) ? 2
                : reader.ValueTextEquals("created"u8) ? 4 : reader.ValueTextEquals("data"u8) ? 8
                : reader.ValueTextEquals("location"u8) ? 16 : reader.ValueTextEquals("metadata"u8) ? 32
                : includeDurable && reader.ValueTextEquals("durable"u8) ? 64 : 0;
            if (!reader.Read()) throw new JsonException();
            switch (field)
            {
                case 1: id = JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.EventId); break;
                case 2:
                    if (reader.TokenType != JsonTokenType.String) throw new JsonException("Event type must be a string.");
                    type = reader.GetString();
                    break;
                case 4: created = FiniteNumberJsonConverter.ReadValue(ref reader); break;
                case 8: data = JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.JsonElement); break;
                case 16: location = Location.Read(ref reader, typeof(LocationRef), OpenCodeJsonContext.Default.Options); break;
                case 32:
                    if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Event metadata must be an object.");
                    metadata = JsonSerializer.Deserialize(ref reader,
                        (JsonTypeInfo<IReadOnlyDictionary<string, JsonElement>>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(IReadOnlyDictionary<string, JsonElement>))!);
                    break;
                case 64:
                    durable = JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.DurableEnvelope)
                        ?? throw new JsonException("Durable envelope must be omitted, not null.");
                    break;
                default: reader.Skip(); break;
            }
            fields |= field & 15;
        }
        throw new JsonException("Unterminated event object.");
    }

    public override void Write(Utf8JsonWriter writer, OpenCodeEvent value, JsonSerializerOptions options)
    {
        PromptValidation.Required(value).Validate();
        writer.WriteStartObject();
        writer.WriteString("id"u8, value.Id.Value);
        writer.WriteString("type"u8, value.Type);
        writer.WriteNumber("created"u8, value.Created);
        if (value.Durable is { } durable)
        {
            writer.WritePropertyName("durable"u8);
            JsonSerializer.Serialize(writer, durable, OpenCodeJsonContext.Default.DurableEnvelope);
        }
        if (value.Location is { } location)
        {
            writer.WritePropertyName("location"u8);
            Location.Write(writer, location, options);
        }
        if (value.Metadata is { } metadata)
        {
            writer.WritePropertyName("metadata"u8);
            JsonSerializer.Serialize(writer, metadata,
                (JsonTypeInfo<IReadOnlyDictionary<string, JsonElement>>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(IReadOnlyDictionary<string, JsonElement>))!);
        }
        writer.WritePropertyName("data"u8);
        value.Data.WriteTo(writer);
        writer.WriteEndObject();
    }
}
