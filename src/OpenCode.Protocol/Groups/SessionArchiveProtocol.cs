namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionArchiveImportInput(
    [property: JsonPropertyName("info"), JsonRequired] SessionInfo Info,
    [property: JsonPropertyName("messages"), JsonRequired] IReadOnlyList<SessionMessage> Messages,
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LocationRef? Location = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionArchiveImportInput))]
[JsonSerializable(typeof(ApiResult<SessionTransferData>), TypeInfoPropertyName = "ArchiveResult")]
public partial class SessionArchiveProtocolJsonContext : JsonSerializerContext;
