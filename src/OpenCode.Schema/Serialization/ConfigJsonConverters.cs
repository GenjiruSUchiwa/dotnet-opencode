namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal static class ConfigJson
{
    internal static JsonTypeInfo<T> Type<T>() => (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!;
    internal static T Read<T>(ref Utf8JsonReader reader) where T : class => PromptValidation.Required(JsonSerializer.Deserialize(ref reader, Type<T>()));
    internal static void Write<T>(Utf8JsonWriter writer, T value) where T : class => JsonSerializer.Serialize(writer, PromptValidation.Required(value), Type<T>());
    internal static IReadOnlyDictionary<string, T> Record<T>(IReadOnlyDictionary<string, T> value) where T : class
    {
        PromptValidation.Required(value);
        foreach (var pair in value) PromptValidation.Required(pair.Value);
        return value;
    }
}

public sealed class ConfigRecordJsonConverter<T> : JsonConverter<IReadOnlyDictionary<string, T>> where T : class
{
    public override bool HandleNull => true;
    public override IReadOnlyDictionary<string, T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected configuration map.");
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString()!;
            if (!reader.Read()) throw new JsonException();
            result[name] = ConfigJson.Read<T>(ref reader);
        }
        throw new JsonException("Unterminated configuration map.");
    }
    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, T> value, JsonSerializerOptions options)
    {
        ConfigJson.Record(value);
        writer.WriteStartObject();
        foreach (var pair in value) { writer.WritePropertyName(pair.Key); ConfigJson.Write(writer, pair.Value); }
        writer.WriteEndObject();
    }
}

public sealed class ConfigModelSelectionJsonConverter : JsonConverter<ConfigModelSelection>
{
    public override bool HandleNull => true;
    public override ConfigModelSelection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var input = reader.GetString()!;
            var slash = input.IndexOf('/');
            var hash = input.IndexOf('#', Math.Max(0, slash + 1));
            if (slash <= 0) throw new JsonException("Invalid short model selection.");
            return new ConfigModelSelection(input[..slash], input[(slash + 1)..(hash < 0 ? input.Length : hash)], hash < 0 ? null : input[(hash + 1)..]);
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected model selection string or object.");
        string? provider = null, model = null, variant = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new ConfigModelSelection(provider ?? throw new JsonException("Model selection requires providerID."), model ?? throw new JsonException("Model selection requires model."), variant);
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.ValueTextEquals("providerID"u8) ? 1 : reader.ValueTextEquals("model"u8) ? 2 : reader.ValueTextEquals("variant"u8) ? 3 : 0;
            if (!reader.Read()) throw new JsonException();
            if (field == 0) { reader.Skip(); continue; }
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Model selection segments must be strings.");
            if (field == 1) provider = reader.GetString();
            else if (field == 2) model = reader.GetString();
            else variant = reader.GetString();
        }
        throw new JsonException("Unterminated model selection.");
    }
    public override void Write(Utf8JsonWriter writer, ConfigModelSelection value, JsonSerializerOptions options)
    {
        PromptValidation.Required(value);
        writer.WriteStartObject();
        writer.WriteString("providerID"u8, value.ProviderId);
        writer.WriteString("model"u8, value.Model);
        if (value.Variant is not null) writer.WriteString("variant"u8, value.Variant);
        writer.WriteEndObject();
    }
}

public sealed class ConfigColorJsonConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    internal static string Validate(string value)
    {
        if (value is null || value.Length != 7 || value[0] != '#') throw new JsonException("Config agent color must be #RRGGBB.");
        foreach (var c in value.AsSpan(1)) if (!char.IsAsciiHexDigit(c)) throw new JsonException("Config agent color must be #RRGGBB.");
        return value;
    }
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? Validate(reader.GetString()!) : throw new JsonException("Expected config color string.");
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Validate(value));
}

