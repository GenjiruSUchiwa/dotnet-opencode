namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Database;
using OpenCode.Core.Session;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Services;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/session", async (
            SessionStore store,
            int? limit,
            CancellationToken ct) =>
        {
            var sessions = await store.ListSessionsAsync(limit ?? 50, ct);
            return Results.Ok(sessions);
        });

        app.MapPost("/api/session", async (
            CreateSessionRequest request,
            SessionStore store,
            IEventFeedService feedService,
            CancellationToken ct) =>
        {
            var directory = request.Directory ?? Directory.GetCurrentDirectory();
            var session = await store.CreateSessionAsync(directory, request.Title, ct: ct);

            feedService.PublishRaw(EventTypes.SessionCreated, new
            {
                sessionID = session.Id.Value,
                title = session.Title,
                directory = session.Directory
            });

            return Results.Created($"/api/session/{session.Id}", session);
        });

        app.MapPost("/api/session/{id}/prompt", async (
            string id,
            PromptInput input,
            SessionExecutionEngine engine,
            IEventFeedService feedService,
            CancellationToken ct) =>
        {
            var sessionId = new SessionId(id);

            feedService.PublishRaw(EventTypes.SessionInboxEnqueued, new
            {
                sessionID = sessionId.Value,
                text = input.Text
            });

            var chunks = new List<string>();
            await foreach (var chunk in engine.PromptAsync(sessionId, input.Text, ct: ct))
            {
                chunks.Add(chunk);
            }

            feedService.PublishRaw(EventTypes.SessionIdle, new
            {
                sessionID = sessionId.Value,
                outcome = "succeeded"
            });

            return Results.Ok(new { text = string.Concat(chunks) });
        });
    }
}
