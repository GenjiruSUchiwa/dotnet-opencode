namespace OpenCode.Core.Tools.Builtins;

using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/webfetch.ts
/// </summary>
public sealed class WebFetchTool : ITool
{
    private readonly HttpClient _http;

    public string Name => "webfetch";

    public string Description =>
        "Fetch content from an HTTP or HTTPS URL and return it as text, markdown, or HTML. Markdown is the default.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "url": { "type": "string", "description": "The HTTP or HTTPS URL to fetch content from" },
            "format": { "type": "string", "enum": ["text", "markdown", "html"], "description": "The format to return" },
            "timeout": { "type": "number", "description": "Optional timeout in seconds" }
        },
        "required": ["url"]
    }
    """).RootElement;

    public WebFetchTool(HttpClient http)
    {
        _http = http;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var url = input.GetProperty("url").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("opencode");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(ct);
        return new ToolExecutionResult(text);
    }
}
