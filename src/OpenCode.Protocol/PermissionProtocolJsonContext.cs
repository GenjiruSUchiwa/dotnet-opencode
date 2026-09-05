namespace OpenCode.Protocol;

using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Protocol.Serialization;
using OpenCode.Schema;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowOutOfOrderMetadataProperties = true,
    Converters = new[] { typeof(PermissionRequestWireConverter), typeof(PermissionLocationWireConverter) })]
[JsonSerializable(typeof(PermissionCreateInput))]
[JsonSerializable(typeof(PermissionReplyInput))]
[JsonSerializable(typeof(PermissionRequest))]
[JsonSerializable(typeof(ApiResult<PermissionDecisionInfo>), TypeInfoPropertyName = "DecisionResult")]
[JsonSerializable(typeof(ApiResult<PermissionRequest>), TypeInfoPropertyName = "RequestResult")]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<PermissionRequest>>), TypeInfoPropertyName = "SessionRequestsResult")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<PermissionRequest>>), TypeInfoPropertyName = "LocationRequestsResult")]
[JsonSerializable(typeof(PermissionSavedListResponse))]
public partial class PermissionProtocolJsonContext : JsonSerializerContext;
