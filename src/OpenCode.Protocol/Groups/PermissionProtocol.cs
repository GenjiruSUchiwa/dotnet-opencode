namespace OpenCode.Protocol.Groups;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Serialization;
using OpenCode.Schema;

public sealed record PermissionSavedListResponse(
    [property: JsonPropertyName("data"), JsonRequired] IReadOnlyList<PermissionSavedInfo> Data
) : IJsonOnDeserialized, IJsonOnSerializing
{
    private void Validate()
    {
        if (Data is null || Data.Any(item => item is null)) throw new JsonException("Saved permission response requires a non-null data array.");
    }
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    void IJsonOnSerializing.OnSerializing() => Validate();
}

public sealed record PermissionCreateInput(
    [property: JsonPropertyName("action"), JsonRequired] string Action,
    [property: JsonPropertyName("resources"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string> Resources,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<PermissionId>))] PermissionId? Id = null,
    [property: JsonPropertyName("save"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Save = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PermissionSource>))] PermissionSource? Source = null,
    [property: JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<AgentId>))] AgentId? Agent = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    public void Validate()
    {
        if (Action is null || Resources is null || Resources.Any(value => value is null) || Save?.Any(value => value is null) == true)
            throw new JsonException("Permission action and resource/save entries must be strings.");
        if (Id is PermissionId id) PermissionWire.ValidateId(id);
        if (Agent is AgentId agent && !agent.IsInitialized()) throw new JsonException("Agent must be an initialized string identifier.");
        if (Source is not null) PermissionWire.ValidateSource(Source);
    }
}

public sealed record PermissionReplyInput(
    [property: JsonPropertyName("reply"), JsonRequired, JsonConverter(typeof(PermissionReplyWireConverter))] PermissionReply Reply,
    [property: JsonPropertyName("message"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Message = null);

public sealed record PermissionDecisionInfo(
    [property: JsonPropertyName("id"), JsonRequired] PermissionId Id,
    [property: JsonPropertyName("effect"), JsonRequired] PermissionEffect Effect
) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => PermissionWire.ValidateId(Id);
}
