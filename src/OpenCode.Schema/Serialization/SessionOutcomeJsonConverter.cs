namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class SessionOutcomeJsonConverter : JsonConverter<SessionOutcome>
{
    public override SessionOutcome Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected a session outcome string.");
        if (reader.ValueTextEquals("succeeded"u8)) return SessionOutcome.Succeeded;
        if (reader.ValueTextEquals("failed"u8)) return SessionOutcome.Failed;
        if (reader.ValueTextEquals("interrupted"u8)) return SessionOutcome.Interrupted;
        throw new JsonException("Unknown session outcome.");
    }

    public override void Write(Utf8JsonWriter writer, SessionOutcome value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case SessionOutcome.Succeeded: writer.WriteStringValue("succeeded"u8); return;
            case SessionOutcome.Failed: writer.WriteStringValue("failed"u8); return;
            case SessionOutcome.Interrupted: writer.WriteStringValue("interrupted"u8); return;
            default: throw new JsonException("Unknown session outcome.");
        }
    }
}
