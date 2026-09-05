namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record VcsBranch(
    [property: JsonPropertyName("current"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Current = null,
    [property: JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Default = null
);

public sealed record VcsInfo(
    [property: JsonPropertyName("branch"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<VcsBranch>))] VcsBranch Branch
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Branch);
}

[JsonConverter(typeof(SourceStringEnumJsonConverter<VcsFileChangeStatus>))]
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
    [property: JsonPropertyName("file"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string File,
    [property: JsonPropertyName("additions"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Additions,
    [property: JsonPropertyName("deletions"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Deletions,
    [property: JsonPropertyName("status"), JsonRequired] VcsFileChangeStatus Status
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(File);
}
