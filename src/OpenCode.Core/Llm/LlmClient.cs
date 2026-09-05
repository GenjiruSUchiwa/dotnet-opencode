namespace OpenCode.Core.Llm;
using Transport;

using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;

public sealed record LlmChatMessage(string Role, string Content);

public interface ILlmClient
{
    string ProviderId { get; }
    // Replay metadata belongs to the protocol namespace, not a configured provider alias.
    string ProviderMetadataKey => ProviderId;
    IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<LlmChatMessage> messages, string modelId,
        object? generationConfig = null, CancellationToken ct = default);
    IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct = default) =>
        throw new LlmException(new LlmFailure.Unsupported("This client does not implement structured streaming."));
}

public sealed class GoogleLlmClient : ILlmClient
{
    private readonly LlmTransport _transport;
    internal bool OmitAuthentication { init => _transport.OmitAuthentication = value; }
    public string ProviderId => "google";
    public string ProviderMetadataKey => _transport.ProviderMetadataKey;

    public GoogleLlmClient(HttpClient http, string apiKey, string? baseUrl = null, string? orgId = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null, JsonObject? body = null, JsonObject? providerOptions = null)
    {
        _transport = new LlmTransport(http, apiKey, baseUrl ?? "https://generativelanguage.googleapis.com/v1beta",
            true, orgId, extraHeaders, body, providerOptions);
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct = default) => _transport.StreamAsync(request, ct);
    public IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<LlmChatMessage> messages, string modelId,
        object? generationConfig = null, CancellationToken ct = default) =>
        LlmAnswerText.StreamAsync(this, messages, modelId, generationConfig, google: true, ct);
}

public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly LlmTransport _transport;
    internal bool OmitAuthentication { init => _transport.OmitAuthentication = value; }
    private readonly JsonObject? _nativeOptions;
    public string ProviderId => "openai";
    public string ProviderMetadataKey => _transport.ProviderMetadataKey;

    public OpenAiLlmClient(HttpClient http, string token, string? baseUrl = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null, JsonObject? body = null, JsonObject? providerOptions = null,
        IReadOnlyDictionary<string, string>? queryParams = null, bool nativeOpenAi = false, string? organization = null, string? project = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (organization is not null) headers["OpenAI-Organization"] = organization;
        if (project is not null) headers["OpenAI-Project"] = project;
        if (extraHeaders is not null) foreach (var (key, value) in extraHeaders) headers[key] = value;
        _transport = new LlmTransport(http, token, baseUrl ?? "https://api.openai.com/v1", false, null,
            headers, body, nativeOpenAi ? null : providerOptions, queryParams);
        _nativeOptions = nativeOpenAi ? providerOptions?.DeepClone().AsObject() ?? new JsonObject() : null;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (_nativeOptions is null) return _transport.StreamAsync(request, ct);
        var options = ResponsesRequestLowering.Defaults(request.ModelId);
        ConfigLoader.MergeOverlay(options, _nativeOptions);
        ConfigLoader.MergeOverlay(options, JsonSerializer.SerializeToNode(request.ProviderOptions)!.AsObject());
        LlmHttp.RequireFields(options, ResponsesRequestLowering.OptionNames);
        // Native Chat consumes only these OpenAI facade options; this is never used
        // to substitute Chat for a Responses package.
        var lowered = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        if (options["reasoningEffort"] is { } effort) lowered["reasoningEffort"] = JsonSerializer.SerializeToElement(effort);
        if (request.Compatibility.SupportsStore != false && options["store"] is { } store) lowered["store"] = JsonSerializer.SerializeToElement(store);
        return _transport.StreamAsync(request with { ProviderOptions = lowered.ToImmutable() }, ct);
    }
    public IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<LlmChatMessage> messages, string modelId,
        object? generationConfig = null, CancellationToken ct = default) =>
        LlmAnswerText.StreamAsync(this, messages, modelId, generationConfig, google: false, ct);
}

