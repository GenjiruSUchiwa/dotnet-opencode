namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record ServerConnectedEventData(
    [property: JsonPropertyName("version")] string? Version = null
);

public sealed record McpStatusChangedEventData(
    [property: JsonPropertyName("server")] string Server
);
