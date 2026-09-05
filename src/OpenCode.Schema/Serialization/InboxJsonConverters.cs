namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class InboxDeliveryModeJsonConverter : JsonConverter<InboxDeliveryMode>
{
    public override InboxDeliveryMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected inbox delivery string.");
        if (reader.ValueTextEquals("steer"u8)) return InboxDeliveryMode.Steer;
        if (reader.ValueTextEquals("queue"u8)) return InboxDeliveryMode.Queue;
        throw new JsonException("Unknown inbox delivery mode.");
    }

    public override void Write(Utf8JsonWriter writer, InboxDeliveryMode value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case InboxDeliveryMode.Steer: writer.WriteStringValue("steer"u8); return;
            case InboxDeliveryMode.Queue: writer.WriteStringValue("queue"u8); return;
            default: throw new JsonException("Unknown inbox delivery mode.");
        }
    }
}

public sealed class InboxItemJsonConverter : JsonConverter<InboxItem>
{
    public override bool HandleNull => true;

    public override InboxItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // The outer type may follow payload. Buffer one item, then decode the concrete payload.
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new InboxItem(InboxJson.Required(root, "delivery").Deserialize(OpenCodeJsonContext.Default.InboxDeliveryMode),
            InboxJson.ReadPayload(root));
    }

    public override void Write(Utf8JsonWriter writer, InboxItem value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Inbox item is required.");
        writer.WriteStartObject();
        InboxJson.WriteItem(writer, value.Delivery, value.Payload);
        writer.WriteEndObject();
    }
}

public sealed class SessionInboxItemJsonConverter : JsonConverter<SessionInboxItem>
{
    public override bool HandleNull => true;

    public override SessionInboxItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var time = InboxJson.Required(root, "timeCreated");
        if (time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out var milliseconds))
            throw new JsonException("Inbox timeCreated must be numeric epoch milliseconds.");
        return new SessionInboxItem(
            InboxJson.Required(root, "id").Deserialize(OpenCodeJsonContext.Default.MessageId),
            InboxJson.Required(root, "sessionID").Deserialize(OpenCodeJsonContext.Default.SessionId),
            InboxJson.Required(root, "delivery").Deserialize(OpenCodeJsonContext.Default.InboxDeliveryMode),
            InboxJson.ReadPayload(root), EpochMillisecondsJsonConverter.FromMilliseconds(milliseconds));
    }

    public override void Write(Utf8JsonWriter writer, SessionInboxItem value, JsonSerializerOptions options)
    {
        if (value is null || !value.Id.IsInitialized() || !value.SessionId.IsInitialized())
            throw new JsonException("Enqueued inbox item requires id and sessionID.");
        writer.WriteStartObject();
        writer.WriteString("id"u8, value.Id.Value);
        writer.WriteString("sessionID"u8, value.SessionId.Value);
        writer.WriteNumber("timeCreated"u8, value.TimeCreated.ToUnixTimeMilliseconds());
        InboxJson.WriteItem(writer, value.Delivery, value.Payload);
        writer.WriteEndObject();
    }
}

internal static class InboxJson
{
    internal static string Type(InboxPayload payload) => payload switch
    {
        UserInboxPayload => "user",
        SyntheticInboxPayload => "synthetic",
        CompactionInboxPayload => "compaction",
        MoveInboxPayload => "move",
        _ => throw new JsonException("Unknown or missing inbox payload.")
    };

    internal static JsonElement Required(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
            throw new JsonException($"Inbox item requires {name}.");
        return value;
    }

    internal static InboxPayload ReadPayload(JsonElement root)
    {
        var type = Required(root, "type");
        var payload = Required(root, "payload");
        if (type.ValueKind != JsonValueKind.String || payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("Inbox item requires a type string and payload object.");
        return type.GetString() switch
        {
            "user" => payload.Deserialize(OpenCodeJsonContext.Default.UserInboxPayload)!,
            "synthetic" => payload.Deserialize(OpenCodeJsonContext.Default.SyntheticInboxPayload)!,
            "compaction" => payload.Deserialize(OpenCodeJsonContext.Default.CompactionInboxPayload)!,
            "move" => payload.Deserialize(OpenCodeJsonContext.Default.MoveInboxPayload)!,
            _ => throw new JsonException("Unknown inbox item type.")
        };
    }

    internal static void WriteItem(Utf8JsonWriter writer, InboxDeliveryMode delivery, InboxPayload payload)
    {
        writer.WriteString("type"u8, Type(payload));
        writer.WritePropertyName("delivery"u8);
        JsonSerializer.Serialize(writer, delivery, OpenCodeJsonContext.Default.InboxDeliveryMode);
        writer.WritePropertyName("payload"u8);
        switch (payload)
        {
            case UserInboxPayload user: JsonSerializer.Serialize(writer, user, OpenCodeJsonContext.Default.UserInboxPayload); break;
            case SyntheticInboxPayload synthetic: JsonSerializer.Serialize(writer, synthetic, OpenCodeJsonContext.Default.SyntheticInboxPayload); break;
            case CompactionInboxPayload compaction: JsonSerializer.Serialize(writer, compaction, OpenCodeJsonContext.Default.CompactionInboxPayload); break;
            case MoveInboxPayload move: JsonSerializer.Serialize(writer, move, OpenCodeJsonContext.Default.MoveInboxPayload); break;
        }
    }
}
