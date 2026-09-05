namespace OpenCode.Protocol.Groups;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Errors;
using OpenCode.Schema;

/// <summary>Global, stateless generation; not a Session prompt or Session generation.</summary>
public static class GenerateEndpoints
{
    public const string Text = "/api/generate";
    public const string Operation = "generate.text";
    public const string OpenApiOperation = "v2.generate.text";
}

public sealed record GenerateTextResponse(
    [property: JsonPropertyName("data"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<GenerateTextResult>))] GenerateTextResult Data
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Data is null) throw new JsonException("Generation response requires data.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(GenerateTextInput))]
[JsonSerializable(typeof(GenerateTextResult))]
[JsonSerializable(typeof(GenerateTextResponse))]
// Existing tagged errors are shared, not copied into a generation-specific hierarchy.
[JsonSerializable(typeof(SessionQueryError))]
[JsonSerializable(typeof(InvalidRequestError))]
[JsonSerializable(typeof(ServiceUnavailableError))]
public partial class GenerateProtocolJsonContext : JsonSerializerContext;
