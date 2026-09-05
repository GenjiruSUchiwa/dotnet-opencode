namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonConverter(typeof(SourceStringEnumJsonConverter<FileDiffStatus>))]
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
    [property: JsonPropertyName("file"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string File,
    [property: JsonPropertyName("patch"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Patch,
    [property: JsonPropertyName("additions"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Additions,
    [property: JsonPropertyName("deletions"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Deletions,
    [property: JsonPropertyName("status"), JsonRequired] FileDiffStatus Status
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(File, Patch);
}
