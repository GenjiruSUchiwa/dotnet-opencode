namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;

public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string? _authToken;
    private readonly string _baseUrl;
    private readonly ImmutableDictionary<string, string> _headers;
    private readonly JsonObject _body;
    private readonly JsonObject _options;
    public string ProviderId { get; }
    public string ProviderMetadataKey => "anthropic";

    public AnthropicLlmClient(HttpClient http, string? apiKey = null, string? authToken = null, string? baseUrl = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null, JsonObject? body = null, JsonObject? providerOptions = null,
        string providerId = "anthropic")
    {
        if (apiKey is not null && authToken is not null)
            throw new LlmException(new LlmFailure.InvalidRequest("Anthropic apiKey and authToken cannot be combined."));
        _http = http;
        _apiKey = apiKey;
        _authToken = authToken;
        _baseUrl = baseUrl ?? "https://api.anthropic.com/v1";
        _headers = extraHeaders?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) ?? ImmutableDictionary<string, string>.Empty;
        _body = body?.DeepClone().AsObject() ?? new();
        _options = providerOptions?.DeepClone().AsObject() ?? new();
        ProviderId = providerId;
    }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct = default) => LlmHttp.Guard(StreamCoreAsync(request, ct), ct);
    public IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<LlmChatMessage> messages, string modelId,
        object? generationConfig = null, CancellationToken ct = default) =>
        LlmAnswerText.StreamAsync(this, messages, modelId, generationConfig, google: false, ct);

    private async IAsyncEnumerable<LlmEvent> StreamCoreAsync(LlmRequest input, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var token = _authToken ?? _apiKey;
        if (string.IsNullOrEmpty(token) || token.Contains('\r') || token.Contains('\n'))
            throw new LlmException(new LlmFailure.Authentication("Anthropic requires a usable API key or auth token."));
        var options = _options.DeepClone().AsObject();
        ConfigLoader.MergeOverlay(options, JsonSerializer.SerializeToNode(input.ProviderOptions)!.AsObject());
        var body = AnthropicRequestLowering.Body(input, options);
        ConfigLoader.MergeOverlay(body, _body);
        ConfigLoader.MergeOverlay(body, JsonSerializer.SerializeToNode(input.Http.Body)!.AsObject());
        if (body["model"]?.GetValue<string>() != input.ModelId || body["stream"]?.GetValue<bool>() != true)
            throw new LlmException(new LlmFailure.InvalidRequest("Body overrides must preserve the exact API model ID and streaming mode."));
        var endpoint = new UriBuilder(_baseUrl.TrimEnd('/') + "/messages");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        // This is the source route's explicit provider identity condition, not protocol inference.
        if (ProviderId == "anthropic") query["beta"] = "true";
        foreach (var (key, value) in input.Http.Query) query[key] = value;
        endpoint.Query = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["anthropic-version"] = "2023-06-01" };
        foreach (var (key, value) in _headers) headers[key] = value;
        foreach (var (key, value) in input.Http.Headers) headers[key] = value;
        using var request = LlmHttp.Request(endpoint.Uri.ToString(), body, headers);
        if (_authToken is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        else
        {
            request.Headers.Remove("x-api-key");
            request.Headers.Add("x-api-key", token);
        }
        using var response = await LlmHttp.SendAsync(_http, request, ct);
        var parser = new AnthropicStreamParser();
        await foreach (var frame in LlmHttp.Frames(response, ct, AnthropicStreamParser.EventNames, parseErrorEvents: true))
            foreach (var item in LlmHttp.Decode(frame, response, parser))
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        ct.ThrowIfCancellationRequested();
        foreach (var item in LlmHttp.Complete(response, parser)) yield return item;
    }
}
