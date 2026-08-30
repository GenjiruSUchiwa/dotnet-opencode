namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Database;
using OpenCode.Core.Session;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Services;

public sealed record PromptRequest(
    string Text,
    string? Model = null,
    string? Variant = null
);

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

        app.MapGet("/api/session/{id}", async (
            string id,
            SessionStore store,
            CancellationToken ct) =>
        {
            var session = await store.GetSessionAsync(new SessionId(id), ct);
            return session is not null ? Results.Ok(new { data = session }) : Results.NotFound();
        });

        app.MapGet("/api/session/{id}/message", async (
            string id,
            SessionStore store,
            int? limit,
            CancellationToken ct) =>
        {
            var messages = await store.ListMessagesAsync(new SessionId(id), limit ?? 100, ct);
            return Results.Ok(new { data = messages, cursor = (string?)null });
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
            PromptRequest input,
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
            var modelToUse = input.Model ?? "gemini-flash";
            var variantToUse = input.Variant ?? "high";

            await foreach (var chunk in engine.PromptAsync(sessionId, input.Text, modelToUse, variantToUse, ct: ct))
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