public sealed class ConfigFormatterJsonConverter : JsonConverter<ConfigFormatterInfo>
{
    private static readonly ConfigRecordJsonConverter<ConfigFormatterEntry> Entries = new();
    public override bool HandleNull => true;
    public override ConfigFormatterInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => new ConfigFormatterInfo.Toggle(true), JsonTokenType.False => new ConfigFormatterInfo.Toggle(false),
        JsonTokenType.StartObject => new ConfigFormatterInfo.Entries(Entries.Read(ref reader, typeToConvert, options)),
        _ => throw new JsonException("Formatter config must be boolean or a map.")
    };
    public override void Write(Utf8JsonWriter writer, ConfigFormatterInfo value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ConfigFormatterInfo.Toggle toggle: writer.WriteBooleanValue(toggle.Value); break;
            case ConfigFormatterInfo.Entries entries: Entries.Write(writer, entries.Values, options); break;
            default: throw new JsonException("Invalid formatter config.");
        }
    }
}

public sealed class ConfigLspJsonConverter : JsonConverter<ConfigLspInfo>
{
    private static readonly ConfigRecordJsonConverter<ConfigLspEntry> Entries = new();
    public override bool HandleNull => true;
    public override ConfigLspInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => new ConfigLspInfo.Toggle(true), JsonTokenType.False => new ConfigLspInfo.Toggle(false),
        JsonTokenType.StartObject => new ConfigLspInfo.Entries(Entries.Read(ref reader, typeToConvert, options)),
        _ => throw new JsonException("LSP config must be boolean or a map.")
    };
    public override void Write(Utf8JsonWriter writer, ConfigLspInfo value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ConfigLspInfo.Toggle toggle: writer.WriteBooleanValue(toggle.Value); break;
            case ConfigLspInfo.Entries entries: Entries.Write(writer, entries.Values, options); break;
            default: throw new JsonException("Invalid LSP config.");
        }
    }
}

public sealed class ConfigLspEntryJsonConverter : JsonConverter<ConfigLspEntry>
{
    public override bool HandleNull => true;
    public override ConfigLspEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected LSP entry object.");
        // Disabled is the first source union branch; other fields are excess in that branch.
        if (document.RootElement.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True) return new ConfigLspDisabled();
        return document.RootElement.Deserialize(OpenCodeJsonContext.Default.ConfigLspServer) ?? throw new JsonException("Missing LSP server.");
    }
    public override void Write(Utf8JsonWriter writer, ConfigLspEntry value, JsonSerializerOptions options)
    {
        if (value is ConfigLspDisabled or ConfigLspServer { Disabled: true })
        {
            writer.WriteStartObject(); writer.WriteBoolean("disabled"u8, true); writer.WriteEndObject(); return;
        }
        if (value is ConfigLspServer server) { ConfigJson.Write(writer, server); return; }
        throw new JsonException("Invalid LSP entry.");
    }
}

public sealed class ConfigPluginJsonConverter : JsonConverter<ConfigPlugin>
{
    public override bool HandleNull => true;
    public override ConfigPlugin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => new ConfigPlugin.Shorthand(reader.GetString()!),
        JsonTokenType.StartObject => ConfigJson.Read<ConfigPluginEntry>(ref reader),
        _ => throw new JsonException("Plugin must be a package string or entry object.")
    };
    public override void Write(Utf8JsonWriter writer, ConfigPlugin value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ConfigPlugin.Shorthand package: writer.WriteStringValue(package.Value); break;
            case ConfigPluginEntry entry: ConfigJson.Write(writer, entry); break;
            default: throw new JsonException("Invalid plugin config.");
        }
    }
}

