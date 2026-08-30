namespace OpenCode.Server.Endpoints;

using OpenCode.Schema;

public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/permission/request", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = Array.Empty<PermissionRequest>()
            });
        });

        app.MapGet("/api/permission/saved", () =>
        {
            return Results.Ok(new
            {
                data = Array.Empty<PermissionSavedInfo>()
            });
        });
    }
}
