namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Projects;

using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Locations;
using OpenCode.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class ModelEndpoints
{
    public static void MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/model", (HttpRequest request, IDatabase database, ProviderResolver resolver, CancellationToken ct) =>
            CatalogRequestLocation.ResponseAsync(request, database, resolver, CatalogResource.Models, ct));
        app.MapGet("/api/model/default", (HttpRequest request, IDatabase database, ProviderResolver resolver, CancellationToken ct) =>
            CatalogRequestLocation.ResponseAsync(request, database, resolver, CatalogResource.Default, ct));
    }
}

internal static class CatalogRequestLocation
{
    internal static async Task<IResult> ResponseAsync(HttpRequest request, IDatabase database, ProviderResolver resolver, CatalogResource resource, CancellationToken ct)
    {
        try
        {
            var directory = request.Query["location[directory]"].ToString();
            if (directory.Length == 0)
            {
                directory = request.Headers["x-opencode-directory"].ToString();
                if (directory.Length == 0) directory = Directory.GetCurrentDirectory();
                else
                {
                    try { directory = Uri.UnescapeDataString(directory); }
                    catch (UriFormatException) { }
                }
            }
            var workspace = request.Query["location[workspace]"].ToString();
            if (workspace.Length == 0) workspace = request.Headers["x-opencode-workspace"].ToString();
            var location = await ProjectDiscovery.ResolveAsync(database, directory, workspace, ct);
            var catalog = await resolver.ReadCanonicalCatalogAsync(location.Directory, ct, resource);
            JsonNode? data = resource switch
            {
                CatalogResource.Models => new JsonArray(catalog.Models.Select(model => JsonSerializer.SerializeToNode(model, OpenCodeJsonContext.Default.ModelInfo)).ToArray()),
                CatalogResource.Providers => new JsonArray(catalog.Providers.Select(provider => JsonSerializer.SerializeToNode(provider, OpenCodeJsonContext.Default.ProviderInfo)).ToArray()),
                _ => catalog.DefaultModel is null ? null : JsonSerializer.SerializeToNode(catalog.DefaultModel, OpenCodeJsonContext.Default.ModelInfo)
            };
            var envelope = new JsonObject
            {
                ["location"] = JsonSerializer.SerializeToNode(location, OpenCodeJsonContext.Default.LocationInfo)
            };
            if (resource != CatalogResource.Default || catalog.DefaultModel is not null) envelope["data"] = data;
            return Results.Json(envelope);
        }
        catch (Exception error) when (error is CatalogLocationUnavailableException or CatalogMetadataUnavailableException)
        {
            return Results.Json(new { _tag = "ServiceUnavailableError", message = error.Message, service = "model.catalog" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (JsonException)
        {
            return Results.Json(new { _tag = "ServiceUnavailableError", message = "Catalog metadata does not satisfy the canonical schema.", service = "model.catalog" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
