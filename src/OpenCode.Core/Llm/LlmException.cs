namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Net;

public sealed record LlmHttpContext(string? Url, HttpStatusCode Status, ImmutableDictionary<string, string> Headers)
{
    internal static LlmHttpContext From(HttpResponseMessage response) => new(
        response.RequestMessage?.RequestUri?.ToString(), response.StatusCode,
        response.Headers.Concat(response.Content.Headers).ToImmutableDictionary(
            header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase));
}

public enum LlmFailureClassification { ContextOverflow, PayloadTooLarge }
public enum LlmTransportType { Http, WebSocket }
public enum LlmTransportOperation { Request, Read, Write }
public enum LlmTransportDelivery { NotSent, Rejected, Ambiguous, Accepted }
public enum LlmTransportRecovery { RetryConnect, RetryFull, RotateAndRetryFull, FallbackHttp, Fail }

public abstract record LlmFailure(string Message)
{
    public string? Body { get; init; }
    public LlmHttpContext? Http { get; init; }
    public sealed record InvalidRequest(string Detail) : LlmFailure(Detail)
    {
        public LlmFailureClassification? Classification { get; init; }
    }
    public sealed record Unsupported(string Detail) : LlmFailure(Detail);
    public sealed record InvalidProviderOutput(string Detail, bool IncompleteStream = false) : LlmFailure(Detail);
    public sealed record Authentication(string Detail) : LlmFailure(Detail);
    public sealed record RateLimit(string Detail) : LlmFailure(Detail);
    public sealed record QuotaExceeded(string Detail) : LlmFailure(Detail);
    public sealed record ContentPolicy(string Detail) : LlmFailure(Detail);
    public sealed record ProviderInternal(string Detail) : LlmFailure(Detail);
    public sealed record Provider(string Detail) : LlmFailure(Detail);
    public sealed record Transport(string Detail) : LlmFailure(Detail)
    {
        public LlmTransportType TransportType { get; init; } = LlmTransportType.Http;
        public LlmTransportOperation? Operation { get; init; }
        public string? Url { get; init; }
        public string? Code { get; init; }
        public LlmTransportDelivery? Delivery { get; init; }
        public LlmTransportRecovery? Recovery { get; init; }
    }
}

public class LlmException(LlmFailure reason, Exception? innerException = null) : IOException(reason.Message, innerException)
{
    public LlmFailure Reason { get; } = reason;
}

// Retained for existing consumers. Structured callers can inspect the base Reason.
public sealed class LlmProviderException(string message, HttpStatusCode statusCode, string? responseBody = null,
    Exception? innerException = null) : LlmException(
        new LlmFailure.InvalidProviderOutput(message) { Body = responseBody }, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
}
