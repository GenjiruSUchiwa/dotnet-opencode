namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionMoveInput(
    [property: JsonPropertyName("directory"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Directory,
    [property: JsonPropertyName("workspaceID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<WorkspaceId>))] WorkspaceId? WorkspaceId = null,
    [property: JsonPropertyName("delivery"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<InboxDeliveryMode>))] InboxDeliveryMode? Delivery = null) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Directory is null) throw new System.Text.Json.JsonException("Move requires directory.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionMoveInput))]
public partial class SessionMoveProtocolJsonContext : JsonSerializerContext;
