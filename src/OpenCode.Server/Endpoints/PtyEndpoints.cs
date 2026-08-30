namespace OpenCode.Server.Endpoints;

using OpenCode.Schema;

public static class PtyEndpoints
{
    public static void MapPtyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pty", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = Array.Empty<PtyInfo>()
            });
        });
    }
}
