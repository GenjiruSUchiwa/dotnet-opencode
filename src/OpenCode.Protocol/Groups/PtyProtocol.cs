namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of PtyGroup from packages/protocol/src/groups/pty.ts
/// </summary>
public sealed record PtyCreateRequest(
    [property: JsonPropertyName("command"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Command = null,
    [property: JsonPropertyName("args"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Cwd = null,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Title = null,
    [property: JsonPropertyName("env"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, string>>))] IReadOnlyDictionary<string, string>? Env = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    private void Validate()
    {
        if (Env?.Any(pair => pair.Value is null) == true)
            throw new System.Text.Json.JsonException("PTY environment values must be strings.");
    }
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
}
