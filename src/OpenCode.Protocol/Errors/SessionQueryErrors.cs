namespace OpenCode.Protocol.Errors;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

// Shared declared error variants for session, permission, and catalog HTTP operations.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "_tag")]
[JsonDerivedType(typeof(InvalidRequestError), "InvalidRequestError")]
[JsonDerivedType(typeof(InvalidCursorError), "InvalidCursorError")]
[JsonDerivedType(typeof(SessionNotFoundError), "SessionNotFoundError")]
[JsonDerivedType(typeof(UnauthorizedError), "UnauthorizedError")]
[JsonDerivedType(typeof(UnknownError), "UnknownError")]
[JsonDerivedType(typeof(PermissionNotFoundError), "PermissionNotFoundError")]
[JsonDerivedType(typeof(AgentNotFoundError), "AgentNotFoundError")]
[JsonDerivedType(typeof(ServiceUnavailableError), "ServiceUnavailableError")]
public abstract record SessionQueryError
{
    [JsonPropertyName("message"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string Message { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record InvalidRequestError : SessionQueryError
{
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Kind { get; init; }
    [JsonPropertyName("field"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Field { get; init; }
}

public sealed record InvalidCursorError : SessionQueryError;
public sealed record UnauthorizedError : SessionQueryError;

public sealed record SessionNotFoundError : SessionQueryError
{
    [JsonPropertyName("sessionID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string SessionId { get; init; }
}

public sealed record PermissionNotFoundError : SessionQueryError
{
    [JsonPropertyName("requestID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string RequestId { get; init; }
}

public sealed record AgentNotFoundError : SessionQueryError
{
    [JsonPropertyName("agentID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public required string AgentId { get; init; }
}

public sealed record ServiceUnavailableError : SessionQueryError
{
    [JsonPropertyName("service"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Service { get; init; }
}

public sealed record UnknownError : SessionQueryError
{
    [JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Reference { get; init; }
}

/// <summary>Local validation, not an HTTP response. Error is the corresponding canonical protocol error shape.</summary>
public sealed class SessionQueryValidationException(SessionQueryError error, string field) : ArgumentException(error.Message, field)
{
    public SessionQueryError Error { get; } = error;

    internal static SessionQueryValidationException InvalidRequest(string message, string field) =>
        new(new InvalidRequestError { Message = message, Field = field, Kind = "query" }, field);
}
