namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Core.Locations;
using OpenCode.Core.Tools;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Services;

internal sealed record ReferenceCatalogResponse(LocationInfo Location, IReadOnlyList<ReferenceInfo> Data);
internal sealed record CommandCatalogResponse(LocationInfo Location, IReadOnlyList<CommandInfo> Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReferenceCatalogResponse))]
[JsonSerializable(typeof(CommandCatalogResponse))]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<PluginInfo>>), TypeInfoPropertyName = "PluginResult")]
internal partial class FeatureEndpointJsonContext : JsonSerializerContext;

public static class FeatureEndpoints
{
    public static void MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (NotSupportedException error) { return Unavailable("feature", error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable("location", error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(FeatureEndpoints))
                    .LogError(error, "Could not read the requested feature catalog");
                return Unavailable("feature", "The feature catalog could not be read from its configured sources.");
            }
            catch (ArgumentException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400);
            }
        });

        routes.MapGet("/reference", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await ResolveLocationAsync(request, database, ct);
            var references = await InstructionCatalog.ListReferencesAsync(location.Directory, ct);
            return Results.Json(new ReferenceCatalogResponse(location, references), FeatureEndpointJsonContext.Default.ReferenceCatalogResponse);
        });

        routes.MapGet("/command", async (HttpRequest request, IDatabase database, IServiceProvider services, CancellationToken ct) =>
        {
            var commands = services.GetService<CommandHostService>()
                ?? throw new NotSupportedException("The shared Location command host is not configured.");
            var location = await ResolveLocationAsync(request, database, ct);
            await using var snapshot = await commands.AcquireAsync(new LocationRef(location.Directory, location.WorkspaceId), ct);
            return Results.Json(new CommandCatalogResponse(snapshot.Location, snapshot.Runtime.List()), FeatureEndpointJsonContext.Default.CommandCatalogResponse);
        });
        routes.MapGet("/plugin", async (HttpRequest request, IDatabase database, ToolLocationFactory factory,
            PermissionLocationMap locations, CancellationToken ct) =>
        {
            var location = await ResolveLocationAsync(request, database, ct);
            await using var lease = await factory.AcquireAsync(locations, new(location.Directory, location.WorkspaceId), ct);
            return Results.Json(new LocationResponse<IReadOnlyList<PluginInfo>>(lease.Location, lease.Plugins.List()),
                FeatureEndpointJsonContext.Default.PluginResult);
        });
    }

    internal static Task<LocationInfo> ResolveLocationAsync(HttpRequest request, IDatabase database, CancellationToken ct)
    {
        foreach (var pair in request.Query)
            if (pair.Key != "auth_token" && (pair.Key is not ("location[directory]" or "location[workspace]") || pair.Value.Count != 1))
                throw new RequestArgumentException("Use a single location[directory] and optional location[workspace].", nameof(request));
        return RequestLocation.ResolveAsync(request, database, ct);
    }

    private static IResult Unavailable(string service, string message) => Results.Json(new
    {
        _tag = "ServiceUnavailableError", service, message
    }, statusCode: 503);
}
