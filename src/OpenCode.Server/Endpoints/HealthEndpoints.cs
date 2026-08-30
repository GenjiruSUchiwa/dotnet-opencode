namespace OpenCode.Server.Endpoints;

using System.Diagnostics;
using OpenCode.Protocol.Groups;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () =>
        {
            var pid = Process.GetCurrentProcess().Id;
            return Results.Ok(new ServiceHealthResponse(
                Healthy: true,
                Version: "10.0.0-opencode-dotnet",
                Pid: pid
            ));
        });

        app.MapGet("/api/server", () =>
        {
            return Results.Ok(new ServerInfoResponse(
                Urls: ["http://127.0.0.1:5055"]
            ));
        });

        app.MapGet("/api/experimental/migration/v1", () =>
        {
            return Results.Ok(new { status = "completed" });
        });
    }
}
