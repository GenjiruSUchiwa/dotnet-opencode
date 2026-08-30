namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record WebSearchResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("content")] string? Content = null
);

/// <summary>
/// 1:1 port of WebSearch.Response from packages/schema/src/websearch.ts
/// </summary>
public sealed record WebSearchResponse(
    [property: JsonPropertyName("providerID")] string ProviderId,
    [property: JsonPropertyName("results")] IReadOnlyList<WebSearchResult> Results
);
