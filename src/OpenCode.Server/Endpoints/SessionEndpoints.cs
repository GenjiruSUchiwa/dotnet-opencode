namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Schema;
using OpenCode.Server.Services;

public sealed record CreateSessionApiRequest(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("directory")] string? Directory = null,
    [property: JsonPropertyName("agent")] string? Agent = null,
    [property: JsonPropertyName("model")] ModelRef? Model = null,
    [property: JsonPropertyName("location")] LocationRef? Location = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
);

public sealed record PromptApiRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("delivery")] string? Delivery = "steer",
    [property: JsonPropertyName("resume")] bool? Resume = true,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("variant")] string? Variant = null
);

public sealed record SwitchAgentApiRequest(
    [property: JsonPropertyName("agent")] string Agent
);

public sealed record SwitchModelApiRequest(
    [property: JsonPropertyName("model")] ModelRef Model
);

public sealed record RenameSessionApiRequest(
    [property: JsonPropertyName("title")] string Title
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

        app.MapGet("/api/session/active", () =>
        {
            return Results.Ok(new { data = new Dictionary<string, object>() });
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

        app.MapDelete("/api/session/{id}", async (
            string id,
            SessionStore store,
            CancellationToken ct) =>
        {
            await store.DeleteSessionAsync(new SessionId(id), ct);
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/agent", async (
            string id,
            SwitchAgentApiRequest request,
            SessionStore store,
            CancellationToken ct) =>
        {
            await store.UpdateAgentAsync(new SessionId(id), request.Agent, ct);
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/model", async (
            string id,
            SwitchModelApiRequest request,
            SessionStore store,
            CancellationToken ct) =>
        {
            await store.UpdateModelAsync(new SessionId(id), request.Model, ct);
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/rename", async (
            string id,
            RenameSessionApiRequest request,
            SessionStore store,
            CancellationToken ct) =>
        {
            await store.UpdateTitleAsync(new SessionId(id), request.Title, ct);
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/interrupt", () =>
        {
            return Results.Ok(new { interrupted = true });
        });

        app.MapPost("/api/session/{id}/background", () =>
        {
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/move", () =>
        {
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/fork", async (
            string id,
            SessionStore store,
            CancellationToken ct) =>
        {
            var existing = await store.GetSessionAsync(new SessionId(id), ct);
            if (existing is null) return Results.NotFound();

            var forked = await store.CreateSessionAsync(existing.Directory ?? Directory.GetCurrentDirectory(), $"Fork of {existing.Title}", existing.ProjectId, ct: ct);
            return Results.Ok(new { data = forked });
        });

        app.MapPut("/api/session/{id}/environment", () =>
        {
            return Results.NoContent();
        });

        app.MapPost("/api/session/{id}/view", () =>
        {
            return Results.NoContent();
        });

        app.MapPost("/api/session", async (
            CreateSessionApiRequest request,
            SessionStore store,
            IEventFeedService feedService,
            CancellationToken ct) =>
        {
            var directory = request.Location?.Directory ?? request.Directory ?? Directory.GetCurrentDirectory();
            var sessionId = !string.IsNullOrEmpty(request.Id) ? new SessionId(request.Id) : (SessionId?)null;

            var session = await store.CreateSessionAsync(directory, request.Title, sessionId: sessionId, ct: ct);

            feedService.PublishRaw(EventTypes.SessionCreated, new
            {
                sessionID = session.Id.Value,
                title = session.Title,
                directory = session.Directory,
                agent = request.Agent,
                model = request.Model
            });

            return Results.Ok(new { data = session });
        });

        app.MapPost("/api/session/{id}/prompt", async (
            string id,
            PromptApiRequest input,
            SessionStore store,
            SessionExecutionEngine engine,
            IEventFeedService feedService,
            CancellationToken ct) =>
        {
            var sessionId = new SessionId(id);
            var msgId = !string.IsNullOrEmpty(input.Id) ? new MessageId(input.Id) : MessageId.Create();
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();

            var inboxUser = new
            {
                id = msgId.Value,
                sessionID = sessionId.Value,
                timeCreated = nowMs,
                type = "user",
                delivery = input.Delivery ?? "steer",
                payload = new
                {
                    text = input.Text
                }
            };

            feedService.PublishRaw(EventTypes.SessionInboxEnqueued, inboxUser);

            // Run execution asynchronously in background so prompt admission returns immediately
            _ = Task.Run(async () =>
            {
                try
                {
                    // 1. Deliver user message
                    var userMsg = new UserMessage
                    {
                        Id = msgId,
                        Time = new MessageTime(now),
                        Text = input.Text
                    };
                    await store.AddMessageAsync(sessionId, userMsg, CancellationToken.None);

                    feedService.PublishRaw(EventTypes.SessionInboxDelivered, new
                    {
                        sessionID = sessionId.Value,
                        id = msgId.Value
                    });

                    // 2. Stream assistant completion
                    var modelToUse = input.Model ?? "gemini-3.7-flash";
                    var variantToUse = input.Variant ?? "high";

                    var chunks = new List<string>();
                    await foreach (var chunk in engine.PromptAsync(sessionId, input.Text, modelToUse, variantToUse, CancellationToken.None))
                    {
                        chunks.Add(chunk);
                    }

                    feedService.PublishRaw(EventTypes.SessionIdle, new
                    {
                        sessionID = sessionId.Value,
                        outcome = "succeeded"
                    });
                }
                catch (Exception ex)
                {
                    feedService.PublishRaw(EventTypes.SessionIdle, new
                    {
                        sessionID = sessionId.Value,
                        outcome = "failed",
                        error = ex.Message
                    });
                }
            });

            return Results.Ok(new { data = inboxUser });
        });
    }
}
