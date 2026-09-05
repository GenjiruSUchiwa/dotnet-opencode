namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class EventLocationJsonConverter : JsonConverter<LocationRef>
{
    public override bool HandleNull => true;

    public override LocationRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected event location object.");
        string? directory = null;
        WorkspaceId? workspace = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                var location = new LocationRef(directory ?? throw new JsonException("Location requires directory."), workspace);
                Validate(location);
                return location;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.ValueTextEquals("directory"u8) ? 1 : reader.ValueTextEquals("workspaceID"u8) ? 2 : 0;
            if (!reader.Read()) throw new JsonException();
            if (field == 0) { reader.Skip(); continue; }
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Location fields must be strings.");
            if (field == 1) directory = reader.GetString();
            else
            {
                var id = reader.GetString()!;
                if (!id.StartsWith("wrk", StringComparison.Ordinal)) throw new JsonException("Workspace ID must start with wrk.");
                workspace = WorkspaceId.FromExisting(id);
            }
        }
        throw new JsonException("Unterminated event location.");
    }

    internal static void Validate(LocationRef value)
    {
        if (value?.Directory is null) throw new JsonException("Event location requires directory.");
        if (value.WorkspaceId is { } workspace && (!workspace.IsInitialized() || !workspace.Value.StartsWith("wrk", StringComparison.Ordinal)))
            throw new JsonException("Workspace ID must start with wrk.");
    }

    public override void Write(Utf8JsonWriter writer, LocationRef value, JsonSerializerOptions options)
    {
        Validate(value);
        writer.WriteStartObject();
        writer.WriteString("directory"u8, value.Directory);
        if (value.WorkspaceId is { } workspace) writer.WriteString("workspaceID"u8, workspace.Value);
        writer.WriteEndObject();
    }
}
