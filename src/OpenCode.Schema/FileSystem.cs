namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonConverter(typeof(SourceStringEnumJsonConverter<FileSystemEntryType>))]
public enum FileSystemEntryType
{
    [JsonStringEnumMemberName("file")]
    File,
    [JsonStringEnumMemberName("directory")]
    Directory
}

public sealed record FileSystemEntry(
    [property: JsonPropertyName("path"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Path,
    [property: JsonPropertyName("type"), JsonRequired] FileSystemEntryType Type
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Path);
}

public sealed record FileSystemSubmatch(
    [property: JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Text,
    [property: JsonPropertyName("start"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Start,
    [property: JsonPropertyName("end"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int End
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Text);
}

public sealed record FileSystemMatch(
    [property: JsonPropertyName("entry"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<FileSystemEntry>))] FileSystemEntry Entry,
    [property: JsonPropertyName("line"), JsonRequired, JsonConverter(typeof(PositiveIntegerJsonConverter<int>))] int Line,
    [property: JsonPropertyName("offset"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Offset,
    [property: JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Text,
    [property: JsonPropertyName("submatches"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<FileSystemSubmatch>))] IReadOnlyList<FileSystemSubmatch> Submatches
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Entry, Text, Submatches);
}

public sealed record FileSystemFindInput(
    [property: JsonPropertyName("query"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Query,
    [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<FileSystemEntryType>))] FileSystemEntryType? Type = null,
    [property: JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter<int>))] int? Limit = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Query);
}
