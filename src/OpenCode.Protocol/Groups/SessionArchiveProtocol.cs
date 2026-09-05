namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionArchiveImportInput(
    [property: JsonPropertyName("info"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionInfo>))] SessionInfo Info,
    [property: JsonPropertyName("messages"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<SessionMessage>))] IReadOnlyList<SessionMessage> Messages,
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<LocationRef>))] LocationRef? Location = null) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Info is null || Messages is null) throw new System.Text.Json.JsonException("Archive requires info and messages.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionArchiveImportInput))]
[JsonSerializable(typeof(ApiResult<SessionTransferData>), TypeInfoPropertyName = "ArchiveResult")]
public partial class SessionArchiveProtocolJsonContext : JsonSerializerContext;
