namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Database;

/// <summary>One HTTP/SSE Responses attempt. ChatGPT binding is created only from a validated channel credential.</summary>
public sealed class OpenAiResponsesLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ImmutableDictionary<string, string> _headers;
    private readonly ImmutableDictionary<string, string> _query;
    private readonly JsonObject _body;
    private readonly JsonObject _options;
    private readonly bool _chatGpt;
    private readonly string? _sessionId;
    public string ProviderId => "openai";
    public string ProviderMetadataKey => "openai";

    public OpenAiResponsesLlmClient(HttpClient http, string apiKey, string? baseUrl = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null, JsonObject? body = null, JsonObject? providerOptions = null,
        string? organization = null, string? project = null, IReadOnlyDictionary<string, string>? queryParams = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = baseUrl ?? "https://api.openai.com/v1";
        var headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (organization is not null) headers["OpenAI-Organization"] = organization;
        if (project is not null) headers["OpenAI-Project"] = project;
        if (extraHeaders is not null) foreach (var (key, value) in extraHeaders) headers[key] = value;
        _headers = headers.ToImmutable();
        _query = queryParams?.ToImmutableDictionary(StringComparer.Ordinal) ?? ImmutableDictionary<string, string>.Empty;
        _body = body?.DeepClone().AsObject() ?? new();
        _options = providerOptions?.DeepClone().AsObject() ?? new();
    }

    internal OpenAiResponsesLlmClient(StoredCredential credential, IReadOnlyDictionary<string, string>? headers,
        JsonObject? body, JsonObject? options, string? sessionId)
        : this(OpenAiOAuthService.PinnedHttp, BackendToken(credential), OpenAiOAuthService.BackendBaseUrl,
            BackendHeaders(credential, headers), body, options)
    {
        if (sessionId is not null && sessionId.Length == 0)
            throw new LlmException(new LlmFailure.InvalidRequest("A supplied session ID must not be empty."));
        _chatGpt = true;
        _sessionId = sessionId;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct = default) => LlmHttp.Guard(StreamCoreAsync(request, ct), ct);
    public IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<LlmChatMessage> messages, string modelId,
        object? generationConfig = null, CancellationToken ct = default) =>
        LlmAnswerText.StreamAsync(this, messages, modelId, generationConfig, google: false, ct);

    private async IAsyncEnumerable<LlmEvent> StreamCoreAsync(LlmRequest input, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(_apiKey) || _apiKey.Contains('\r') || _apiKey.Contains('\n'))
            throw new LlmException(new LlmFailure.Authentication("Responses requires a usable API credential."));
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")
            || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
            throw new LlmException(new LlmFailure.InvalidRequest("Responses baseURL must be an absolute HTTP(S) endpoint without query or fragment."));
        var endpoint = new UriBuilder(_baseUrl.TrimEnd('/') + "/responses");
        if (!_chatGpt && endpoint.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) && endpoint.Path.StartsWith("/backend-api/", StringComparison.Ordinal))
            throw new LlmException(new LlmFailure.Unsupported("The ChatGPT backend requires a channel OAuth-bound client, not an API-key adapter."));
        var options = ResponsesRequestLowering.Defaults(input.ModelId);
        ConfigLoader.MergeOverlay(options, _options);
        ConfigLoader.MergeOverlay(options, JsonSerializer.SerializeToNode(input.ProviderOptions)!.AsObject());
        var body = ResponsesRequestLowering.Body(input, options);
        ConfigLoader.MergeOverlay(body, _body);
        ConfigLoader.MergeOverlay(body, JsonSerializer.SerializeToNode(input.Http.Body)!.AsObject());
        if (body["model"]?.GetValue<string>() != input.ModelId || body["stream"]?.GetValue<bool>() != true)
            throw new LlmException(new LlmFailure.InvalidRequest("Responses body overrides must preserve the exact model ID and streaming mode."));
        if (_chatGpt)
        {
            if (!OpenAiOAuthService.Eligible(input.ModelId, body))
                throw new LlmException(new LlmFailure.InvalidRequest("The exact model/request is excluded by the source ChatGPT eligibility policy; no substitute was selected."));
            if (body["store"]?.GetValue<bool>() != false)
                throw new LlmException(new LlmFailure.Unsupported("The ChatGPT HTTP adapter supports stateless store:false requests only."));
            if (body["instructions"] is not JsonValue instruction || !instruction.TryGetValue<string>(out _))
                throw new LlmException(new LlmFailure.InvalidRequest("ChatGPT backend requests require explicit instruction text."));
        }
        if (body["previous_response_id"] is not null || body["conversation"] is not null)
            throw new LlmException(new LlmFailure.Unsupported("Stateful Responses continuation is not implemented; supply complete structured history."));
        if (body["tools"] is { } tools && (tools is not JsonArray entries || entries.Any(tool => tool?["type"]?.GetValue<string>() != "function")))
            throw new LlmException(new LlmFailure.Unsupported("Provider-hosted Responses tool declarations are not implemented."));
        if (body["input"] is not JsonArray inputs) throw new LlmException(new LlmFailure.InvalidRequest("Responses input must be an array."));
        if (inputs.Any(item => item?["type"]?.GetValue<string>() is { } type && type is not ("message" or "reasoning" or "function_call" or "function_call_output")))
            throw new LlmException(new LlmFailure.Unsupported("Unsupported Responses replay item type."));
        var query = new Dictionary<string, string>(_query, StringComparer.Ordinal);
        if (_chatGpt && input.Http.Query.Count > 0)
            throw new LlmException(new LlmFailure.Unsupported("ChatGPT backend query overrides are not implemented."));
        foreach (var (key, value) in input.Http.Query) query[key] = value;
        endpoint.Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in input.Http.Headers) headers[key] = value;
        if (_chatGpt)
        {
            if (headers.ContainsKey("Host")) throw new LlmException(new LlmFailure.InvalidRequest("ChatGPT backend Host overrides are not allowed."));
            headers["originator"] = "opencode";
            if (_sessionId is not null) headers["session-id"] = _sessionId;
        }
        using var request = LlmHttp.Request(endpoint.Uri.ToString(), body, headers);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await LlmHttp.SendAsync(_http, request, ct);
        var parser = new ResponsesStreamParser();
        await foreach (var frame in LlmHttp.Frames(response, ct, parseErrorEvents: true, classifyHttpError: ResponsesStreamParser.HttpFailure))
        {
            if (frame == "[DONE]") continue;
            foreach (var item in LlmHttp.Decode(frame, response, parser))
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
            if (parser.IsTerminal) break;
        }
        ct.ThrowIfCancellationRequested();
        foreach (var item in LlmHttp.Complete(response, parser)) yield return item;
    }

    private static string BackendToken(StoredCredential credential)
    {
        if (!OpenAiOAuthService.IsChatGptCredential(credential))
            throw new LlmException(new LlmFailure.Authentication("A supported channel OpenAI OAuth credential is required for backend routing."));
        return credential.Value.GetProperty("access").GetString()!;
    }

    private static IReadOnlyDictionary<string, string> BackendHeaders(StoredCredential credential, IReadOnlyDictionary<string, string>? overlay)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["User-Agent"] = OpenAiOAuthService.DefaultUserAgent, ["originator"] = "opencode" };
        if (OpenAiOAuthService.AccountId(credential) is { } account) result["chatgpt-account-id"] = account;
        if (overlay is not null) foreach (var (key, value) in overlay) result[key] = value;
        return result;
    }
}
