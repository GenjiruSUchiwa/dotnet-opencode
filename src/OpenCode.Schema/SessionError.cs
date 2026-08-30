namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of SessionError from packages/schema/src/session-error.ts
/// </summary>
public sealed record SessionStructuredError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("status")] int? Status = null
);
