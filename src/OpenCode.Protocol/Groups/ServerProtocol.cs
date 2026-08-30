namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of ServerGroup response from packages/protocol/src/groups/server.ts
/// </summary>
public sealed record ServerInfoResponse(
    [property: JsonPropertyName("urls")] IReadOnlyList<string> Urls
);
