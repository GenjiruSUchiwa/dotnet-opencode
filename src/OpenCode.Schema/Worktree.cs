namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record WorktreeCreateInput(
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("strategy"), JsonRequired, JsonConverter(typeof(WorktreeTrimmedStringConverter))] string Strategy,
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? From = null,
    [property: JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(WorktreeTrimmedStringConverter))] string? Branch = null,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null
);

public sealed record WorktreeRemoveInput(
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("force"), JsonRequired] bool Force
);

public sealed record WorktreeDirectory(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("strategy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Strategy = null
);

public sealed record WorktreeInfo(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory
);
