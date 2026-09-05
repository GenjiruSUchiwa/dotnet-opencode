namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Location.Ref and Location.Info from packages/schema/src/location.ts
/// </summary>
public sealed record LocationRef(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("workspaceID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<WorkspaceId>))] WorkspaceId? WorkspaceId = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Directory);
}

public sealed record LocationProjectInfo(
    [property: JsonPropertyName("id"), JsonRequired] ProjectId Id,
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("canonical"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Canonical
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Directory, Canonical);
}

public sealed record LocationInfo(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("project"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<LocationProjectInfo>))] LocationProjectInfo Project,
    [property: JsonPropertyName("workspaceID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<WorkspaceId>))] WorkspaceId? WorkspaceId = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Directory, Project);
}
