namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionForkInput(
    [property: JsonPropertyName("boundary"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ForkRequestBoundary>))] ForkRequestBoundary Boundary) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Boundary is null) throw new System.Text.Json.JsonException("Fork requires boundary.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionForkInput))]
public partial class SessionForkProtocolJsonContext : JsonSerializerContext;
