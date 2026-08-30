namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Config;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", () =>
        {
            var config = ConfigLoader.LoadConfig();
            var configPath = Path.Combine(ConfigLoader.GetDefaultConfigDirectory(), "opencode.json");

            return Results.Ok(new object[]
            {
                new
                {
                    type = "document",
                    path = configPath,
                    info = config
                }
            });
        });
    }
}
