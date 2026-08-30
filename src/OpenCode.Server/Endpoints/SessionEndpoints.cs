namespace OpenCode.Server.Endpoints;

using System.Text;
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

        app.MapGet("/api/session/{id}/inbox", (string id) =>
        {
            return Results.Ok(new { data = Array.Empty<object>() });
        });

        app.MapGet("/api/session/{id}/message", async (
            string id,
            SessionStore store,
            int? limit,
            CancellationToken ct) =>
        {
            var messages = await store.ListMessagesAsync(new SessionId(id), limit ?? 100, ct);
            return Results.Ok(new
            {
                data = messages,
                cursor = new { previous = (string?)null, next = (string?)null }
            });
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
                location = session.Location,
                agent = request.Agent,
                model = request.Model
            });

            return Results.Ok(new { data = session });
        });

        app.MapPost("/api/session/{id}/prompt", async (
            string id,
            PromptApiRequest input,
            SessionStore store,
            ProviderResolver resolver,
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

            feedService.PublishRaw("session.inbox.enqueued", inboxUser);

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

                    feedService.PublishRaw("session.inbox.delivered", new
                    {
                        sessionID = sessionId.Value,
                        id = msgId.Value
                    });

                    // 2. Resolve Model and Start Assistant Step
                    var assistantMsgId = MessageId.Create();
                    var modelToUse = input.Model ?? "gemini-3.7-flash";
                    var variantToUse = input.Variant ?? "high";
                    var resolved = await resolver.ResolveAsync(modelToUse, variantToUse, CancellationToken.None);

                    var modelRef = resolved.ModelId.Contains('/')
                        ? ModelRef.Parse(resolved.ModelId)
                        : new ModelRef(resolved.Client.ProviderId, resolved.ModelId);

                    feedService.PublishRaw("session.step.started", new
                    {
                        sessionID = sessionId.Value,
                        assistantMessageID = assistantMsgId.Value,
                        agent = "build",
                        model = modelRef
                    });

                    feedService.PublishRaw("session.text.started", new
                    {
                        sessionID = sessionId.Value,
                        assistantMessageID = assistantMsgId.Value
                    });

                    var assistantText = new StringBuilder();
                    var messages = new List<LlmChatMessage>
                    {
                        new("system", "You are an AI engineering agent inside opencode-dotnet. Help the user accomplish their goals."),
                        new("user", input.Text)
                    };

                    await foreach (var chunk in resolved.Client.StreamChatAsync(messages, resolved.ModelId, resolved.GenerationConfig, CancellationToken.None))
                    {
                        assistantText.Append(chunk);
                        feedService.PublishRaw("session.text.delta", new
                        {
                            sessionID = sessionId.Value,
                            assistantMessageID = assistantMsgId.Value,
                            delta = chunk
                        });
                    }

                    var fullText = assistantText.ToString();
                    feedService.PublishRaw("session.text.ended", new
                    {
                        sessionID = sessionId.Value,
                        assistantMessageID = assistantMsgId.Value,
                        text = fullText
                    });

                    feedService.PublishRaw("session.step.streamed", new
                    {
                        sessionID = sessionId.Value,
                        assistantMessageID = assistantMsgId.Value
                    });

                    // Record assistant message to SQLite
                    var assistantMsg = new AssistantMessage
                    {
                        Id = assistantMsgId,
                        Time = new MessageTime(DateTimeOffset.UtcNow),
                        Model = modelRef,
                        Content = [new AssistantTextContent(fullText)]
                    };
                    await store.AddMessageAsync(sessionId, assistantMsg, CancellationToken.None);

                    feedService.PublishRaw("session.idle", new
                    {
                        sessionID = sessionId.Value,
                        outcome = "succeeded"
                    });
                }
                catch (Exception ex)
                {
                    feedService.PublishRaw("session.idle", new
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
