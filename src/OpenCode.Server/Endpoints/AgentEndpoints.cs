namespace OpenCode.Server.Endpoints;

using OpenCode.Schema;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agent", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var agents = new[]
            {
                AgentInfo.CreateDefault(new AgentId("build")),
                new AgentInfo(
                    Id: new AgentId("general"),
                    Name: "General",
                    Mode: AgentMode.Subagent,
                    Hidden: false,
                    Description: "General-purpose agent for researching complex questions and executing multi-step tasks.",
                    Permissions: [new PermissionRule("*", "*", PermissionEffect.Allow)]
                ),
                new AgentInfo(
                    Id: new AgentId("explore"),
                    Name: "Explore",
                    Mode: AgentMode.Subagent,
                    Hidden: false,
                    Description: "Fast agent specialized for exploring codebases.",
                    Permissions: [new PermissionRule("*", "*", PermissionEffect.Allow)]
                )
            };

            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = agents
            });
        });
    }
}
