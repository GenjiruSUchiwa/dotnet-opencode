namespace OpenCode.Server.Endpoints;

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/location", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                directory = cwd,
                project = new
                {
                    id = "prj_local",
                    directory = cwd,
                    canonical = cwd
                }
            });
        });
    }
}
