namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of SessionGroup input/output shapes from packages/protocol/src/groups/session.ts
/// </summary>
public sealed record CreateSessionRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("directory")] string? Directory = null,
    [property: JsonPropertyName("parentID")] SessionId? ParentId = null,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("model")] ModelRef? Model = null
);

public sealed record InterruptSessionResponse(
    [property: JsonPropertyName("interrupted")] bool Interrupted
);
