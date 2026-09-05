namespace OpenCode.Protocol.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

// Preserve Schema type identity while its permission DTOs lack required/optional-null guards.
internal static class PermissionWire
{
    internal static JsonElement Required(JsonElement value, string field, JsonValueKind kind)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(field, out var property) || property.ValueKind != kind)
            throw new JsonException($"Permission wire value requires {field} of kind {kind}.");
        return property;
    }

    internal static void Optional(JsonElement value, string field, JsonValueKind kind)
    {
        if (value.TryGetProperty(field, out var property) && property.ValueKind != kind)
            throw new JsonException($"Optional {field} must have kind {kind} or be omitted, not null.");
    }

    internal static void Strings(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new JsonException("Permission resources/save must be arrays of strings.");
    }

    internal static void ValidateId(PermissionId id)
    {
        if (!id.IsInitialized() || !id.Value.StartsWith("per", StringComparison.Ordinal))
            throw new JsonException("Permission ID must start with per.");
    }

    internal static void ValidateSource(PermissionSource source)
    {
        if (source.Type != "tool" || source.MessageId is null || source.Id is null)
            throw new JsonException("Permission source requires type tool, messageID, and id strings.");
    }
}

public sealed class PermissionRequestWireConverter : JsonConverter<PermissionRequest>
{
    public override bool HandleNull => true;
    public override PermissionRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        PermissionWire.Required(root, "id", JsonValueKind.String);
        PermissionWire.Required(root, "sessionID", JsonValueKind.String);
        PermissionWire.Required(root, "action", JsonValueKind.String);
        PermissionWire.Strings(PermissionWire.Required(root, "resources", JsonValueKind.Array));
        PermissionWire.Optional(root, "save", JsonValueKind.Array);
        if (root.TryGetProperty("save", out var save)) PermissionWire.Strings(save);
        PermissionWire.Optional(root, "metadata", JsonValueKind.Object);
        PermissionWire.Optional(root, "message", JsonValueKind.String);
        PermissionWire.Optional(root, "source", JsonValueKind.Object);
        if (root.TryGetProperty("source", out var source))
        {
            if (PermissionWire.Required(source, "type", JsonValueKind.String).GetString() != "tool")
                throw new JsonException("Unknown permission source variant.");
            PermissionWire.Required(source, "messageID", JsonValueKind.String);
            PermissionWire.Required(source, "id", JsonValueKind.String);
        }
        var value = root.Deserialize(OpenCodeJsonContext.Default.PermissionRequest) ?? throw new JsonException("Permission request must not be null.");
        Validate(value);
        return value;
    }

    public override void Write(Utf8JsonWriter writer, PermissionRequest value, JsonSerializerOptions options)
    {
        Validate(value);
        JsonSerializer.Serialize(writer, value, OpenCodeJsonContext.Default.PermissionRequest);
    }

    private static void Validate(PermissionRequest value)
    {
        if (value is null || !value.SessionId.IsInitialized() || value.Action is null || value.Resources is null
            || value.Resources.Any(item => item is null) || value.Save?.Any(item => item is null) == true)
            throw new JsonException("Permission request requires sessionID, action, and string resource/save entries.");
        PermissionWire.ValidateId(value.Id);
        if (value.Source is not null) PermissionWire.ValidateSource(value.Source);
    }
}

public sealed class PermissionReplyWireConverter : JsonConverter<PermissionReply>
{
    public override PermissionReply Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Permission reply must be a string.");
        return reader.GetString() switch
        {
            "once" => PermissionReply.Once, "always" => PermissionReply.Always, "reject" => PermissionReply.Reject,
            _ => throw new JsonException("Permission reply must be once, always, or reject.")
        };
    }

    public override void Write(Utf8JsonWriter writer, PermissionReply value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PermissionReply.Once => "once", PermissionReply.Always => "always", PermissionReply.Reject => "reject",
            _ => throw new JsonException("Permission reply must be once, always, or reject.")
        });
}

public sealed class PermissionLocationWireConverter : JsonConverter<LocationInfo>
{
    public override bool HandleNull => true;
    public override LocationInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        PermissionWire.Required(root, "directory", JsonValueKind.String);
        PermissionWire.Optional(root, "workspaceID", JsonValueKind.String);
        if (root.TryGetProperty("workspaceID", out var workspace) && !workspace.GetString()!.StartsWith("wrk", StringComparison.Ordinal))
            throw new JsonException("Workspace ID must start with wrk.");
        var project = PermissionWire.Required(root, "project", JsonValueKind.Object);
        PermissionWire.Required(project, "id", JsonValueKind.String);
        PermissionWire.Required(project, "directory", JsonValueKind.String);
        PermissionWire.Required(project, "canonical", JsonValueKind.String);
        return root.Deserialize(OpenCodeJsonContext.Default.LocationInfo) ?? throw new JsonException("Location must not be null.");
    }

    public override void Write(Utf8JsonWriter writer, LocationInfo value, JsonSerializerOptions options)
    {
        if (value is null || value.Directory is null || value.Project is null || !value.Project.Id.IsInitialized()
            || value.Project.Directory is null || value.Project.Canonical is null
            || (value.WorkspaceId is WorkspaceId workspace && (!workspace.IsInitialized() || !workspace.Value.StartsWith("wrk", StringComparison.Ordinal))))
            throw new JsonException("Location requires canonical directory/project fields and an optional workspace identifier.");
        JsonSerializer.Serialize(writer, value, OpenCodeJsonContext.Default.LocationInfo);
    }
}
