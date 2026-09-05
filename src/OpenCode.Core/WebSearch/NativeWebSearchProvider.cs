namespace OpenCode.Core.WebSearch;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Integrations;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Actual source HTTP/MCP backends. Endpoints and key destinations cannot be overridden by model input.</summary>
public sealed class NativeWebSearchProvider : IWebSearchProvider, IDisposable, IAsyncDisposable
{
    public const int MaximumResponseBytes = 256 * 1024;
    private readonly CredentialStore _credentials;
    private readonly Func<string, string?> _environment;
    private readonly HttpClient _http;
    private readonly string _environmentName;
    private readonly string _endpoint;
    private readonly string? _tool;
    private readonly string _userAgent;
    public WebSearchProvider Info { get; }
    /// <summary>Compose into the existing integration catalog; this does not register or persist anything itself.</summary>
    public IntegrationDefinition Integration => new(new(IntegrationId.FromExisting(Info.Id), Info.Name),
        [new IntegrationKeyMethod(), new IntegrationEnvMethod([_environmentName])],
        new Dictionary<string, Func<FormAnswer?, string?, CancellationToken, Task<IntegrationAuthorization>>>());

    public NativeWebSearchProvider(string providerId, CredentialStore credentials, string userAgent, Func<string, string?>? environment = null)
    {
        (Info, _endpoint, _environmentName, _tool) = providerId switch
        {
            "exa" => (new WebSearchProvider("exa", "Exa"), "https://mcp.exa.ai/mcp", "EXA_API_KEY", "web_search_exa"),
            "parallel" => (new WebSearchProvider("parallel", "Parallel"), "https://search.parallel.ai/mcp", "PARALLEL_API_KEY", "web_search"),
            "firecrawl" => (new WebSearchProvider("firecrawl", "Firecrawl"), "https://mcp.firecrawl.dev/v2/mcp", "FIRECRAWL_API_KEY", "firecrawl_search"),
            "tavily" => (new WebSearchProvider("tavily", "Tavily"), "https://api.tavily.com/search", "TAVILY_API_KEY", null),
            _ => throw new WebSearchException(WebSearchFailure.Unavailable, $"Unsupported native web search backend: {providerId}", providerId)
        };
        _credentials = credentials;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _userAgent = userAgent;
        // No shared service/Console Authorization headers, cookies, automatic redirects, or credential refresh.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task<string?> KeyAsync(CancellationToken ct)
    {
        try
        {
            var saved = await _credentials.GetActiveCredentialAsync(Info.Id, ct);
            if (saved is null) return _environment(_environmentName) is { Length: > 0 } key ? key : null;
            if (saved.IntegrationId != Info.Id) throw Unavailable();
            // Source saved-connection precedence: an unsupported selected credential does not fall through to another identity.
            var value = saved.Value.Deserialize(OpenCodeJsonContext.Default.CredentialValue);
            return value is CredentialKey { Key.Length: > 0 } credential ? credential.Key : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        { throw new WebSearchException(WebSearchFailure.Unavailable, $"The {Info.Name} credential could not be resolved from this channel.", Info.Id); }
    }

    public async Task<bool> AvailableAsync(CancellationToken ct) => await KeyAsync(ct) is not null;

    public async Task<IReadOnlyList<WebSearchResult>> ExecuteAsync(string query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var key = await KeyAsync(ct) ?? throw Unavailable();
        using var lifetime = _credentials.Clock.CreateLinkedCancellationTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var url = Info.Id == "exa" ? _endpoint + "?exaApiKey=" + Uri.EscapeDataString(key) : _endpoint;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Accept.ParseAdd(_tool is null ? "application/json" : "application/json, text/event-stream");
            if (Info.Id != "exa")
            {
                request.Headers.UserAgent.ParseAdd(_userAgent);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            if (Info.Id == "tavily") request.Headers.Add("X-Client-Name", "opencode2");
            request.Content = new ByteArrayContent(RequestBody(query));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (!response.IsSuccessStatusCode)
                throw new WebSearchException(WebSearchFailure.Request, $"Web search request failed: {Info.Id}", Info.Id, (int)response.StatusCode);
            var body = Encoding.UTF8.GetString(await HttpBody.CollectAsync(response, MaximumResponseBytes, lifetime.Token));
            return WebSearchBackendResponses.Parse(Info.Id, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
        { throw new WebSearchException(WebSearchFailure.Request, $"{Info.Name} web search request timed out", Info.Id); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or FormatException or ToolExecutionException)
        {
            // In particular, HttpRequestException can contain Exa's credential-bearing URL. Do not retain it as an inner exception.
            throw new WebSearchException(WebSearchFailure.Request, $"Invalid or unavailable web search response: {Info.Id}", Info.Id);
        }
    }

    private byte[] RequestBody(string query)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (_tool is not null)
            {
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", 1);
                writer.WriteString("method", "tools/call");
                writer.WriteStartObject("params");
                writer.WriteString("name", _tool);
                writer.WriteStartObject("arguments");
            }
            if (Info.Id == "parallel")
            {
                writer.WriteString("objective", query);
                writer.WriteStartArray("search_queries"); writer.WriteStringValue(query); writer.WriteEndArray();
            }
            else writer.WriteString("query", query);
            if (Info.Id == "exa") writer.WriteNumber("numResults", 8);
            if (Info.Id == "firecrawl") writer.WriteNumber("limit", 8);
            if (Info.Id == "tavily")
            {
                writer.WriteString("search_depth", "basic");
                writer.WriteNumber("chunks_per_source", 3);
                writer.WriteNumber("max_results", 8);
            }
            if (_tool is not null) { writer.WriteEndObject(); writer.WriteEndObject(); }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private WebSearchException Unavailable() => new(WebSearchFailure.Unavailable, $"A {Info.Name} API key is required for this configured backend.", Info.Id);
    public void Dispose() => _http.Dispose();
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
