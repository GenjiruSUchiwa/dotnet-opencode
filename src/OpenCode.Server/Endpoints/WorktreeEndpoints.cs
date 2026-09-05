namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenCode.Core.Worktrees;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public static class WorktreeEndpoints
{
    public static void MapWorktreeEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/worktree/{projectID}");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (WorktreeException error)
            {
                return Results.Json(new WorktreeErrorResponse("WorktreeError", new(error.Message, error.ForceRequired)),
                    WorktreeProtocolJsonContext.Default.WorktreeErrorResponse, statusCode: 400);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400); }
        });
        routes.MapGet("", async (string projectID, WorktreeService worktrees, CancellationToken ct) =>
            Results.Json(await worktrees.ListAsync(ProjectId.FromExisting(projectID), ct), WorktreeProtocolJsonContext.Default.WorktreeList));
        routes.MapPost("", async (string projectID, WorktreeCreatePayload input, WorktreeService worktrees, CancellationToken ct) =>
            Results.Json(await worktrees.CreateAsync(new WorktreeCreateInput(ProjectId.FromExisting(projectID), input.Strategy, input.Directory,
                input.From, input.Branch, input.Name), ct), WorktreeProtocolJsonContext.Default.WorktreeInfo));
        routes.MapDelete("", async (string projectID, [FromBody] WorktreeRemovePayload input, WorktreeService worktrees, CancellationToken ct) =>
        {
            await worktrees.RemoveAsync(new WorktreeRemoveInput(ProjectId.FromExisting(projectID), input.Directory, input.Force), ct);
            return Results.NoContent();
        });
        routes.MapPost("/refresh", async (string projectID, WorktreeService worktrees, CancellationToken ct) =>
        {
            await worktrees.RefreshAsync(ProjectId.FromExisting(projectID), ct);
            return Results.NoContent();
        });
    }
}