/// <summary>Compatibility adapter: answer text only. Reasoning/usage stay on StreamAsync; tools and opaque output fail explicitly.</summary>
internal static class LlmAnswerText
{
    internal static async IAsyncEnumerable<string> StreamAsync(ILlmClient client, IReadOnlyList<LlmChatMessage> messages,
        string modelId, object? generationConfig, bool google, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var leading = messages.TakeWhile(message => message.Role == "system").ToArray();
        var body = generationConfig is null ? new JsonObject() : LlmHttp.JsonObject(generationConfig);
        if (google && generationConfig is not null) body = new JsonObject { ["generationConfig"] = body };
        var request = new LlmRequest(modelId, messages.Skip(leading.Length).Select(message => new LlmMessage(message.Role switch
        {
            "user" => LlmRole.User, "assistant" => LlmRole.Assistant, "system" => LlmRole.System,
            _ => throw new LlmException(new LlmFailure.Unsupported("The legacy text API cannot express tool messages."))
        }, [new LlmContent.Text(message.Content)])).ToImmutableArray())
        {
            System = leading.Select(message => new LlmSystemPart(message.Content)).ToImmutableArray(),
            Http = new LlmHttpOptions { Body = body.ToImmutableDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)) }
        };
        var fragments = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        await foreach (var item in client.StreamAsync(request, ct).ConfigureAwait(true))
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case LlmEvent.TextDelta text:
                    if (!fragments.TryGetValue(text.Id, out var fragment)) fragments[text.Id] = fragment = new StringBuilder();
                    fragment.Append(text.Text);
                    yield return text.Text;
                    break;
                case LlmEvent.ToolInputStart or LlmEvent.ToolInputDelta or LlmEvent.ToolInputEnd or LlmEvent.ToolInputError
                    or LlmEvent.ToolCall or LlmEvent.ToolResult or LlmEvent.ToolError or LlmEvent.ProviderState:
                    throw new LlmException(new LlmFailure.Unsupported("This response requires the structured LLM API."));
                case LlmEvent.ProviderError error: throw new LlmException(error.Reason);
                case LlmEvent.Finish finish when finish.Reason.Normalized != LlmFinishReason.Stop:
                    throw new LlmException(new LlmFailure.InvalidProviderOutput("The answer-text adapter requires a normal stop outcome."));
                case LlmEvent.TextEnd end when end.Text is not null:
                    var prior = fragments.GetValueOrDefault(end.Id)?.ToString() ?? "";
                    if (!end.Text.StartsWith(prior, StringComparison.Ordinal))
                        throw new LlmException(new LlmFailure.Unsupported("Authoritative text retraction requires the structured LLM API."));
                    if (end.Text.Length > prior.Length) yield return end.Text[prior.Length..];
                    fragments[end.Id] = new StringBuilder(end.Text);
                    break;
            }
        }
        ct.ThrowIfCancellationRequested();
    }
}

internal sealed class LlmTransport
{
    internal bool OmitAuthentication { get; set; }
    internal string ProviderMetadataKey => _google ? "google" : "openai";
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _baseUrl;
    private readonly bool _google;
    private readonly string? _orgId;
    private readonly ImmutableDictionary<string, string> _headers;
    private readonly JsonObject _body;
    private readonly JsonObject _options;
    private readonly ImmutableDictionary<string, string> _query;

    internal LlmTransport(HttpClient http, string key, string baseUrl, bool google, string? orgId,
        IReadOnlyDictionary<string, string>? headers, JsonObject? body, JsonObject? options, IReadOnlyDictionary<string, string>? query = null)
    {
        _http = http;
        _key = key;
        _baseUrl = baseUrl;
        _google = google;
        _orgId = orgId;
        _headers = headers?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) ?? ImmutableDictionary<string, string>.Empty;
        _body = body?.DeepClone().AsObject() ?? new();
        _options = options?.DeepClone().AsObject() ?? new();
        _query = query?.ToImmutableDictionary(StringComparer.Ordinal) ?? ImmutableDictionary<string, string>.Empty;
    }

    internal IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct) => LlmHttp.Guard(StreamCoreAsync(request, ct), ct);

    private async IAsyncEnumerable<LlmEvent> StreamCoreAsync(LlmRequest input, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var options = _options.DeepClone().AsObject();
        ConfigLoader.MergeOverlay(options, JsonSerializer.SerializeToNode(input.ProviderOptions)!.AsObject());
        var body = LlmRequestLowering.Body(input, _google, options, ProviderMetadataKey);
        ConfigLoader.MergeOverlay(body, _body);
        ConfigLoader.MergeOverlay(body, JsonSerializer.SerializeToNode(input.Http.Body)!.AsObject());
        if (!_google && (body["model"]?.GetValue<string>() != input.ModelId || body["stream"]?.GetValue<bool>() != true))
            throw new LlmException(new LlmFailure.InvalidRequest("Body overrides must preserve the exact model ID and streaming mode."));
        var url = _baseUrl.TrimEnd('/') + (_google ? $"/models/{input.ModelId}:streamGenerateContent?alt=sse" : "/chat/completions");
        if (input.Http.Query.Count > 0 || _query.Count > 0)
        {
            var endpoint = new UriBuilder(url);
            var query = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Split('=', 2)).ToDictionary(value => Uri.UnescapeDataString(value[0]),
                    value => value.Length == 2 ? Uri.UnescapeDataString(value[1]) : "", StringComparer.Ordinal);
            foreach (var (key, value) in _query) query[key] = value;
            foreach (var (key, value) in input.Http.Query) query[key] = value;
            if (_google && query.GetValueOrDefault("alt") != "sse")
                throw new LlmException(new LlmFailure.InvalidRequest("Google structured streaming requires alt=sse."));
            endpoint.Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
            url = endpoint.Uri.ToString();
        }
        var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in input.Http.Headers) headers[key] = value;
        using var request = LlmHttp.Request(url, body, headers);
        if (_google)
        {
            if (_orgId is not null) { request.Headers.Remove("x-org-id"); request.Headers.Add("x-org-id", _orgId); }
            if (!OmitAuthentication)
            {
                request.Headers.Remove("x-goog-api-key");
                request.Headers.Add("x-goog-api-key", _key);
            }
        }
        else if (!OmitAuthentication) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _key);
        using var response = await LlmHttp.SendAsync(_http, request, ct).ConfigureAwait(false);
        LlmStreamParser parser = _google ? new GoogleStreamParser(ProviderMetadataKey) : new ChatStreamParser(input.Compatibility, ProviderMetadataKey);
        await foreach (var frame in LlmHttp.Frames(response, ct).ConfigureAwait(false))
        {
            if (frame == "[DONE]") { if (!_google) break; continue; }
            foreach (var item in LlmHttp.Decode(frame, response, parser))
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }
        ct.ThrowIfCancellationRequested();
        foreach (var item in LlmHttp.Complete(response, parser))
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}

