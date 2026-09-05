namespace OpenCode.Core.WebSearch;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Schema;

/// <summary>Source adapter response contracts only; this is not an HTML scraper or a fallback search service.</summary>
public static class WebSearchBackendResponses
{
    public static IReadOnlyList<WebSearchResult> Parse(string providerId, string body)
    {
        if (providerId == "tavily")
        {
            using var document = JsonDocument.Parse(body);
            return Array(document.RootElement, "results").Select(item => new WebSearchResult(String(item, "url"), new(),
                String(item, "title"), Nonempty(String(item, "content")))).ToArray();
        }
        var result = Mcp(body);
        if (result.TryGetProperty("isError", out var failed) && failed.ValueKind == JsonValueKind.True)
            throw new JsonException("MCP reported a failed search.");
        var content = Array(result, "content").Select(item =>
        {
            if (String(item, "type") != "text") throw new JsonException("Expected source text content.");
            if (providerId == "exa" && item.TryGetProperty("_meta", out _))
            {
                var meta = Object(item, "_meta");
                if (!meta.TryGetProperty("searchTime", out var duration) || duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new JsonException("Invalid Exa search metadata.");
            }
            return String(item, "text");
        }).ToArray();
        if (providerId == "exa") return Exa(content.FirstOrDefault(text => text.Length > 0)
            ?? throw new JsonException("Missing Exa text result."));
        if (providerId == "parallel")
        {
            var response = Object(result, "structuredContent");
            _ = String(response, "search_id");
            _ = String(response, "session_id");
            ValidateParallelExtras(response);
            return Array(response, "results").Select(item =>
            {
                var excerpts = Array(item, "excerpts").Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("Invalid excerpt.")).ToArray();
                return new WebSearchResult(String(item, "url"), new(Published(OptionalString(item, "publish_date"))),
                    Nonempty(OptionalString(item, "title")), excerpts.Length > 0 ? string.Join("\n\n", excerpts) : null);
            }).ToArray();
        }
        if (providerId == "firecrawl")
        {
            using var document = JsonDocument.Parse(content.FirstOrDefault(text => text.Length > 0)
                ?? throw new JsonException("Missing Firecrawl search response."));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new JsonException("Firecrawl did not report success.");
            return Array(Object(document.RootElement, "data"), "web").Select(item => new WebSearchResult(String(item, "url"), new(),
                Nonempty(OptionalString(item, "title")), Nonempty(OptionalString(item, "description")))).ToArray();
        }
        throw new JsonException("Unsupported search response provider.");
    }

    private static void ValidateParallelExtras(JsonElement response)
    {
        if (response.TryGetProperty("warnings", out var warnings) && warnings.ValueKind != JsonValueKind.Null)
        {
            foreach (var warning in Array(response, "warnings"))
            {
                if (String(warning, "type") is not ("spec_validation_warning" or "input_validation_warning" or "warning"))
                    throw new JsonException("Invalid Parallel warning type.");
                _ = String(warning, "message");
                if (warning.TryGetProperty("detail", out var detail) && detail.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                    throw new JsonException("Invalid Parallel warning detail.");
            }
        }
        if (response.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            foreach (var item in Array(response, "usage"))
            {
                _ = String(item, "name");
                if (!item.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetDouble(out var value)
                    || !double.IsFinite(value) || Math.Truncate(value) != value) throw new JsonException("Invalid Parallel usage count.");
            }
        }
    }

    private static JsonElement Mcp(string body)
    {
        var text = body.Trim();
        var payload = text.StartsWith('{') ? text : body.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => line[6..].Trim()).FirstOrDefault(line => line.StartsWith('{'));
        if (payload is null) throw new JsonException("No MCP result in the response.");
        using var document = JsonDocument.Parse(payload);
        return Object(document.RootElement, "result").Clone();
    }

    private static IReadOnlyList<WebSearchResult> Exa(string text)
    {
        var results = text.Split("\n\n---\n\n", StringSplitOptions.None).Select(block =>
        {
            var url = Field(block, "URL");
            if (url is null) return null;
            var title = Field(block, "Title");
            var published = Field(block, "Published");
            var content = Regex.Match(block, @"^(?:Highlights|Text):\s*\n?([\s\S]*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            return new WebSearchResult(url, new(Published(published == "N/A" ? null : published)), title == "N/A" ? null : title, Nonempty(content));
        }).OfType<WebSearchResult>().ToArray();
        // A malformed/unknown textual payload must not masquerade as a successful empty search.
        if (results.Length == 0) throw new JsonException("Exa response contained no source-format URL records.");
        return results;
    }
    private static string? Field(string block, string field) => Nonempty(Regex.Match(block, "^" + field + @":\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim());
    private static string? Nonempty(string? text) => string.IsNullOrEmpty(text) ? null : text;
    private static double? Published(string? value) => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date) ? date.ToUnixTimeMilliseconds() : null;
    private static JsonElement Object(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Object
        ? item : throw new JsonException($"Missing object: {name}.");
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Array
        ? item.EnumerateArray() : throw new JsonException($"Missing array: {name}.");
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
        ? item.GetString()! : throw new JsonException($"Missing string: {name}.");
    private static string? OptionalString(JsonElement value, string name) => !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null
        ? null : item.ValueKind == JsonValueKind.String ? item.GetString() : throw new JsonException($"Invalid string: {name}.");
}
