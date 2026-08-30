namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(EventIdJsonConverter))]
public readonly record struct EventId : IEquatable<EventId>
{
    public const string Prefix = "evt_";
    public string Value { get; }

    public EventId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static EventId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(EventId id) => id.Value;
    public static explicit operator EventId(string value) => new(value);
}

public sealed class EventIdJsonConverter : JsonConverter<EventId>
{
    public override EventId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, EventId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

public sealed record DurableEnvelope(
    [property: JsonPropertyName("aggregateID")] string AggregateId,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("version")] int Version
);

/// <summary>
/// 1:1 port of Event.Payload from packages/schema/src/event.ts
/// </summary>
public sealed record OpenCodeEvent(
    [property: JsonPropertyName("id")] EventId Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("location")] string? Location = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("durable")] DurableEnvelope? Durable = null
);
