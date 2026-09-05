namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Core.Locations;
using OpenCode.Core.Mcp;
using OpenCode.Core.Tools;
using OpenCode.Schema;

internal sealed record McpServersResponse(LocationInfo Location, IReadOnlyList<McpServer> Data);
internal sealed record McpResourcesResponse(LocationInfo Location, McpResourceCatalog Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpServersResponse))]
[JsonSerializable(typeof(McpResourcesResponse))]
internal partial class McpEndpointJsonContext : JsonSerializerContext;

public static class McpEndpoints
{
    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/mcp");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (McpServerNotFoundException error)
            {
                return Results.Json(new { _tag = "McpServerNotFoundError", server = error.Server, message = error.Message }, statusCode: 404);
            }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
            catch (ArgumentException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(McpEndpoints))
                    .LogError(error, "Could not observe the shared Location MCP runtime");
                return Unavailable("The MCP catalog could not be read from its configured sources.");
            }
        });

        routes.MapGet("", async (HttpRequest request, IDatabase database, ToolLocationFactory factory,
            PermissionLocationMap locations, CancellationToken ct) =>
        {
            var info = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
            await using var location = await factory.AcquireAsync(locations, new(info.Directory, info.WorkspaceId), ct);
            var observation = await location.Mcp.ObserveAsync(InstructionCatalog.ReadMcpConfiguration(info.Directory), ct);
            return Results.Json(new McpServersResponse(location.Location, observation.Servers), McpEndpointJsonContext.Default.McpServersResponse);
        });
        routes.MapGet("/resource", async (HttpRequest request, IDatabase database, ToolLocationFactory factory,
            PermissionLocationMap locations, CancellationToken ct) =>
        {
            var info = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
            await using var location = await factory.AcquireAsync(locations, new(info.Directory, info.WorkspaceId), ct);
            var observation = await location.Mcp.ObserveAsync(InstructionCatalog.ReadMcpConfiguration(info.Directory), ct);
            return Results.Json(new McpResourcesResponse(location.Location, observation.Resources), McpEndpointJsonContext.Default.McpResourcesResponse);
        });

        routes.MapPut("/{server}", (string server, McpAddPayload input, HttpRequest request, IDatabase database,
            ToolLocationFactory factory, PermissionLocationMap locations, CancellationToken ct) =>
            MutateAsync(request, database, factory, locations, runtime => runtime.AddAsync(server, input.Config, ct), ct));
        routes.MapDelete("/{server}", (string server, HttpRequest request, IDatabase database,
            ToolLocationFactory factory, PermissionLocationMap locations, CancellationToken ct) =>
            MutateAsync(request, database, factory, locations, runtime => runtime.RemoveAsync(server, ct), ct));
        routes.MapPost("/{server}/connect", (string server, HttpRequest request, IDatabase database,
            ToolLocationFactory factory, PermissionLocationMap locations, CancellationToken ct) =>
            MutateAsync(request, database, factory, locations, runtime => runtime.ConnectAsync(server, ct), ct));
        routes.MapPost("/{server}/disconnect", (string server, HttpRequest request, IDatabase database,
            ToolLocationFactory factory, PermissionLocationMap locations, CancellationToken ct) =>
            MutateAsync(request, database, factory, locations, runtime => runtime.DisconnectAsync(server, ct), ct));
    }

    private static async Task<IResult> MutateAsync(HttpRequest request, IDatabase database, ToolLocationFactory factory,
        PermissionLocationMap locations, Func<McpRuntime, Task<McpObservation>> mutate, CancellationToken ct)
    {
        var info = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
        await using var location = await factory.AcquireAsync(locations, new(info.Directory, info.WorkspaceId), ct);
        await location.Mcp.ObserveAsync(InstructionCatalog.ReadMcpConfiguration(info.Directory), ct);
        await mutate(location.Mcp);
        // A settled failed/needs_auth connection is an observed status, not an HTTP operation failure.
        return Results.NoContent();
    }

    private static IResult Unavailable(string message) => Results.Json(new
    {
        _tag = "ServiceUnavailableError", service = "mcp", message
    }, statusCode: 503);
}
