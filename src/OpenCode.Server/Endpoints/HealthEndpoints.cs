namespace OpenCode.Server.Endpoints;

using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Protocol;
using OpenCode.Server.Hosting;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (IServerIdentity service, HttpContext context) =>
        {
            var state = service.State;
            if (state is "starting" or "stopping") context.Response.Headers.RetryAfter = "1";
            return Results.Json(new
            {
                healthy = true,
                version = OpenCodeChannel.ServiceVersion,
                buildID = ApplicationBuild.Id,
                pid = Environment.ProcessId,
                id = service.Id,
                application = OpenCodeChannel.Application,
                channel = OpenCodeChannel.Name,
                state
            }, statusCode: state == "ready" ? StatusCodes.Status200OK : state == "failed"
                ? StatusCodes.Status500InternalServerError : StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/api/server", (IServerIdentity service) =>
        {
            return Results.Ok(new ServerInfoResponse(
                Urls: service.Url is null ? [] : [service.Url]
            ));
        });

        app.MapGet("/api/experimental/migration/v1", () =>
        {
            return Results.Problem("Legacy database upgrades are not implemented; only fresh current-schema bootstrap is supported.", statusCode: StatusCodes.Status501NotImplemented);
        });
    }
}
