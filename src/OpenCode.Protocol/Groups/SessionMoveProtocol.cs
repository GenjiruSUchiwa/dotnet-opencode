namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionMoveInput(
    [property: JsonPropertyName("directory"), JsonRequired] string Directory,
    [property: JsonPropertyName("workspaceID")] WorkspaceId? WorkspaceId = null,
    [property: JsonPropertyName("delivery")] InboxDeliveryMode? Delivery = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionMoveInput))]
public partial class SessionMoveProtocolJsonContext : JsonSerializerContext;
