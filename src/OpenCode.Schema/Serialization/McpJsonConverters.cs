namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class McpOAuthSettingJsonConverter : JsonConverter<McpOAuthSetting>
{
    public override bool HandleNull => true;
    public override McpOAuthSetting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.False => McpOAuthDisabled.Instance,
        JsonTokenType.StartObject => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.McpOAuthConfig)!,
        _ => throw new JsonException("MCP OAuth must be a configuration object or false.")
    };
    public override void Write(Utf8JsonWriter writer, McpOAuthSetting value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case McpOAuthDisabled: writer.WriteBooleanValue(false); break;
            case McpOAuthConfig config: JsonSerializer.Serialize(writer, config, OpenCodeJsonContext.Default.McpOAuthConfig); break;
            default: throw new JsonException("MCP OAuth must be a configuration object or false.");
        }
    }
}

public sealed class McpCallbackPortJsonConverter : JsonConverter<double?>
{
    public override bool HandleNull => true;
    internal static double Validate(double value)
    {
        MessageContract.Integer(value, 1);
        if (value > 65_535) throw new JsonException("MCP callback port must be between 1 and 65535.");
        return value;
    }
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Validate(FiniteNumberJsonConverter.ReadValue(ref reader));
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("MCP callback port must be omitted, not null.");
        writer.WriteNumberValue(Validate(value.Value));
    }
}

public sealed class McpServersJsonConverter : JsonConverter<IReadOnlyDictionary<string, McpServerConfig>>
{
    public override bool HandleNull => true;
    public override IReadOnlyDictionary<string, McpServerConfig> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected MCP servers object.");
        var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString()!;
            if (!reader.Read()) throw new JsonException();
            result[name] = JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.McpServerConfig)
                ?? throw new JsonException("MCP server config cannot be null.");
        }
        throw new JsonException("Unterminated MCP servers object.");
    }
    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, McpServerConfig> value, JsonSerializerOptions options)
    {
        PromptValidation.Required(value);
        writer.WriteStartObject();
        foreach (var server in value)
        {
            writer.WritePropertyName(server.Key);
            JsonSerializer.Serialize(writer, PromptValidation.Required(server.Value), OpenCodeJsonContext.Default.McpServerConfig);
        }
        writer.WriteEndObject();
    }
}
