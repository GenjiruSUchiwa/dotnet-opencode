namespace OpenCode.Protocol;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

// Base permits null data but not an absent data property. Location's own optional
// workspace annotation still omits workspaceID, independently of this context.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocationResponse<VcsBase?>), TypeInfoPropertyName = "BaseResult")]
[JsonSerializable(typeof(VcsDiffMode))]
public partial class VcsProtocolJsonContext : JsonSerializerContext;
