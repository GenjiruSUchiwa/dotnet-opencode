namespace OpenCode.Protocol.Errors;

public class OpenCodeProtocolException : Exception
{
    public int StatusCode { get; }

    public OpenCodeProtocolException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}

public sealed class InvalidRequestException : OpenCodeProtocolException
{
    public string? Field { get; }
    public InvalidRequestException(string message, string? field = null)
        : base(message, 400)
    {
        Field = field;
    }
}

public sealed class UnauthorizedException : OpenCodeProtocolException
{
    public UnauthorizedException(string message = "Unauthorized")
        : base(message, 401) { }
}

public sealed class SessionNotFoundException : OpenCodeProtocolException
{
    public string SessionId { get; }
    public SessionNotFoundException(string sessionId)
        : base($"Session '{sessionId}' not found.", 404)
    {
        SessionId = sessionId;
    }
}

public sealed class ProjectNotFoundException : OpenCodeProtocolException
{
    public string ProjectId { get; }
    public ProjectNotFoundException(string projectId)
        : base($"Project '{projectId}' not found.", 404)
    {
        ProjectId = projectId;
    }
}

public sealed class ProviderNotFoundException : OpenCodeProtocolException
{
    public string ProviderId { get; }
    public ProviderNotFoundException(string providerId)
        : base($"Provider '{providerId}' not found.", 404)
    {
        ProviderId = providerId;
    }
}

public sealed class SessionBusyException : OpenCodeProtocolException
{
    public string SessionId { get; }
    public SessionBusyException(string sessionId)
        : base($"Session '{sessionId}' is currently busy.", 409)
    {
        SessionId = sessionId;
    }
}

public sealed class ConflictException : OpenCodeProtocolException
{
    public ConflictException(string message) : base(message, 409) { }
}

public sealed class ServiceUnavailableException : OpenCodeProtocolException
{
    public ServiceUnavailableException(string message) : base(message, 503) { }
}
