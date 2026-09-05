namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Agent;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Schema;

internal sealed record AgentCatalogResponse(LocationInfo Location, IReadOnlyList<AgentInfo> Data);
internal sealed record AgentResponse(LocationInfo Location, AgentInfo Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentCatalogResponse))]
[JsonSerializable(typeof(AgentResponse))]
internal partial class AgentEndpointJsonContext : JsonSerializerContext;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/agent");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AgentEndpoints))
                    .LogError(error, "Could not read the configured agent catalog");
                return Unavailable("The agent catalog could not be read from its configured sources.");
            }
            catch (ArgumentException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400);
            }
        });

        routes.MapGet("", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
            var agents = await AgentCatalog.ListAsync(location.Directory, ct);
            return Results.Json(new AgentCatalogResponse(location, agents), AgentEndpointJsonContext.Default.AgentCatalogResponse);
        });
        routes.MapGet("/{agentID}", async (string agentID, HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
            var agent = await AgentCatalog.ResolveAsync(location.Directory, AgentId.FromExisting(agentID), ct);
            return agent is null
                ? Results.Json(new { _tag = "AgentNotFoundError", agentID, message = $"Agent not found: {agentID}" }, statusCode: 404)
                : Results.Json(new AgentResponse(location, agent), AgentEndpointJsonContext.Default.AgentResponse);
        });
    }

    private static IResult Unavailable(string message) => Results.Json(new
    {
        _tag = "ServiceUnavailableError", service = "agent", message
    }, statusCode: 503);
}
