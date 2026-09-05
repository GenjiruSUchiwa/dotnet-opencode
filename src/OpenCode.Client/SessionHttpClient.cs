namespace OpenCode.Client;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Protocol;
using OpenCode.Protocol.Errors;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

/// <summary>Network-only session API. No daemon discovery, local database, retries, or execution worker.</summary>
public sealed partial class SessionHttpClient : IDisposable
{
    private readonly ServiceEndpoint _endpoint;
    private readonly Uri _origin;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public SessionHttpClient(ServiceEndpoint endpoint, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var origin)
            || (origin.Scheme != "http" && origin.Scheme != "https") || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("The service endpoint must be an HTTP(S) origin without a path or embedded credentials.", nameof(endpoint));
        _endpoint = endpoint;
        _origin = origin;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<ApiResult<SessionInfo>> CreateAsync(SessionCreateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Id is SessionId id) SessionPath(id);
        if (input.Location is not null) ArgumentNullException.ThrowIfNull(input.Location.Directory);
        return RequestAsync(HttpMethod.Post, "/api/session", SessionProtocolJsonContext.Default.SessionResult, ct,
            JsonContent.Create(input, SessionProtocolJsonContext.Default.SessionCreateInput));
    }

    public Task<ApiResult<SessionInfo>> GetAsync(SessionId sessionId, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, SessionPath(sessionId), SessionProtocolJsonContext.Default.SessionResult, ct);

    public async Task<ApiPage<SessionInfo>> ListAsync(SessionListQuery? query = null, CancellationToken ct = default)
    {
        query ??= new SessionListQuery();
        query.Validate();
        var result = await RequestAsync(HttpMethod.Get, "/api/session" + Query(
            ("workspace", query.Workspace?.Value), ("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)),
            ("order", Order(query.Order)), ("search", query.Search),
            ("parentID", query.RootOnly ? "null" : query.ParentId?.Value), ("directory", query.Directory),
            ("project", query.Project?.Value), ("subpath", query.Subpath), ("cursor", query.Cursor)),
            SessionProtocolJsonContext.Default.SessionsPage, ct).ConfigureAwait(false);
        if (result.Data.Any(item => item is null)) throw Malformed("session.list", "Session page contains a null item.");
        return result;
    }

    public async Task<ApiPage<SessionMessage>> MessagesAsync(SessionId sessionId, SessionMessagesQuery? query = null, CancellationToken ct = default)
    {
        query ??= new SessionMessagesQuery();
        query.Validate();
        var result = await RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/message" + Query(
            ("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)), ("order", Order(query.Order)), ("cursor", query.Cursor)),
            SessionProtocolJsonContext.Default.MessagesPage, ct).ConfigureAwait(false);
        if (result.Data.Any(item => item is null)) throw Malformed("session.messages", "Message page contains a null item.");
        return result;
    }

    public async Task<ApiResult<IReadOnlyList<SessionInboxItem>>> InboxAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/inbox", SessionProtocolJsonContext.Default.InboxResult, ct).ConfigureAwait(false);
        if (result.Data.Any(item => item is null || item.SessionId != sessionId))
            throw Malformed("session.inbox.list", "Inbox response contains a null or cross-session item.");
        return result;
    }

    /// <summary>Cancels an undelivered input; preserves the server's conflict response.</summary>
    public Task CancelInboxAsync(SessionId sessionId, MessageId inboxId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, InboxPath(sessionId, inboxId), ct);

    public Task SteerInboxAsync(SessionId sessionId, MessageId inboxId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, InboxPath(sessionId, inboxId) + "/steer", ct);

    public Task QueueInboxAsync(SessionId sessionId, MessageId inboxId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, InboxPath(sessionId, inboxId) + "/queue", ct);

    private static string InboxPath(SessionId sessionId, MessageId inboxId)
    {
        ArgumentException.ThrowIfNullOrEmpty(inboxId.Value, nameof(inboxId));
        return SessionPath(sessionId) + "/inbox/" + Uri.EscapeDataString(inboxId.Value);
    }

    /// <summary>Admits one prompt. It does not return assistant text and is never automatically retried.</summary>
    public Task<ApiResult<SessionInboxItem>> PromptAsync(SessionId sessionId, PromptInput input, MessageId? id = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null, InboxDeliveryMode? delivery = null, bool? resume = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PromptAsync(sessionId, new SessionPromptInput(input.Text, id, input.Files, input.Agents, input.Skills, metadata, delivery, resume), ct);
    }

    /// <summary>Admits one prompt. It does not return assistant text and is never automatically retried.</summary>
    public async Task<ApiResult<SessionInboxItem>> PromptAsync(SessionId sessionId, SessionPromptInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Text);
        if (input.Id is MessageId id && (!id.IsInitialized() || !id.Value.StartsWith("msg_", StringComparison.Ordinal)))
            throw new ArgumentException("Prompt ID must be a canonical msg_ identifier.", nameof(input));
        var result = await RequestAsync(HttpMethod.Post, SessionPath(sessionId) + "/prompt", SessionProtocolJsonContext.Default.PromptResult, ct,
            JsonContent.Create(input, SessionProtocolJsonContext.Default.SessionPromptInput)).ConfigureAwait(false);
        if (result.Data.Payload is not UserInboxPayload || result.Data.SessionId != sessionId
            || (input.Id is MessageId requested && result.Data.Id != requested))
            throw Malformed("session.prompt", "Admission response does not match the submitted user input identity.");
        return result;
    }

    public Task<InterruptSessionResponse> InterruptAsync(SessionId sessionId, bool? continueExecution = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, SessionPath(sessionId) + "/interrupt" + Query(
            ("continue", continueExecution is null ? null : continueExecution.Value ? "true" : "false")),
            SessionProtocolJsonContext.Default.InterruptSessionResponse, ct);

    /// <summary>Process-owned foreground drains, not a projection-derived or queued-session approximation.</summary>
    public async Task<ApiResult<IReadOnlyDictionary<string, SessionActive>>> ActiveAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/session/active",
            SessionProtocolJsonContext.Default.ActiveSessionsResult, ct).ConfigureAwait(false);
        foreach (var entry in result.Data)
        {
            if (entry.Value is null) throw Malformed("session.active", "Active map contains a null state.");
            try { _ = SessionId.FromExisting(entry.Key); }
            catch (ArgumentException error) { throw Malformed("session.active", "Active map contains an invalid session identifier.", error); }
        }
        return result;
    }

    /// <summary>Marks the specific observed idle transition, not the current wall-clock time, as viewed.</summary>
    public Task ViewAsync(SessionId sessionId, double idle, CancellationToken ct = default)
    {
        if (!double.IsFinite(idle) || idle < 0 || Math.Truncate(idle) != idle)
            throw new ArgumentOutOfRangeException(nameof(idle), "Idle must be a nonnegative finite integer.");
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/view", ct,
            JsonContent.Create(new SessionViewInput(idle), SessionProtocolJsonContext.Default.SessionViewInput));
    }

    /// <summary>Replaces the session's shell environment. An empty dictionary is sent as an explicit empty override.</summary>
    public Task SetEnvironmentAsync(SessionId sessionId, IReadOnlyDictionary<string, string> variables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (variables.Any(pair => pair.Key is null || pair.Value is null))
            throw new ArgumentException("Environment variables must contain string keys and values.", nameof(variables));
        return NoContentAsync(HttpMethod.Put, SessionPath(sessionId) + "/environment", ct,
            JsonContent.Create(new SessionEnvironmentInput(variables), SessionProtocolJsonContext.Default.SessionEnvironmentInput));
    }

    /// <summary>An empty title requests upstream automatic title generation; this client does not generate a substitute.</summary>
    public Task RenameAsync(SessionId sessionId, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/rename", ct,
            JsonContent.Create(new SessionRenameInput(title), SessionProtocolJsonContext.Default.SessionRenameInput));
    }

    public Task SwitchAgentAsync(SessionId sessionId, AgentId agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent.Value, nameof(agent));
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/agent", ct,
            JsonContent.Create(new SessionSwitchAgentInput(agent), SessionProtocolJsonContext.Default.SessionSwitchAgentInput));
    }

    public Task SwitchModelAsync(SessionId sessionId, ModelRef model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/model", ct,
            JsonContent.Create(new SessionSwitchModelInput(model), SessionProtocolJsonContext.Default.SessionSwitchModelInput));
    }

    /// <summary>Requests canonical session removal, including child sessions. No local data or service is modified.</summary>
    public Task DeleteAsync(SessionId sessionId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, SessionPath(sessionId), ct);

    public Task WaitAsync(SessionId sessionId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/wait", ct);

    /// <summary>Moves active blocking jobs into background observation. Known idle sessions are unchanged.</summary>
    public Task BackgroundAsync(SessionId sessionId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/background", ct);

    private async Task NoContentAsync(HttpMethod method, string path, CancellationToken ct, HttpContent? content = null)
    {
        using var request = CreateRequest(method, path, "application/json", content);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
        await RequireSuccessAsync(response, path, cancellation.Token, HttpStatusCode.NoContent).ConfigureAwait(false);
    }

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, JsonTypeInfo<T> type, CancellationToken ct,
        HttpContent? content = null, Action<HttpRequestMessage>? configure = null) where T : class
    {
        using var request = CreateRequest(method, path, "application/json", content);
        configure?.Invoke(request);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
        await RequireSuccessAsync(response, path, cancellation.Token).ConfigureAwait(false);
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media is null || (!media.Equals("application/json", StringComparison.OrdinalIgnoreCase) && !media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new SessionProtocolException(SessionProtocolFailure.UnsupportedContentType, path, "Expected a JSON response.");
        var stream = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
        await using var streamLifetime = stream.ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync(stream, type, cancellation.Token).ConfigureAwait(false)
                ?? throw Malformed(path, "Response must not be null.");
        }
        catch (JsonException error) { throw Malformed(path, "Response does not satisfy the canonical wire contract.", error); }
        catch (NotSupportedException error)
        {
            throw new SessionProtocolException(SessionProtocolFailure.UnsupportedSchema, path, "This response requires schema or serialization support not present in the native client.", error);
        }
        catch (ArgumentException error) { throw Malformed(path, "Response contains an invalid schema value.", error); }
        catch (InvalidOperationException error) when (error is not SessionProtocolException)
        {
            throw Malformed(path, "Response contains a value the current schema cannot decode.", error);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string accept, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, new Uri(_origin, path)) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        _endpoint.ApplyAuth(request);
        return request;
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct,
        HttpStatusCode expectedStatusCode = HttpStatusCode.OK)
    {
        if (response.StatusCode == expectedStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonElement? payload = null;
        try { payload = JsonSerializer.Deserialize(body, SessionHttpJsonContext.Default.JsonElement); }
        catch (JsonException) { }
        SessionQueryError? queryError = null;
        if (payload is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("_tag", out var tag)
            && tag.ValueKind == JsonValueKind.String && tag.GetString() is
                "InvalidRequestError" or "InvalidCursorError" or "SessionNotFoundError" or "PermissionNotFoundError" or "AgentNotFoundError"
                or "ServiceUnavailableError" or "UnauthorizedError" or "UnknownError")
        {
            try { queryError = json.Deserialize(SessionProtocolJsonContext.Default.SessionQueryError); }
            catch (JsonException) { }
        }
        throw new SessionApiException(response.StatusCode, operation, body, payload, queryError, expectedStatusCode);
    }

    private static string SessionPath(SessionId id)
    {
        // The Schema constructor owns legacy prefix validation; reject only an uninitialized struct here.
        ArgumentException.ThrowIfNullOrEmpty(id.Value, nameof(id));
        return "/api/session/" + Uri.EscapeDataString(id.Value);
    }

    private static string? Order(SessionOrder? order) => order switch
    {
        null => null,
        SessionOrder.Ascending => "asc",
        SessionOrder.Descending => "desc",
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    private static string Query(params (string Key, string? Value)[] fields)
    {
        var query = string.Join("&", fields.Where(field => field.Value is not null)
            .Select(field => Uri.EscapeDataString(field.Key) + "=" + Uri.EscapeDataString(field.Value!)));
        return query.Length == 0 ? "" : "?" + query;
    }

    private static SessionProtocolException Malformed(string operation, string message, Exception? inner = null) =>
        new(SessionProtocolFailure.MalformedResponse, operation, message, inner);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
        _lifetime.Dispose();
    }
}
