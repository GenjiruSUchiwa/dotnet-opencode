namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Decode the selected union branch before reading branch-specific fields.</summary>
public sealed class PluginInfoJsonConverter : JsonConverter<PluginInfo>
{
    public override bool HandleNull => true;

    public override PluginInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var status = Required(root, "status");
        if (status.ValueKind != JsonValueKind.String || status.GetString() is not ("active" or "failed"))
            throw new JsonException("Plugin status must be active or failed.");
        var source = Required(root, "source").Deserialize(OpenCodeJsonContext.Default.PluginSource)
            ?? throw new JsonException("Plugin requires source.");
        var tui = Required(root, "tui");
        if (tui.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("Plugin tui must be boolean.");
        var id = root.TryGetProperty("id", out var encodedId)
            ? encodedId.Deserialize(OpenCodeJsonContext.Default.PluginId) : (PluginId?)null;
        if (status.ValueEquals("active"))
            return new(source, "active", tui.GetBoolean(), id ?? throw new JsonException("Active plugin requires id."));
        var error = Required(root, "error");
        if (error.ValueKind != JsonValueKind.String) throw new JsonException("Failed plugin requires error string.");
        return new(source, "failed", tui.GetBoolean(), id, error.GetString());
    }

    public override void Write(Utf8JsonWriter writer, PluginInfo value, JsonSerializerOptions options)
    {
        if (value is null || value.Source is null) throw new JsonException("Plugin requires source.");
        if (value.Status is not ("active" or "failed")) throw new JsonException("Plugin status must be active or failed.");
        if (value.Status == "active" && value.Id is null) throw new JsonException("Active plugin requires id.");
        if (value.Status == "failed" && value.Error is null) throw new JsonException("Failed plugin requires error string.");
        writer.WriteStartObject();
        if (value.Id is { } id)
        {
            writer.WritePropertyName("id");
            JsonSerializer.Serialize(writer, id, OpenCodeJsonContext.Default.PluginId);
        }
        writer.WritePropertyName("source");
        JsonSerializer.Serialize(writer, value.Source, OpenCodeJsonContext.Default.PluginSource);
        writer.WriteString("status", value.Status);
        if (value.Status == "failed") writer.WriteString("error", value.Error);
        writer.WriteBoolean("tui", value.Tui);
        writer.WriteEndObject();
    }

    private static JsonElement Required(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            ? value : throw new JsonException($"Plugin requires {name}.");
}
