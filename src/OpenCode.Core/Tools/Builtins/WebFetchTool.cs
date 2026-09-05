namespace OpenCode.Core.Tools.Builtins;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

public sealed record WebFetchOutput(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("output")] string Output);

/// <summary>Current source webfetch is text-only: binary images and PDFs are explicitly unsupported.</summary>
public sealed class WebFetchTool
{
    public const string Name = "webfetch";
    private const string UserAgent = "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; OpenCode-User/1.0; +https://opencode.ai";
    private readonly WebFetchTransport _http;
    private readonly IToolPermission _permission;
    private readonly TimeProvider _clock;

    public WebFetchTool(WebFetchTransport http, IToolPermission permission, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _permission = permission ?? throw new ArgumentNullException(nameof(permission));
    }

    public ToolInfo Create() => ToolInfo.FromJson(Name,
        "Fetch content from an HTTP or HTTPS URL and return it as text, markdown, or HTML. Markdown is the default.\n\nUse a more targeted tool when one is available. This tool is read-only. Large text results may be replaced with a preview while the complete output is retained in managed storage.",
        JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"url":{"type":"string"},"format":{"type":"string","enum":["text","markdown","html"],"default":"markdown"},
              "timeout":{"type":"number","exclusiveMinimum":0,"maximum":120}},"required":["url"]}
            """), ExecuteAsync, BuiltinToolSchemas.WebFetch, new ToolOptions(CodeMode: false));

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var args = new ToolInput(input);
        var rawUrl = args.String("url");
        var url = WebFetchTransport.Parse(rawUrl);
        var format = args.OptionalString("format") ?? "markdown";
        if (format is not ("text" or "markdown" or "html")) throw new ToolExecutionException("format must be text, markdown, or html.");
        var timeout = 30d;
        if (input.TryGetProperty("timeout", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out timeout) || !double.IsFinite(timeout) || timeout <= 0 || timeout > 120)
                throw new ToolExecutionException("timeout must be a finite number greater than 0 and at most 120 seconds.");
        }
        var metadata = new Dictionary<string, object> { ["url"] = rawUrl, ["format"] = format };
        if (input.TryGetProperty("timeout", out _)) metadata["timeout"] = timeout;
        try { await _permission.AssertAsync(Name, [rawUrl], ["*"], context, metadata, ct).ConfigureAwait(true); }
        catch (PermissionBlockedException denial) { throw new ToolExecutionException(denial.Detail, denial); }
        catch (PermissionCorrectedException correction) { throw new ToolExecutionException(correction.Feedback, correction); }

        byte[] body;
        string contentType;
        using (var lifetime = _clock.CreateLinkedCancellationTokenSource(ct))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(timeout));
            try
            {
                var accept = format switch
                {
                    "markdown" => "text/markdown;q=1.0, text/x-markdown;q=0.9, text/plain;q=0.8, text/html;q=0.7, */*;q=0.1",
                    "text" => "text/plain;q=1.0, text/markdown;q=0.9, text/html;q=0.8, */*;q=0.1",
                    _ => "text/html;q=1.0, application/xhtml+xml;q=0.9, text/plain;q=0.8, text/markdown;q=0.7, */*;q=0.1"
                };
                var response = await _http.GetAsync(url, accept, UserAgent, lifetime.Token).ConfigureAwait(true);
                if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Contains("challenge", StringComparer.Ordinal))
                {
                    response.Dispose();
                    response = await _http.GetAsync(url, accept, "opencode", lifetime.Token).ConfigureAwait(true);
                }
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("cf-mitigated", out var challenge) && challenge.Contains("challenge", StringComparer.Ordinal))
                        throw new ToolExecutionException("Unable to fetch URL: protection challenge remains. Browser or credential-based challenge solving is not supported.");
                    if (!response.IsSuccessStatusCode) throw new ToolExecutionException($"Unable to fetch URL: HTTP {(int)response.StatusCode}.");
                    contentType = response.Content.Headers.TryGetValues("Content-Type", out var types) ? string.Join(", ", types) : "";
                    var mime = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
                    if (mime.StartsWith("image/", StringComparison.Ordinal) && mime is not ("image/svg+xml" or "image/vnd.fastbidsheet"))
                        throw new ToolExecutionException($"Unsupported fetched image content type: {mime}");
                    if (!(mime.Length == 0 || mime.StartsWith("text/", StringComparison.Ordinal) || mime is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript" ||
                        mime.EndsWith("+json", StringComparison.Ordinal) || mime.EndsWith("+xml", StringComparison.Ordinal)))
                        throw new ToolExecutionException($"Unsupported fetched file content type: {mime}");
                    body = await HttpBody.CollectAsync(response, HtmlMarkdown.MaximumBytes, lifetime.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
            { throw new ToolExecutionException("Request timed out"); }
            catch (HttpRequestException) { throw new ToolExecutionException("Unable to fetch URL: HTTP transport failed."); }
            catch (IOException) { throw new ToolExecutionException("Unable to fetch URL: response stream failed."); }
        }
        ct.ThrowIfCancellationRequested();
        var content = Encoding.UTF8.GetString(body);
        if (content.StartsWith('\uFEFF')) content = content[1..];
        var output = contentType.Contains("text/html", StringComparison.Ordinal) && format != "html"
            ? await HtmlMarkdown.ConvertAsync(content, format == "text", ct).ConfigureAwait(true) : content;
        ct.ThrowIfCancellationRequested();
        var result = new WebFetchOutput(rawUrl, contentType, format, output);
        return new(output, result, new Dictionary<string, object> { ["contentType"] = contentType });
    }
}
