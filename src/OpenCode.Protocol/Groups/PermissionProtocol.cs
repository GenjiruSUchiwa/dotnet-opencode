namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of PermissionGroup from packages/protocol/src/groups/permission.ts
/// </summary>
public sealed record PermissionSavedListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PermissionSavedInfo> Data
);
