namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<FileDiffStatus>))]
public enum FileDiffStatus
{
    [JsonStringEnumMemberName("added")]
    Added,
    [JsonStringEnumMemberName("deleted")]
    Deleted,
    [JsonStringEnumMemberName("modified")]
    Modified
}

/// <summary>
/// 1:1 port of FileDiff.Info from packages/schema/src/file-diff.ts
/// </summary>
public sealed record FileDiffInfo(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("patch")] string Patch,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions,
    [property: JsonPropertyName("status")] FileDiffStatus Status
);
