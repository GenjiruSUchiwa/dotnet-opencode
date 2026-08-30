namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record WorktreeCreateInput(
    [property: JsonPropertyName("projectID")] ProjectId ProjectId,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("name")] string? Name = null
);

public sealed record WorktreeRemoveInput(
    [property: JsonPropertyName("projectID")] ProjectId ProjectId,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("force")] bool Force
);

public sealed record WorktreeDirectory(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("strategy")] string? Strategy = null
);

public sealed record WorktreeInfo(
    [property: JsonPropertyName("directory")] string Directory
);
