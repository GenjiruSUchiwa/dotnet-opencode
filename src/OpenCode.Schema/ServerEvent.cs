namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>Shared exact empty data shape for invalidation and server lifecycle events.</summary>
public sealed record EmptyEventData
{
    public static EmptyEventData Instance { get; } = new();
}

/// <summary>Outgoing connection-local Protocol frame, not a generic Event.Payload.</summary>
public sealed record ServerConnectedFrame([property: JsonPropertyName("id")] EventId Id)
{
    [JsonPropertyName("type")]
    public string Type => "server.connected";
    [JsonPropertyName("data")]
    public EmptyEventData Data => EmptyEventData.Instance;
}

/// <summary>Shared server-only data shape for MCP status, resources, and tools change events.</summary>
public sealed record McpStatusChangedEventData(string Server)
{
    [JsonPropertyName("server"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Server { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Server);
}
