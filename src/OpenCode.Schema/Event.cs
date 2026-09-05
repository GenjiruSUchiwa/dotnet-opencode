namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(EventIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct EventId
{
    public const string Prefix = "evt_";
    public static EventId FromExisting(string value)
    {
        if (value?.StartsWith(Prefix, StringComparison.Ordinal) != true)
            throw new JsonException("Event ID must start with evt_.");
        return From(value);
    }

    public static EventId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("Event ID must start with evt_.");
    public override string ToString() => Value;
    public static implicit operator string(EventId id) => id.Value;
    public static explicit operator EventId(string value) => FromExisting(value);
}

public sealed class EventIdJsonConverter() : ScalarJsonConverter<EventId, string>(EventId.FromExisting, static value => value.Value)
{
    public override EventId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected event ID string.");
        return EventId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, EventId value, JsonSerializerOptions options)
    {
        if (!value.IsInitialized() || !value.Value.StartsWith(EventId.Prefix, StringComparison.Ordinal))
            throw new JsonException("Event ID must start with evt_.");
        writer.WriteStringValue(value.Value);
    }
}

public sealed record DurableEnvelope(string AggregateId, double Seq, double Version)
{
    [JsonPropertyName("aggregateID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string AggregateId { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(AggregateId);
    [JsonPropertyName("seq"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter))]
    public double Seq { get; init => field = MessageContract.Integer(value, 0); } = MessageContract.Integer(Seq, 0);
    [JsonPropertyName("version"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter))]
    public double Version { get; init => field = MessageContract.Integer(value, 1); } = MessageContract.Integer(Version, 1);
}

/// <summary>
/// Encoded event envelope. A definition supplies literal type/version and data validation.
/// </summary>
[JsonConverter(typeof(OpenCodeEventJsonConverter))]
public sealed record OpenCodeEvent(
    [property: JsonPropertyName("id"), JsonRequired] EventId Id,
    string Type,
    double Created,
    [property: JsonPropertyName("data"), JsonRequired] JsonElement Data,
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(EventLocationJsonConverter))] LocationRef? Location = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("durable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<DurableEnvelope>))] DurableEnvelope? Durable = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    [JsonPropertyName("type"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Type { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Type);
    [JsonPropertyName("created"), JsonRequired, JsonConverter(typeof(FiniteNumberJsonConverter))]
    public double Created { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Created);

    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    public void Validate()
    {
        if (!Id.IsInitialized() || !Id.Value.StartsWith(EventId.Prefix, StringComparison.Ordinal))
            throw new JsonException("Event ID must start with evt_.");
        if (Data.ValueKind != JsonValueKind.Object) throw new JsonException("Event data must be an object.");
        if (Location is not null) EventLocationJsonConverter.Validate(Location);
        if (Durable is not null)
        {
            PromptValidation.Required(Durable.AggregateId);
            MessageContract.Integer(Durable.Seq, 0);
            MessageContract.Integer(Durable.Version, 1);
        }
    }
}
