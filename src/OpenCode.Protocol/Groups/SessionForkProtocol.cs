namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionForkInput(
    [property: JsonPropertyName("boundary"), JsonRequired] ForkRequestBoundary Boundary);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionForkInput))]
public partial class SessionForkProtocolJsonContext : JsonSerializerContext;
