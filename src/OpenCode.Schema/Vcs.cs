namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record VcsBranch(
    [property: JsonPropertyName("current")] string? Current = null,
    [property: JsonPropertyName("default")] string? Default = null
);

public sealed record VcsInfo(
    [property: JsonPropertyName("branch")] VcsBranch Branch
);

[JsonConverter(typeof(JsonStringEnumConverter<VcsFileChangeStatus>))]
public enum VcsFileChangeStatus
{
    [JsonStringEnumMemberName("added")]
    Added,
    [JsonStringEnumMemberName("deleted")]
    Deleted,
    [JsonStringEnumMemberName("modified")]
    Modified
}

public sealed record VcsFileStatus(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions,
    [property: JsonPropertyName("status")] VcsFileChangeStatus Status
);
