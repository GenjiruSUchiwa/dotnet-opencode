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
    Converters = new[] { typeof(PermissionLocationWireConverter) })]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<AgentInfo>>), TypeInfoPropertyName = "AgentsResult")]
[JsonSerializable(typeof(LocationResponse<AgentInfo>), TypeInfoPropertyName = "AgentResult")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<ModelInfo>>), TypeInfoPropertyName = "ModelsResult")]
[JsonSerializable(typeof(DefaultModelResponse))]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<ProviderInfo>>), TypeInfoPropertyName = "ProvidersResult")]
public partial class CatalogProtocolJsonContext : JsonSerializerContext;
