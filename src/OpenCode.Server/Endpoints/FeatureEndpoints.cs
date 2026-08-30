namespace OpenCode.Server.Endpoints;

using OpenCode.Schema;

public static class FeatureEndpoints
{
    public static void MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/skill", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                location = new { directory = cwd, project = new { id = "prj_local", directory = cwd, canonical = cwd } },
                data = Array.Empty<SkillInfo>()
            });
        });

        app.MapGet("/api/command", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                location = new { directory = cwd, project = new { id = "prj_local", directory = cwd, canonical = cwd } },
                data = Array.Empty<CommandInfo>()
            });
        });

        app.MapGet("/api/plugin", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                location = new { directory = cwd, project = new { id = "prj_local", directory = cwd, canonical = cwd } },
                data = Array.Empty<PluginInfo>()
            });
        });

        app.MapGet("/api/reference", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                location = new { directory = cwd, project = new { id = "prj_local", directory = cwd, canonical = cwd } },
                data = Array.Empty<ReferenceInfo>()
            });
        });
    }
}
