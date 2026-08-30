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
        CancellationToken ct = default);
}

public sealed class GoogleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public string ProviderId => "google";

    public GoogleLlmClient(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<LlmChatMessage> messages,
        string modelId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Strip provider prefix if present (e.g. "google/gemini-2.5-flash" -> "gemini-2.5-flash")
        var normalizedModel = modelId.StartsWith("google/", StringComparison.OrdinalIgnoreCase)
            ? modelId["google/".Length..]
            : modelId;

        // Default to a current fast Gemini model if not specified or alias
        if (normalizedModel is "default" or "gemini-flash-latest" or "")
        {
            normalizedModel = "gemini-2.5-flash";
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{normalizedModel}:streamGenerateContent?alt=sse&key={_apiKey}";

        var contents = messages.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToArray();

        var bodyJson = JsonSerializer.Serialize(new { contents });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

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
                        var text = parts[0].GetProperty("text").GetString();
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