public sealed class ConfigReferenceJsonConverter : JsonConverter<ConfigReferenceEntry>
{
    public override bool HandleNull => true;
    public override ConfigReferenceEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new ConfigReferenceEntry.Shorthand(reader.GetString()!);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected reference string or object.");
        // Git precedes Local; consider all Git constraints before falling back to Local.
        if (root.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.String
            && OptionalString(root, "branch") && OptionalString(root, "description")
            && (!root.TryGetProperty("hidden", out var hidden) || hidden.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return root.Deserialize(OpenCodeJsonContext.Default.ConfigGitReference)!;
        return root.Deserialize(OpenCodeJsonContext.Default.ConfigLocalReference) ?? throw new JsonException("Invalid local reference.");
    }
    private static bool OptionalString(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.String;
    public override void Write(Utf8JsonWriter writer, ConfigReferenceEntry value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ConfigReferenceEntry.Shorthand text: writer.WriteStringValue(text.Value); break;
            case ConfigGitReference git: ConfigJson.Write(writer, git); break;
            case ConfigLocalReference local: ConfigJson.Write(writer, local); break;
            default: throw new JsonException("Invalid reference config.");
        }
    }
}

public sealed class ConfigModelCostsJsonConverter : JsonConverter<ConfigModelCosts>
{
    private static readonly PromptAttachmentListJsonConverter<ConfigModelCost> Costs = new();
    public override bool HandleNull => true;
    public override ConfigModelCosts Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.StartObject => new ConfigModelCosts.Single(ConfigJson.Read<ConfigModelCost>(ref reader)),
        JsonTokenType.StartArray => new ConfigModelCosts.Multiple(Costs.Read(ref reader, typeToConvert, options)),
        _ => throw new JsonException("Model cost must be an object or array.")
    };
    public override void Write(Utf8JsonWriter writer, ConfigModelCosts value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ConfigModelCosts.Single single: ConfigJson.Write(writer, single.Value); break;
            case ConfigModelCosts.Multiple multiple: Costs.Write(writer, multiple.Values, options); break;
            default: throw new JsonException("Invalid model costs.");
        }
    }
}

public sealed class ConfigWebSearchJsonConverter : JsonConverter<ConfigWebSearchSelection>
{
    public override bool HandleNull => true;
    public override ConfigWebSearchSelection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.False => new ConfigWebSearchSelection.Disabled(),
        JsonTokenType.StartObject => ConfigJson.Read<ConfigWebSearchInfo>(ref reader),
        _ => throw new JsonException("Web search config must be false or a provider object.")
    };
    public override void Write(Utf8JsonWriter writer, ConfigWebSearchSelection value, JsonSerializerOptions options)
    {
        if (value is ConfigWebSearchSelection.Disabled) { writer.WriteBooleanValue(false); return; }
        if (value is ConfigWebSearchInfo info) { ConfigJson.Write(writer, info); return; }
        throw new JsonException("Invalid web search config.");
    }
}

public sealed class ConfigWarmingJsonConverter : JsonConverter<ConfigWarming>
{
    public override bool HandleNull => true;
    public override ConfigWarming Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => new ConfigWarming.Toggle(true), JsonTokenType.False => new ConfigWarming.Toggle(false),
        JsonTokenType.StartObject => ConfigJson.Read<ConfigWarmingInfo>(ref reader),
        _ => throw new JsonException("Warming config must be boolean or an object.")
    };
    public override void Write(Utf8JsonWriter writer, ConfigWarming value, JsonSerializerOptions options)
    {
        if (value is ConfigWarming.Toggle toggle) { writer.WriteBooleanValue(toggle.Value); return; }
        if (value is ConfigWarmingInfo info) { ConfigJson.Write(writer, info); return; }
        throw new JsonException("Invalid warming config.");
    }
}

public sealed class ConfigAutoUpdateJsonConverter : JsonConverter<ConfigAutoUpdate>
{
    public override bool HandleNull => true;
    public override ConfigAutoUpdate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return new ConfigAutoUpdate.Toggle(reader.GetBoolean());
        if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("notify"u8)) return new ConfigAutoUpdate.Notify();
        throw new JsonException("Autoupdate must be boolean or notify.");
    }
    public override void Write(Utf8JsonWriter writer, ConfigAutoUpdate value, JsonSerializerOptions options)
    {
        if (value is ConfigAutoUpdate.Toggle toggle) { writer.WriteBooleanValue(toggle.Value); return; }
        if (value is ConfigAutoUpdate.Notify) { writer.WriteStringValue("notify"u8); return; }
        throw new JsonException("Invalid autoupdate config.");
    }
}

public sealed class ConfigDurationJsonConverter : JsonConverter<ConfigDuration>
{
    public override bool HandleNull => true;
    public override ConfigDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? ConfigDuration.Parse(reader.GetString()!) : throw new JsonException("Expected duration string.");
    public override void Write(Utf8JsonWriter writer, ConfigDuration value, JsonSerializerOptions options) => writer.WriteStringValue(PromptValidation.Required(value).Encode());
}
