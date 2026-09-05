namespace OpenCode.Client;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Errors;
using OpenCode.Schema;

/// <summary>A canonical envelope view; Raw and Data retain fields not modeled by this client.</summary>
public sealed record ServerEventEnvelope(
    EventId Id, string Type, double? Created, LocationRef? Location, DurableEnvelope? Durable, JsonElement Raw)
{
    public JsonElement Data => Raw.GetProperty("data");
    public JsonElement? Metadata => Raw.TryGetProperty("metadata", out var metadata) ? metadata : null;
}

// Protocol's connected frame is not Schema.Event.Payload: created is absent.
internal sealed record ConnectedEventFrame(
    [property: JsonPropertyName("id"), JsonRequired] EventId Id,
    [property: JsonPropertyName("type"), JsonRequired] string Type,
    [property: JsonPropertyName("data"), JsonRequired] JsonElement Data,
    [property: JsonPropertyName("location"), JsonConverter(typeof(EventLocationJsonConverter))] LocationRef? Location = null,
    [property: JsonPropertyName("metadata"), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Metadata = null
) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Type != "server.connected" || Data.ValueKind != JsonValueKind.Object)
            throw new JsonException("Connected frame requires server.connected and object data.");
    }
}

public sealed class SessionApiException(
    HttpStatusCode statusCode, string operation, string responseBody, JsonElement? payload, SessionQueryError? queryError = null,
    HttpStatusCode? expectedStatusCode = null)
    : HttpRequestException($"{operation} returned HTTP {(int)statusCode}." +
        (expectedStatusCode is null ? "" : $" Expected HTTP {(int)expectedStatusCode.Value}."), null, statusCode)
{
    public string Operation { get; } = operation;
    public string ResponseBody { get; } = responseBody;
    public JsonElement? Payload { get; } = payload;
    public SessionQueryError? QueryError { get; } = queryError;
    public HttpStatusCode? ExpectedStatusCode { get; } = expectedStatusCode;
}

public enum SessionProtocolFailure { UnsupportedContentType, MalformedResponse, UnsupportedSchema, EventTooLarge }

public sealed class SessionProtocolException(SessionProtocolFailure code, string operation, string message, Exception? inner = null)
    : InvalidOperationException($"{operation}: {message}", inner)
{
    public SessionProtocolFailure Code { get; } = code;
    public string Operation { get; } = operation;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ConnectedEventFrame))]
internal partial class SessionHttpJsonContext : JsonSerializerContext;
