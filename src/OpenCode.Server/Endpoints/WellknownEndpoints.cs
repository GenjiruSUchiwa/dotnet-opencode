namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCode.Core.Database;
using OpenCode.Core.Integrations.Wellknown;
using OpenCode.Core.Locations;
using OpenCode.Schema;

public static class WellknownEndpoints
{
    /// <summary>Uses the host's existing selected database. Construction performs no discovery or storage I/O.</summary>
    public static IServiceCollection AddWellknownDiscovery(this IServiceCollection services)
    {
        services.TryAddSingleton<WellknownSourceStore>();
        services.TryAddSingleton<WellknownTransport>();
        services.TryAddSingleton<WellknownService>();
        services.TryAddSingleton<WellknownConfigSources>();
        return services;
    }

    /// <summary>Replace the old stub before mounting. Reload may observe compatible contributions, never execute auth.command.</summary>
    public static void MapWellknownEndpoints(this IEndpointRouteBuilder app,
        Func<LocationInfo, CancellationToken, Task>? reload = null)
    {
        app.MapPost("/api/experimental/integration/wellknown", async (HttpRequest request, IDatabase database,
            WellknownService wellknown, CancellationToken ct) =>
        {
            try
            {
                var location = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
                var input = await request.ReadFromJsonAsync(OpenCodeJsonContext.Default.IntegrationWellknownAddPayload, ct)
                    ?? throw new WellknownDiscoveryException("A wellknown URL payload is required.");
                await wellknown.AddAsync(input.Url, ct);
                if (reload is not null) await reload(location, ct);
                return Results.NoContent();
            }
            catch (CatalogLocationUnavailableException error)
            {
                return Results.Json(new { _tag = "ServiceUnavailableError", service = "location", message = error.Message }, statusCode: 503);
            }
            catch (Exception error) when (error is WellknownDiscoveryException or JsonException or ArgumentException or BadHttpRequestException or IOException or SqliteException)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = "Wellknown discovery or source persistence failed.", kind = "well_known_discovery" }, statusCode: 400);
            }
        });
    }
}
