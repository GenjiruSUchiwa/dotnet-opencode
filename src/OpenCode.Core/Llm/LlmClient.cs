namespace OpenCode.Core.Llm;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

public sealed record LlmChatMessage(string Role, string Content);

public interface ILlmClient
{
    string ProviderId { get; }
    IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<LlmChatMessage> messages,
        string modelId,
        object? generationConfig = null,
        CancellationToken ct = default);
}

public sealed class GoogleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string? _baseUrl;
    private readonly string? _orgId;
    private readonly IReadOnlyDictionary<string, string>? _extraHeaders;

    public string ProviderId => "google";

    public GoogleLlmClient(
        HttpClient http,
        string apiKey,
        string? baseUrl = null,
        string? orgId = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _orgId = orgId;
        _extraHeaders = extraHeaders;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<LlmChatMessage> messages,
        string modelId,
        object? generationConfig = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedModel = modelId.StartsWith("google/", StringComparison.OrdinalIgnoreCase)
            ? modelId["google/".Length..]
            : modelId;

        if (normalizedModel.StartsWith("console-google/", StringComparison.OrdinalIgnoreCase))
        {
            normalizedModel = normalizedModel["console-google/".Length..];
        }

        if (normalizedModel is "default" or "gemini-flash-latest" or "")
        {
            normalizedModel = "gemini-2.5-flash";
        }

        // Determine request URL based on whether this is a custom gateway or Google direct
        string url;
        if (!string.IsNullOrEmpty(_baseUrl))
        {
            url = $"{_baseUrl.TrimEnd('/')}/models/{normalizedModel}:streamGenerateContent?alt=sse";
        }
        else
        {
            url = $"https://generativelanguage.googleapis.com/v1beta/models/{normalizedModel}:streamGenerateContent?alt=sse&key={_apiKey}";
        }

        var contents = messages.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToArray();

        object bodyPayload;
        if (generationConfig is not null)
        {
            bodyPayload = new { contents, generationConfig };
        }
        else
        {
            bodyPayload = new { contents };
        }

        var bodyJson = JsonSerializer.Serialize(bodyPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        // Always identify as opencode to avoid Cloudflare bot blocking
        request.Headers.UserAgent.ParseAdd("opencode");

        if (!string.IsNullOrEmpty(_baseUrl))
        {
            request.Headers.Add("x-goog-api-key", _apiKey);
        }

        if (!string.IsNullOrEmpty(_orgId))
        {
            request.Headers.Add("x-org-id", _orgId);
        }

        if (_extraHeaders is not null)
        {
            foreach (var (k, v) in _extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                var json = line["data: ".Length..];
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                    candidates.GetArrayLength() > 0)
                {
                    var firstCandidate = candidates[0];
                    if (firstCandidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.GetArrayLength() > 0)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var textProp))
                            {
                                var text = textProp.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    yield return text;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _baseUrl;

    public string ProviderId => "openai";

    public OpenAiLlmClient(HttpClient http, string token, string? baseUrl = null)
    {
        _http = http;
        _token = token;
        _baseUrl = baseUrl ?? "https://api.openai.com/v1";
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<LlmChatMessage> messages,
        string modelId,
        object? generationConfig = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedModel = modelId.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            ? modelId["openai/".Length..]
            : modelId;

        if (normalizedModel is "default" or "")
        {
            normalizedModel = "gpt-4o";
        }

        var url = $"{_baseUrl.TrimEnd('/')}/chat/completions";

        var payload = new
        {
            model = normalizedModel,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true
        };

        var bodyJson = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("opencode");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: "))
            {
                var payloadStr = line["data: ".Length..].Trim();
                if (payloadStr == "[DONE]") break;

                using var doc = JsonDocument.Parse(payloadStr);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var contentProp))
                    {
                        var text = contentProp.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return text;
                        }
                    }
                }
            }
        }
    }
}
