namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of ModelGroup from packages/protocol/src/groups/model.ts
/// </summary>
public sealed record LocationResponse<T>(
    [property: JsonPropertyName("location")] LocationInfo? Location,
    [property: JsonPropertyName("data")] T Data
);
