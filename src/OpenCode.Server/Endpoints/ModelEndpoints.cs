namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Llm;
using OpenCode.Schema;

public static class ModelEndpoints
{
    public static void MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/model", async (ProviderResolver resolver, CancellationToken ct) =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var resolved = await resolver.ResolveAsync("gemini-flash", "high", ct);

            var models = new[]
            {
                new ModelInfo(
                    Id: "gemini-flash",
                    ModelId: "gemini-flash",
                    ProviderId: "console-google",
                    Name: "gemini-flash (Gemini 3.7 Flash)",
                    Family: "gemini-flash",
                    Enabled: true
                ),
                new ModelInfo(
                    Id: "gemini-2.5-flash",
                    ModelId: "gemini-2.5-flash",
                    ProviderId: "google",
                    Name: "Gemini 2.5 Flash",
                    Family: "gemini-flash",
                    Enabled: true
                ),
                new ModelInfo(
                    Id: "gpt-4o",
                    ModelId: "gpt-4o",
                    ProviderId: "openai",
                    Name: "GPT-4o",
                    Family: "gpt",
                    Enabled: true
                )
            };

            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = models
            });
        });

        app.MapGet("/api/model/default", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var defaultModel = new ModelInfo(
                Id: "gemini-flash",
                ModelId: "gemini-flash",
                ProviderId: "console-google",
                Name: "gemini-flash (Gemini 3.7 Flash)",
                Family: "gemini-flash",
                Enabled: true
            );

            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = defaultModel
            });
        });
    }
}
