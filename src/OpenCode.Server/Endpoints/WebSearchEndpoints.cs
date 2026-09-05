namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.WebSearch;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public static class WebSearchEndpoints
{
    /// <summary>Mount only these two source routes. Host binds IWebSearchLocationSource to its real plugin/Location leases.</summary>
    public static void MapWebSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/websearch");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (WebSearchException error)
            {
                return error.Failure switch
                {
                    WebSearchFailure.ProviderRequired => Invalid("Web search provider is required", "websearch_provider_required", "providerID"),
                    WebSearchFailure.ProviderNotFound => Invalid($"Web search provider not found: {error.ProviderId}", "websearch_provider_not_found", "providerID"),
                    WebSearchFailure.Disabled => Invalid("Web search is disabled", "websearch_disabled"),
                    _ => Unavailable(error.ProviderId ?? "websearch", error.Failure == WebSearchFailure.Request ? $"Web search request failed: {error.ProviderId}" : error.Message)
                };
            }
            catch (NotSupportedException) { return Unavailable("websearch", "The Location web search host is not available."); }
            catch (Exception error) when (error is JsonException or ArgumentException or BadHttpRequestException)
            { return Invalid("Invalid web search request", "websearch_request"); }
        });
        group.MapGet("/provider", async (HttpRequest request, IDatabase database, IServiceProvider services, CancellationToken ct) =>
        {
            await using var lease = await AcquireAsync(request, database, services, ct);
            var providers = lease.Runtime.Providers();
            if (providers.Count == 0) throw new WebSearchException(WebSearchFailure.Unavailable, "No web search backends are registered for this Location.");
            return Results.Json(new LocationResponse<IReadOnlyList<WebSearchProvider>>(lease.Location, providers), WebSearchEndpointJsonContext.Default.ProvidersResponse);
        });
        group.MapPost("", async (HttpRequest request, IDatabase database, IServiceProvider services, CancellationToken ct) =>
        {
            var input = await request.ReadFromJsonAsync(WebSearchJsonContext.Default.WebSearchInput, ct)
                ?? throw new RequestArgumentException("Web search payload is required.", nameof(request));
            await using var lease = await AcquireAsync(request, database, services, ct);
            var result = await lease.Runtime.QueryAsync(input, ct);
            return Results.Json(new LocationResponse<WebSearchResponse>(lease.Location, result), WebSearchEndpointJsonContext.Default.QueryResponse);
        });
    }

    private static async ValueTask<WebSearchLocationLease> AcquireAsync(HttpRequest request, IDatabase database, IServiceProvider services, CancellationToken ct)
    {
        var source = services.GetService<IWebSearchLocationSource>()
            ?? throw new WebSearchException(WebSearchFailure.Unavailable, "Web search is not composed by this host.");
        var location = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
        using var readiness = database.Clock.CreateLinkedCancellationTokenSource(ct);
        readiness.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await source.AcquireAsync(new(location.Directory, location.WorkspaceId), readiness.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && readiness.IsCancellationRequested)
        { throw new WebSearchException(WebSearchFailure.Unavailable, "Web search provider initialization timed out"); }
    }

    private static IResult Invalid(string message, string kind, string? field = null) => Results.Json(
        new WebSearchRequestError("InvalidRequestError", message, kind, field), WebSearchEndpointJsonContext.Default.WebSearchRequestError, statusCode: 400);
    private static IResult Unavailable(string service, string message) => Results.Json(
        new WebSearchUnavailableError("ServiceUnavailableError", message, service), WebSearchEndpointJsonContext.Default.WebSearchUnavailableError, statusCode: 503);
}

internal sealed record WebSearchRequestError([property: JsonPropertyName("_tag")] string Tag, string Message, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field);
internal sealed record WebSearchUnavailableError([property: JsonPropertyName("_tag")] string Tag, string Message, string Service);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<WebSearchProvider>>), TypeInfoPropertyName = "ProvidersResponse")]
[JsonSerializable(typeof(LocationResponse<WebSearchResponse>), TypeInfoPropertyName = "QueryResponse")]
[JsonSerializable(typeof(WebSearchRequestError))]
[JsonSerializable(typeof(WebSearchUnavailableError))]
internal partial class WebSearchEndpointJsonContext : JsonSerializerContext;