internal static class LlmHttp
{
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or IOException && error is not LlmException)
        { throw TransportFailure(error, LlmTransportOperation.Request, request.RequestUri?.ToString()); }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        { throw TransportFailure(error, LlmTransportOperation.Request, request.RequestUri?.ToString()); }
    }

    private static LlmException TransportFailure(Exception error, LlmTransportOperation operation, string? url,
        LlmHttpContext? context = null) => new(new LlmFailure.Transport(error.Message)
        {
            Operation = operation,
            Url = url,
            Http = context,
            Code = error is OperationCanceledException ? "Timeout" : null
            // HTTP completion does not establish provider acceptance. Leave delivery/recovery unspecified.
        }, error);

    internal static JsonObject JsonObject(object value) => JsonSerializer.SerializeToNode(value) as JsonObject
        ?? throw new LlmException(new LlmFailure.InvalidRequest("LLM options must be a JSON object."));

    internal static void RequireFields(JsonObject value, params string[] fields)
    {
        if (value.Any(pair => !fields.Contains(pair.Key, StringComparer.Ordinal)))
            throw new LlmException(new LlmFailure.Unsupported("The request contains unsupported provider options."));
    }

    internal static void RequireType(JsonObject value, string field, params JsonValueKind[] kinds)
    {
        if (value.TryGetPropertyValue(field, out var node) && (node is null || !kinds.Contains(node.GetValueKind())))
            throw new LlmException(new LlmFailure.InvalidRequest("A provider option has an invalid JSON type."));
    }

    internal static HttpRequestMessage Request(string url, JsonObject body, IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        try
        {
            foreach (var (name, value) in headers)
            {
                if (value is null || name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                    throw new LlmException(new LlmFailure.InvalidRequest("HTTP headers must be strings without newlines."));
                if (request.Headers.Any(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) request.Headers.Remove(name);
                if (request.Content.Headers.Any(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) request.Content.Headers.Remove(name);
                if (!request.Headers.TryAddWithoutValidation(name, value) && !request.Content.Headers.TryAddWithoutValidation(name, value))
                    throw new LlmException(new LlmFailure.InvalidRequest("Invalid configured HTTP header."));
            }
            ProviderUserAgent.Apply(request);
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    internal static IReadOnlyList<LlmEvent> Decode(string frame, HttpResponseMessage response, LlmStreamParser parser)
    {
        try
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected provider event object.");
            if (!parser.HandlesProviderErrors && root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new LlmException(ProviderFailure.Classify("Provider reported an error.", body: frame));
            return parser.Step(root);
        }
        catch (LlmException error)
        { throw new LlmException(error.Reason with { Body = error.Reason.Body ?? frame, Http = error.Reason.Http ?? LlmHttpContext.From(response) }, error.InnerException); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new LlmException(new LlmFailure.InvalidProviderOutput("Provider sent an invalid event.") { Body = frame, Http = LlmHttpContext.From(response) }, error); }
    }

    internal static IReadOnlyList<LlmEvent> Complete(HttpResponseMessage response, LlmStreamParser parser)
    {
        try { return parser.Complete(); }
        catch (LlmException error)
        { throw new LlmException(error.Reason with { Http = error.Reason.Http ?? LlmHttpContext.From(response) }, error.InnerException); }
    }

    internal static async IAsyncEnumerable<LlmEvent> Guard(IAsyncEnumerable<LlmEvent> source, [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        await using var enumeratorLifetime = enumerator.ConfigureAwait(true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool next;
            try { next = await enumerator.MoveNextAsync().ConfigureAwait(true); }
            catch (HttpRequestException error) { throw new LlmException(new LlmFailure.Transport("Provider HTTP transport failed."), error); }
            catch (IOException error) when (error is not LlmException) { throw new LlmException(new LlmFailure.Transport("Provider transport failed."), error); }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            { throw new LlmException(new LlmFailure.InvalidRequest("Invalid structured LLM request."), error); }
            ct.ThrowIfCancellationRequested();
            if (!next) yield break;
            yield return enumerator.Current;
        }
    }

    internal static async IAsyncEnumerable<string> Frames(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct,
        IReadOnlySet<string>? allowedEvents = null, bool parseErrorEvents = false,
        Func<LlmHttpContext, string, LlmFailure>? classifyHttpError = null)
    {
        var frames = ReadFrames(response, ct, allowedEvents, parseErrorEvents, classifyHttpError).GetAsyncEnumerator(ct);
        await using var framesLifetime = frames.ConfigureAwait(true);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool next;
            try { next = await frames.MoveNextAsync().ConfigureAwait(true); }
            catch (Exception error) when (error is HttpRequestException or IOException && error is not LlmException)
            { throw TransportFailure(error, LlmTransportOperation.Read, response.RequestMessage?.RequestUri?.ToString(), LlmHttpContext.From(response)); }
            catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
            { throw TransportFailure(error, LlmTransportOperation.Read, response.RequestMessage?.RequestUri?.ToString(), LlmHttpContext.From(response)); }
            ct.ThrowIfCancellationRequested();
            if (!next) yield break;
            yield return frames.Current;
        }
    }

    private static async IAsyncEnumerable<string> ReadFrames(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct,
        IReadOnlySet<string>? allowedEvents, bool parseErrorEvents,
        Func<LlmHttpContext, string, LlmFailure>? classifyHttpError)
    {
        ct.ThrowIfCancellationRequested();
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(true);
            var context = LlmHttpContext.From(response);
            if (classifyHttpError is not null) throw new LlmException(classifyHttpError(context, body) with { Body = body, Http = context });
            throw new LlmException(ProviderFailure.Classify($"Provider request failed with HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode, body, http: context));
        }
        if (response.Content.Headers.ContentType?.MediaType is { } mediaType && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new LlmException(new LlmFailure.InvalidProviderOutput("Expected an SSE response.")
            { Body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(true), Http = LlmHttpContext.From(response) });
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(true);
        await using var streamLifetime = stream.ConfigureAwait(true);
        var lines = PipelineText.DecodedLinesAsync(stream, new UTF8Encoding(false, true),
            cancellationToken: ct).GetAsyncEnumerator(ct);
        await using var linesLifetime = lines.ConfigureAwait(true);
        var data = new List<string>();
        string? eventName = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string? line;
            try { line = await lines.MoveNextAsync().ConfigureAwait(true) ? lines.Current : null; }
            catch (DecoderFallbackException error)
            {
                throw new LlmException(new LlmFailure.InvalidProviderOutput("Provider response is not valid UTF-8.")
                { Http = LlmHttpContext.From(response) }, error);
            }
            ct.ThrowIfCancellationRequested();
            if (line is null)
            {
                if (data.Count > 0 || eventName == "error")
                    throw new LlmException(new LlmFailure.InvalidProviderOutput("SSE ended before the event boundary.", true)
                    { Body = data.Count == 0 ? "" : string.Join('\n', data) + "\n", Http = LlmHttpContext.From(response) });
                yield break;
            }
            if (line.Length == 0)
            {
                var frame = string.Join('\n', data);
                data.Clear();
                if (eventName == "error" && !parseErrorEvents) throw new LlmException(ProviderFailure.Classify(
                    "Provider sent an SSE error event.", body: frame, http: LlmHttpContext.From(response)));
                var accepted = allowedEvents is null || allowedEvents.Contains(eventName ?? "message");
                eventName = null;
                if (accepted && frame.Length > 0) yield return frame;
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var offset = colon < 0 ? line.Length : colon + 1;
            if (offset < line.Length && line[offset] == ' ') offset++;
            var field = line.AsSpan(0, colon < 0 ? line.Length : colon);
            if (field.SequenceEqual("data")) data.Add(line[offset..]);
            else if (field.SequenceEqual("event")) eventName = line[offset..];
        }
    }
}
