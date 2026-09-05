namespace OpenCode.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(FormCreatePayload))]
[JsonSerializable(typeof(FormReply))]
[JsonSerializable(typeof(ApiResult<FormInfo>), TypeInfoPropertyName = "FormResult")]
[JsonSerializable(typeof(ApiResult<FormState>), TypeInfoPropertyName = "StateResult")]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<FormInfo>>), TypeInfoPropertyName = "FormsResult")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<FormInfo>>), TypeInfoPropertyName = "LocationFormsResult")]
public partial class FormProtocolJsonContext : JsonSerializerContext;
