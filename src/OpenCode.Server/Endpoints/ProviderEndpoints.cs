namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Database;
using OpenCode.Core.Llm;

public static class ProviderEndpointsMapping
{
    public static void MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/provider", (HttpRequest request, IDatabase database, ProviderResolver resolver, CancellationToken ct) =>
            CatalogRequestLocation.ResponseAsync(request, database, resolver, CatalogResource.Providers, ct));
    }
}
