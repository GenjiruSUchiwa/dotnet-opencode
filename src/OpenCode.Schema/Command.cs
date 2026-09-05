namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Command.Info from packages/schema/src/command.ts
/// </summary>
public sealed record CommandInfo(
    [property: JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Name,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Name);
}
