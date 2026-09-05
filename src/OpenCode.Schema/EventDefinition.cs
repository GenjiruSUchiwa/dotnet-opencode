namespace OpenCode.Schema;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

public enum EventDurability { Durable, Ephemeral }

public sealed class DurableEventMetadata(double version, string aggregate)
{
    public double Version { get; } = MessageContract.Integer(version, 1);
    public string Aggregate { get; } = PromptValidation.Required(aggregate);
}

/// <summary>Pure contract metadata and codecs; no observers, persistence, or projectors.</summary>
public abstract class EventDefinition
{
    public string Type { get; }
    public string Identifier { get; }
    public abstract EventDurability Durability { get; }
    public abstract DurableEventMetadata? Durable { get; }

    private protected EventDefinition(string type, string? identifier)
    {
        Type = PromptValidation.Required(type);
        Identifier = identifier ?? type;
    }

    protected void ValidateEnvelope(OpenCodeEvent value)
    {
        PromptValidation.Required(value).Validate();
        if (value.Type != Type) throw new JsonException($"Expected event type {Type}.");
        if (Durable is { } definition && (value.Durable is null || value.Durable.Version != definition.Version))
            throw new JsonException($"Expected durable version {definition.Version} for {Type}.");
        // Aggregate-field selection is publication metadata, not an equality check in event.ts.
    }

    public abstract void Validate(OpenCodeEvent value);
    public abstract OpenCodeEvent Normalize(OpenCodeEvent value);
}

public abstract class EventDefinition<TData> : EventDefinition where TData : class
{
    public JsonTypeInfo<TData> DataType { get; }

    private protected EventDefinition(string type, JsonTypeInfo<TData> dataType, string? identifier)
        : base(type, identifier) => DataType = PromptValidation.Required(dataType);

    public TData DecodeData(JsonElement encoded)
    {
        if (encoded.ValueKind != JsonValueKind.Object) throw new JsonException("Event data must be an object.");
        return encoded.Deserialize(DataType) ?? throw new JsonException("Event data cannot be null.");
    }

    public JsonElement EncodeData(TData data)
    {
        var encoded = JsonSerializer.SerializeToElement(PromptValidation.Required(data), DataType);
        // Required JSON properties also need enforcement when a caller constructs an invalid DTO.
        _ = DecodeData(encoded);
        return encoded;
    }

    public TData Decode(OpenCodeEvent value)
    {
        ValidateEnvelope(value);
        return DecodeData(value.Data);
    }

    public override void Validate(OpenCodeEvent value) => _ = Decode(value);

    public override OpenCodeEvent Normalize(OpenCodeEvent value)
    {
        var data = Decode(value);
        // Ephemeral definitions have no durable field. Struct decoding ignores excess fields.
        return value with { Data = JsonSerializer.SerializeToElement(data, DataType), Durable = Durable is null ? null : value.Durable };
    }

    public OpenCodeEvent Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read()) throw new JsonException("Event cannot be empty.");
        // For ephemeral definitions, durable is an excess field, even when its value is malformed.
        var value = OpenCodeEventJsonConverter.ReadValue(ref reader, Durable is not null);
        if (reader.Read()) throw new JsonException("Expected a single event.");
        return Normalize(value);
    }
}

public sealed class DurableEventDefinition<TData> : EventDefinition<TData> where TData : class
{
    public override EventDurability Durability => EventDurability.Durable;
    public override DurableEventMetadata Durable { get; }

    public DurableEventDefinition(string type, double version, string aggregate, JsonTypeInfo<TData> dataType, string? identifier = null)
        : base(type, dataType, identifier) => Durable = new DurableEventMetadata(version, aggregate);

    public string AggregateId(JsonElement encodedData)
    {
        if (encodedData.ValueKind != JsonValueKind.Object || !encodedData.TryGetProperty(Durable.Aggregate, out var value)
            || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"Durable event requires string aggregate field {Durable.Aggregate}.");
        return value.GetString()!;
    }

    public OpenCodeEvent Create(EventId id, double created, TData data, double sequence,
        LocationRef? location = null, IReadOnlyDictionary<string, JsonElement>? metadata = null)
    {
        var encoded = EncodeData(data);
        var value = new OpenCodeEvent(id, Type, created, encoded, location, metadata,
            new DurableEnvelope(AggregateId(encoded), sequence, Durable.Version));
        value.Validate();
        return value;
    }
}

public sealed class EphemeralEventDefinition<TData> : EventDefinition<TData> where TData : class
{
    public override EventDurability Durability => EventDurability.Ephemeral;
    public override DurableEventMetadata? Durable => null;

    public EphemeralEventDefinition(string type, JsonTypeInfo<TData> dataType, string? identifier = null)
        : base(type, dataType, identifier) { }

    public OpenCodeEvent Create(EventId id, double created, TData data,
        LocationRef? location = null, IReadOnlyDictionary<string, JsonElement>? metadata = null)
    {
        var value = new OpenCodeEvent(id, Type, created, EncodeData(data), location, metadata);
        value.Validate();
        return value;
    }
}

public static class EventDefinitions
{
    public static IReadOnlyList<EventDefinition> Inventory(params EventDefinition[] definitions) =>
        Array.AsReadOnly((EventDefinition[])definitions.Clone());

    public static IReadOnlyDictionary<string, EventDefinition> Latest(IEnumerable<EventDefinition> definitions)
    {
        var result = new OrderedDictionary<string, EventDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!result.TryGetValue(definition.Type, out var existing))
            {
                result.Add(definition.Type, definition);
                continue;
            }
            if (definition.Durable is { } next && existing.Durable is { } previous && next.Version != previous.Version)
            {
                if (next.Version > previous.Version) result[definition.Type] = definition;
                continue;
            }
            if (!ReferenceEquals(definition, existing)) throw new ArgumentException($"Duplicate latest event definition for {definition.Type}.");
        }
        return new ReadOnlyDictionary<string, EventDefinition>(result);
    }

    public static string VersionedType(string type, double version)
    {
        var number = MessageContract.Integer(version, 1).ToString("R", CultureInfo.InvariantCulture);
        var marker = number.IndexOf('E');
        if (marker < 0) return $"{type}.{number}";
        var exponent = int.Parse(number.AsSpan(marker + 1), CultureInfo.InvariantCulture);
        // JavaScript formats integer magnitudes below 1e21 without exponent notation.
        if (exponent < 21)
        {
            var digits = number[..marker].Replace(".", "", StringComparison.Ordinal);
            return $"{type}.{digits}{new string('0', exponent + 1 - digits.Length)}";
        }
        return $"{type}.{number[..marker]}e+{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    public static IReadOnlyDictionary<string, EventDefinition> DurableMap(IEnumerable<EventDefinition> definitions)
    {
        var result = new OrderedDictionary<string, EventDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition.Durable is not { } durable) continue;
            var key = VersionedType(definition.Type, durable.Version);
            if (!result.TryAdd(key, definition)) throw new ArgumentException($"Duplicate durable event definition for {key}.");
        }
        return new ReadOnlyDictionary<string, EventDefinition>(result);
    }
}
