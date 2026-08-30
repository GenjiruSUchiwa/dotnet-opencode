namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<FileSystemEntryType>))]
public enum FileSystemEntryType
{
    [JsonStringEnumMemberName("file")]
    File,
    [JsonStringEnumMemberName("directory")]
    Directory
}

public sealed record FileSystemEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] FileSystemEntryType Type
);

public sealed record FileSystemSubmatch(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End
);

public sealed record FileSystemMatch(
    [property: JsonPropertyName("entry")] FileSystemEntry Entry,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("submatches")] IReadOnlyList<FileSystemSubmatch> Submatches
);

public sealed record FileSystemFindInput(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("type")] FileSystemEntryType? Type = null,
    [property: JsonPropertyName("limit")] int? Limit = null
);
