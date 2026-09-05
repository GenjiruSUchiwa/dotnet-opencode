namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

public sealed class PromptBase64JsonConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected base64 string.");
        return PromptBase64.Create(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptBase64.Create(value));
}

// Property-level only: missing optional keys are allowed, but explicit JSON null is not.
public sealed class NonNullPromptJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) throw new JsonException("Prompt property must be omitted rather than null.");
        return PromptValidation.Required(JsonSerializer.Deserialize(ref reader,
            (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, PromptValidation.Required(value),
            (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!);
}

public sealed class PromptAttachmentListJsonConverter<T> : JsonConverter<IReadOnlyList<T>> where T : class
{
    public override bool HandleNull => true;

    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected prompt attachment array.");
        var result = new List<T>();
        var typeInfo = (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return result;
            result.Add(PromptValidation.Required(JsonSerializer.Deserialize(ref reader, typeInfo)));
        }
        throw new JsonException("Unterminated prompt attachment array.");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
    {
        PromptValidation.Required(value);
        var typeInfo = (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!;
        writer.WriteStartArray();
        foreach (var item in value) JsonSerializer.Serialize(writer, PromptValidation.Required(item), typeInfo);
        writer.WriteEndArray();
    }
}
