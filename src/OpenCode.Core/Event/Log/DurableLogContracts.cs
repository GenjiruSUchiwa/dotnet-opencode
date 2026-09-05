namespace OpenCode.Core.Event.Log;

using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>Source Bus.SerializedEvent. Type is versioned, such as session.created.1.</summary>
public sealed record SerializedDurableEvent(
    [property: JsonPropertyName("id")] EventId Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("seq")] double Seq,
    [property: JsonPropertyName("aggregateID")] string AggregateId,
    [property: JsonPropertyName("data")] JsonElement Data,
    [property: JsonPropertyName("created"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Created = null);
public sealed record DurableReplayOptions(
    [property: JsonPropertyName("publish")] bool Publish = false,
    [property: JsonPropertyName("ownerID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerId = null,
    [property: JsonPropertyName("strictOwner")] bool StrictOwner = false);
public sealed class InvalidDurableEventException(string type, string message) : InvalidOperationException(message)
{
    public string EventType { get; } = type;
}

[JsonConverter(typeof(DurableLogItemJsonConverter))]
public abstract record DurableLogItem
{
    private protected DurableLogItem() { }
    public sealed record Entry(OpenCodeEvent Event) : DurableLogItem;
    public sealed record Synced(string AggregateId, double? Seq = null) : DurableLogItem;
}

/// <summary>The wire union is the event envelope itself or log.synced, not an invented nested event response.</summary>
public sealed class DurableLogItemJsonConverter : JsonConverter<DurableLogItem>
{
    public override DurableLogItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() == "log.synced")
        {
            var aggregate = root.GetProperty("aggregateID").GetString() ?? throw new JsonException("Missing aggregate ID.");
            var sequence = root.TryGetProperty("seq", out var seq) ? seq.GetDouble() : (double?)null;
            if (sequence is { } value && (!double.IsFinite(value) || value < 0 || value != Math.Truncate(value))) throw new JsonException("Invalid log watermark.");
            return new DurableLogItem.Synced(aggregate, sequence);
        }
        var item = root.Deserialize(OpenCodeJsonContext.Default.OpenCodeEvent) ?? throw new JsonException("Missing durable event.");
        if (item.Durable is null) throw new JsonException("Log entries must be durable.");
        return new DurableLogItem.Entry(item);
    }

    [SuppressMessage("Design", "MA0015", Justification = "Retain the existing serialized-log validation error and inferred aggregate-field parameter name.")]
    public override void Write(Utf8JsonWriter writer, DurableLogItem value, JsonSerializerOptions options)
    {
        if (value is DurableLogItem.Entry entry)
        {
            if (entry.Event.Durable is null) throw new JsonException("Log entries must be durable.");
            JsonSerializer.Serialize(writer, entry.Event, OpenCodeJsonContext.Default.OpenCodeEvent);
            return;
        }
        var synced = (DurableLogItem.Synced)value;
        ArgumentNullException.ThrowIfNull(synced.AggregateId);
        if (synced.Seq is { } watermark && (!double.IsFinite(watermark) || watermark < 0 || watermark != Math.Truncate(watermark)))
            throw new JsonException("Invalid log watermark.");
        writer.WriteStartObject();
        writer.WriteString("type", "log.synced");
        writer.WriteString("aggregateID", synced.AggregateId);
        if (synced.Seq is { } seq) writer.WriteNumber("seq", seq);
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(DurableLogItem))]
[JsonSerializable(typeof(SerializedDurableEvent))]
[JsonSerializable(typeof(DurableReplayOptions))]
public partial class DurableLogJsonContext : JsonSerializerContext;
