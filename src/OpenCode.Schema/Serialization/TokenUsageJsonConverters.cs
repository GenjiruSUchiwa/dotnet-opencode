namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class TokenCacheUsageJsonConverter : JsonConverter<TokenCacheUsage>
{
    public override bool HandleNull => true;

    public override TokenCacheUsage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader);

    internal static TokenCacheUsage ReadValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected token cache object.");
        double read = 0, write = 0;
        var fields = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (fields != 3) throw new JsonException("Token cache requires read and write.");
                return new TokenCacheUsage(read, write);
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.ValueTextEquals("read"u8) ? 1 : reader.ValueTextEquals("write"u8) ? 2 : 0;
            if (!reader.Read()) throw new JsonException();
            if (field == 0) { reader.Skip(); continue; }
            var value = FiniteNumberJsonConverter.ReadValue(ref reader);
            if (field == 1) read = value;
            else write = value;
            fields |= field;
        }
        throw new JsonException("Unterminated token cache object.");
    }

    internal static void WriteValue(Utf8JsonWriter writer, TokenCacheUsage value)
    {
        if (value is null) throw new JsonException("Token cache is required.");
        writer.WriteStartObject();
        writer.WritePropertyName("read"u8);
        FiniteNumberJsonConverter.WriteValue(writer, value.Read);
        writer.WritePropertyName("write"u8);
        FiniteNumberJsonConverter.WriteValue(writer, value.Write);
        writer.WriteEndObject();
    }

    public override void Write(Utf8JsonWriter writer, TokenCacheUsage value, JsonSerializerOptions options) =>
        WriteValue(writer, value);
}

public sealed class TokenUsageInfoJsonConverter : JsonConverter<TokenUsageInfo>
{
    public override bool HandleNull => true;

    public override TokenUsageInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected token usage object.");
        double input = 0, output = 0, reasoning = 0;
        TokenCacheUsage? cache = null;
        var fields = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (fields != 15) throw new JsonException("Token usage requires input, output, reasoning, and cache.");
                return new TokenUsageInfo(input, output, reasoning, cache!);
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.ValueTextEquals("input"u8) ? 1 : reader.ValueTextEquals("output"u8) ? 2
                : reader.ValueTextEquals("reasoning"u8) ? 4 : reader.ValueTextEquals("cache"u8) ? 8 : 0;
            if (!reader.Read()) throw new JsonException();
            if (field == 0) { reader.Skip(); continue; }
            if (field == 8) cache = TokenCacheUsageJsonConverter.ReadValue(ref reader);
            else
            {
                var value = FiniteNumberJsonConverter.ReadValue(ref reader);
                if (field == 1) input = value;
                else if (field == 2) output = value;
                else reasoning = value;
            }
            fields |= field;
        }
        throw new JsonException("Unterminated token usage object.");
    }

    public override void Write(Utf8JsonWriter writer, TokenUsageInfo value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Token usage is required.");
        writer.WriteStartObject();
        writer.WritePropertyName("input"u8);
        FiniteNumberJsonConverter.WriteValue(writer, value.Input);
        writer.WritePropertyName("output"u8);
        FiniteNumberJsonConverter.WriteValue(writer, value.Output);
        writer.WritePropertyName("reasoning"u8);
        FiniteNumberJsonConverter.WriteValue(writer, value.Reasoning);
        writer.WritePropertyName("cache"u8);
        TokenCacheUsageJsonConverter.WriteValue(writer, value.Cache);
        writer.WriteEndObject();
    }
}
