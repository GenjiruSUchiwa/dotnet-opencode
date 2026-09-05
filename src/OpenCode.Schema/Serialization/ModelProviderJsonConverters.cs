namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

internal static class ModelProviderContract
{
    internal static T Id<T>(T value, bool initialized) where T : struct
    {
        if (!initialized) throw new JsonException("Required prompt value cannot be null.");
        return value;
    }
    internal static double Integer(double value)
    {
        if (!double.IsFinite(value) || Math.Truncate(value) != value) throw new JsonException("Expected a finite integer.");
        return value;
    }
}

public sealed class IntegerNumberJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ModelProviderContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader));
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(ModelProviderContract.Integer(value));
}

public sealed class OptionalIntegerNumberJsonConverter : JsonConverter<double?>
{
    public override bool HandleNull => true;
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ModelProviderContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader));
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Optional integer must be omitted, not null.");
        writer.WriteNumberValue(ModelProviderContract.Integer(value.Value));
    }
}

public sealed class ProviderActivationJsonConverter : JsonConverter<ProviderActivation>
{
    internal static ProviderActivation Validate(ProviderActivation value) => value is ProviderActivation.Auto or ProviderActivation.Enabled or ProviderActivation.Disabled
        ? value : throw new JsonException("Unknown provider activation.");
    public override ProviderActivation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected provider activation string.");
        return reader.GetString() switch
        {
            "auto" => ProviderActivation.Auto, "enabled" => ProviderActivation.Enabled, "disabled" => ProviderActivation.Disabled,
            _ => throw new JsonException("Unknown provider activation.")
        };
    }
    public override void Write(Utf8JsonWriter writer, ProviderActivation value, JsonSerializerOptions options) => writer.WriteStringValue(Validate(value) switch
    {
        ProviderActivation.Auto => "auto", ProviderActivation.Enabled => "enabled", _ => "disabled"
    });
}

public sealed class ProviderHeadersJsonConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    internal static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> value)
    {
        PromptValidation.Required(value);
        foreach (var header in value) PromptValidation.Required(header.Value);
        return value;
    }

    public override bool HandleNull => true;
    public override IReadOnlyDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected provider headers object.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new JsonException("Header values must be strings.");
            result[name] = reader.GetString()!;
        }
        throw new JsonException("Unterminated provider headers object.");
    }
    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
    {
        PromptValidation.Required(value);
        writer.WriteStartObject();
        foreach (var header in value) writer.WriteString(header.Key, PromptValidation.Required(header.Value));
        writer.WriteEndObject();
    }
}
