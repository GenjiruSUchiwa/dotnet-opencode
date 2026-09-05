namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public async Task<LocationResponse<IReadOnlyList<WebSearchProvider>>> ListWebSearchProvidersAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/websearch/provider" + Query(("location[directory]", directory), ("location[workspace]", workspace)),
            WebSearchHttpJsonContext.Default.ProvidersResponse, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(provider => provider is null || provider.Id is null || provider.Name is null))
            throw Malformed("websearch.providers", "Invalid registered provider catalog.");
        return result;
    }

    /// <summary>One canonical query. Never retries, switches providers, or replaces an error with an empty result.</summary>
    public async Task<LocationResponse<WebSearchResponse>> WebSearchAsync(WebSearchInput input,
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Query);
        var result = await RequestAsync(HttpMethod.Post, "/api/websearch" + Query(("location[directory]", directory), ("location[workspace]", workspace)),
            WebSearchHttpJsonContext.Default.QueryResponse, ct, JsonContent.Create(input, WebSearchJsonContext.Default.WebSearchInput)).ConfigureAwait(false);
        if (result.Data is null || !string.IsNullOrEmpty(input.ProviderId) && input.ProviderId != result.Data.ProviderId)
            throw Malformed("websearch.query", "Response does not match the requested provider.");
        result.Data.Validate();
        return result;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<WebSearchProvider>>), TypeInfoPropertyName = "ProvidersResponse")]
[JsonSerializable(typeof(LocationResponse<WebSearchResponse>), TypeInfoPropertyName = "QueryResponse")]
internal partial class WebSearchHttpJsonContext : JsonSerializerContext;
