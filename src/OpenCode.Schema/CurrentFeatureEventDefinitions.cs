namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record FileSystemChangedEventData(
    [property: JsonPropertyName("file"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string File,
    [property: JsonPropertyName("event"), JsonRequired] string Event) : IJsonOnSerializing, IJsonOnDeserialized
{
    private void Validate()
    {
        SourceObjectContract.Required(File);
        if (Event is not ("add" or "change" or "unlink")) throw new JsonException("Unknown filesystem change event.");
    }
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
}

public sealed record PermissionRepliedEventData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("requestID"), JsonRequired] PermissionId RequestId,
    [property: JsonPropertyName("reply"), JsonRequired] PermissionReply Reply);

public sealed record PluginAddedEventData([property: JsonPropertyName("id"), JsonRequired] PluginId Id);

public static class FileSystemEventDefinitions
{
    public static readonly EphemeralEventDefinition<FileSystemChangedEventData> Changed = new("filesystem.changed", OpenCodeJsonContext.Default.FileSystemChangedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Changed);
}

public static class PermissionEventDefinitions
{
    public static readonly EphemeralEventDefinition<PermissionRequest> Asked = new("permission.asked", OpenCodeJsonContext.Default.PermissionRequest);
    public static readonly EphemeralEventDefinition<PermissionRepliedEventData> Replied = new("permission.replied", OpenCodeJsonContext.Default.PermissionRepliedEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Asked, Replied);
}

public static class PluginEventDefinitions
{
    public static readonly EphemeralEventDefinition<PluginAddedEventData> Added = new("plugin.added", OpenCodeJsonContext.Default.PluginAddedEventData);
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("plugin.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Added, Updated);
}

public static class WebSearchEventDefinitions
{
    public static readonly EphemeralEventDefinition<EmptyEventData> Updated = new("websearch.updated", OpenCodeJsonContext.Default.EmptyEventData);
    public static IReadOnlyList<EventDefinition> Definitions { get; } = EventDefinitions.Inventory(Updated);
}
