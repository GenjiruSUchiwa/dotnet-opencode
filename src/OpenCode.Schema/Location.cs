namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Location.Ref and Location.Info from packages/schema/src/location.ts
/// </summary>
public sealed record LocationRef(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("workspaceID")] WorkspaceId? WorkspaceId = null
);

public sealed record LocationProjectInfo(
    [property: JsonPropertyName("id")] ProjectId Id,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("canonical")] string Canonical
);

public sealed record LocationInfo(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("project")] LocationProjectInfo Project,
    [property: JsonPropertyName("workspaceID")] WorkspaceId? WorkspaceId = null
);
