namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record WebSearchProvider(
    [property: JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Id,
    [property: JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Name);

public sealed record WebSearchInput(
    [property: JsonPropertyName("query"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Query,
    [property: JsonPropertyName("providerID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? ProviderId = null);

public sealed record WebSearchResultTime(
    [property: JsonPropertyName("published"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFiniteNumberJsonConverter))] double? Published = null);

public sealed record WebSearchResult(
    [property: JsonPropertyName("url"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Url,
    [property: JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<WebSearchResultTime>))] WebSearchResultTime Time,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Title = null,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Content = null
)
{
    public void Validate()
    {
        if (Url is null || Time is null || Time.Published is { } time && !double.IsFinite(time))
            throw new JsonException("Web search result requires url, time, and a finite optional published timestamp.");
    }
}

/// <summary>
/// 1:1 port of WebSearch.Response from packages/schema/src/websearch.ts
/// </summary>
public sealed record WebSearchResponse(
    [property: JsonPropertyName("providerID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string ProviderId,
    [property: JsonPropertyName("results"), JsonRequired] IReadOnlyList<WebSearchResult> Results
) : IJsonOnDeserialized, IJsonOnSerializing
{
    public void Validate()
    {
        if (ProviderId is null || Results is null || Results.Any(item => item is null)) throw new JsonException("Invalid web search response.");
        foreach (var item in Results) item.Validate();
    }
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    void IJsonOnSerializing.OnSerializing() => Validate();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WebSearchInput))]
[JsonSerializable(typeof(WebSearchProvider))]
[JsonSerializable(typeof(WebSearchResponse))]
public partial class WebSearchJsonContext : JsonSerializerContext;
