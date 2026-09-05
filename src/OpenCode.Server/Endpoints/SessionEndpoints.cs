namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Commands;
using OpenCode.Core.Event;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Transfer;
using OpenCode.Core.Snapshot;
using OpenCode.Protocol.Groups;
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PromptApiRequest(
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("delivery")] string? Delivery = "steer",
    [property: JsonPropertyName("resume")] bool? Resume = true,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("variant")] string? Variant = null,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptInputFileAttachment>? Files = null,
    [property: JsonPropertyName("agents")] IReadOnlyList<PromptAgentAttachment>? Agents = null,
    [property: JsonPropertyName("skills")] IReadOnlyList<PromptInputSkillAttachment>? Skills = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null
);

public sealed record SwitchAgentApiRequest([property: JsonPropertyName("agent")] string Agent);
public sealed record SwitchModelApiRequest([property: JsonPropertyName("model")] ModelRef Model);
public sealed record RenameSessionApiRequest([property: JsonPropertyName("title")] string Title);
public sealed record ViewSessionApiRequest([property: JsonPropertyName("idle"), JsonRequired] long Idle);
public sealed record SessionEnvironmentApiRequest(
    [property: JsonPropertyName("variables"), JsonRequired] IReadOnlyDictionary<string, string> Variables);
public sealed record InstructionEntryApiRequest([property: JsonPropertyName("value"), JsonRequired] JsonElement Value);
public sealed record StageRevertApiRequest([property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId,
    [property: JsonPropertyName("files")] bool Files = true);
public sealed record SessionGenerateApiRequest([property: JsonPropertyName("prompt"), JsonRequired] string Prompt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompactSessionApiRequest(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("delivery")] string Delivery = "steer");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommandSessionApiRequest(
    [property: JsonPropertyName("command"), JsonRequired] string Command,
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptInputFileAttachment>? Files = null,
    [property: JsonPropertyName("agents")] IReadOnlyList<PromptAgentAttachment>? Agents = null,
    [property: JsonPropertyName("skills")] IReadOnlyList<PromptInputSkillAttachment>? Skills = null,
    [property: JsonPropertyName("delivery")] string Delivery = "steer");

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/session");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (InstructionEntryValueTooLargeException error)
            {
                return Results.Json(new { _tag = "InstructionEntryValueTooLargeError", actualBytes = error.ActualBytes,
                    maxBytes = error.MaxBytes, message = error.Message }, statusCode: 413);
            }
            catch (CommandNotFoundException error)
            {
                return Results.Json(new { _tag = "CommandNotFoundError", command = error.Command, message = error.Message }, statusCode: 404);
            }
            catch (CommandExecutionException error)
            {
                return Results.Json(new { _tag = "CommandExecutionError", command = error.Command, message = error.Message }, statusCode: 500);
            }
            catch (SessionMutationNotFoundException error) { return Missing(error.SessionId.Value); }
            catch (SessionBusyException error)
            {
                return Results.Json(new { _tag = "SessionBusyError", sessionID = error.SessionId.Value,
                    message = $"Session is busy: {error.SessionId.Value}" }, statusCode: 409);
            }
            catch (SessionRevertMessageNotFoundException error)
            {
                return Results.Json(new { _tag = "MessageNotFoundError", sessionID = error.SessionId.Value,
                    messageID = error.MessageId.Value, message = $"Message not found: {error.MessageId.Value}" }, statusCode: 404);
            }
            catch (SnapshotException error)
            {
                var reference = "err_" + Guid.NewGuid().ToString("N")[..8];
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SessionEndpoints))
                    .LogError(error, "Session revert snapshot operation failed: {Operation} ({Reference})", error.Operation, reference);
                return Results.Json(new { _tag = "UnknownError", message = "Unexpected server error. Check server logs for details.", @ref = reference }, statusCode: 500);
            }
            catch (SessionForkMessageNotFoundException error)
            {
                return Results.Json(new { _tag = "MessageNotFoundError", sessionID = error.SessionId.Value,
                    messageID = error.MessageId.Value, message = $"Message not found: {error.MessageId.Value}" }, statusCode: 404);
            }
            catch (SessionForkEmptyException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message, kind = "empty_session" }, statusCode: 400);
            }
            catch (SessionMoveDestinationException error)
            {
                return Invalid(error.Failure switch
                {
                    SessionMoveDestinationFailure.NotFound => $"Directory does not exist: {error.Directory}",
                    SessionMoveDestinationFailure.NotDirectory => $"Not a directory: {error.Directory}",
                    _ => $"Directory is unavailable: {error.Directory}"
                });
            }
            catch (SessionMutationInProgressException error)
            {
                return Results.Json(new { _tag = "SessionBusyError", sessionID = error.SessionId.Value,
                    message = $"Session is busy: {error.SessionId.Value}" }, statusCode: 409);
            }
            catch (SessionSelectionException error) { return Invalid(error.Message, error.Field); }
            catch (PromptAttachmentException error) { return Invalid(error.Message, "files"); }
            catch (PromptSkillNotFoundException error) { return Invalid(error.Message, "skills"); }
            catch (LlmException)
            {
                return Unavailable("The provider service could not resolve this request.");
            }
            catch (SessionCursorException error)
            {
                return Results.Json(new { _tag = "InvalidCursorError", message = error.Message }, statusCode: 400);
            }
            catch (SessionQueryParameterException error) { return Invalid(error.Message, error.Field); }
            catch (SessionMessageReadException error)
            {
                var reference = "err_" + Guid.NewGuid().ToString("N")[..8];
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SessionEndpoints))
                    .LogError(error, "Failed to decode session message {SessionId} {MessageId} ({Reference})", error.SessionId.Value, error.MessageId.Value, reference);
                return Results.Json(new { _tag = "UnknownError", message = "Unexpected server error. Check server logs for details.", @ref = reference }, statusCode: 500);
            }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (InboxLifecycleConflictException error)
            {
                var message = context.HttpContext.Request.Path.Value?.EndsWith("/prompt", StringComparison.Ordinal) == true
                    ? $"Prompt message ID conflicts with an existing durable record: {error.Id.Value}" : error.Message;
                return Results.Json(new { _tag = "ConflictError", message, resource = error.Id.Value }, statusCode: 409);
            }
            catch (ArgumentException error) { return Invalid(error.Message); }
        });

        routes.MapGet("", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var query = SessionQueryParameters.Sessions(request.Query);
            var sessions = await new SessionQueries(database).ListAsync(query, ct);
            return Results.Ok(new
            {
                data = sessions,
                cursor = sessions.Count == 0 ? new SessionPageCursors() : new SessionPageCursors(
                    SessionQueryParameters.SessionCursor(query, sessions[0], SessionPageDirection.Previous),
                    SessionQueryParameters.SessionCursor(query, sessions[^1], SessionPageDirection.Next))
            });
        });

        routes.MapGet("/active", (SessionExecutionEngine engine) => Results.Ok(new
        {
            data = engine.ActiveSessionIds.ToDictionary(id => id.Value, _ => new { type = "running" })
        }));
        routes.MapGet("/capabilities", (SessionExecutionService execution) => Results.Ok(new
        {
            data = execution.Capabilities
        }));

        routes.MapGet("/{id}", async (string id, SessionStore store, CancellationToken ct) =>
        {
            var session = await store.GetSessionAsync(SessionId.FromExisting(id), ct);
            return session is not null ? Results.Ok(new { data = session }) : Missing(id);
        });

        routes.MapGet("/{id}/inbox", async (string id, SessionStore store, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            return Results.Ok(new { data = await store.ListInboxAsync(sessionId, ct) });
        });

        routes.MapDelete("/{id}/inbox/{inboxID}", (string id, string inboxID, SessionStore store,
            SessionExecutionService execution, CancellationToken ct) => MutateInboxAsync(id, inboxID, null, store, execution, ct));
        routes.MapPost("/{id}/inbox/{inboxID}/steer", (string id, string inboxID, SessionStore store,
            SessionExecutionService execution, CancellationToken ct) => MutateInboxAsync(id, inboxID, InboxDeliveryMode.Steer, store, execution, ct));
        routes.MapPost("/{id}/inbox/{inboxID}/queue", (string id, string inboxID, SessionStore store,
            SessionExecutionService execution, CancellationToken ct) => MutateInboxAsync(id, inboxID, InboxDeliveryMode.Queue, store, execution, ct));

        routes.MapGet("/{id}/message", async (string id, HttpRequest request, IDatabase database, SessionStore store, CancellationToken ct) =>
        {
            var query = SessionQueryParameters.Messages(request.Query);
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            var messages = await new SessionQueries(database).MessagesAsync(sessionId, query.Limit, query.Order, query.Anchor, ct);
            return Results.Ok(new
            {
                data = messages,
                cursor = messages.Count == 0 ? new SessionPageCursors() : new SessionPageCursors(
                    SessionQueryParameters.MessageCursor(messages[0], query.Order, SessionPageDirection.Previous),
                    SessionQueryParameters.MessageCursor(messages[^1], query.Order, SessionPageDirection.Next))
            });
        });

        routes.MapPost("/{id}/interrupt", async (string id, HttpRequest request, SessionStore store, SessionExecutionService execution, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            if (request.Query.Keys.FirstOrDefault(key => key is not ("continue" or "auth_token")) is { } unsupported)
                return Invalid("This interrupt query option is not implemented.", unsupported);
            if (request.Query.TryGetValue("continue", out var continuation))
            {
                if (continuation.Count != 1 || continuation[0] is not ("true" or "false"))
                    return Invalid("Continue must be true or false.", "continue");
            }
            return Results.Ok(new { interrupted = await execution.InterruptAsync(sessionId,
                new SessionInterruptOptions(request.Query["continue"] == "true"), ct) });
        });

        routes.MapGet("/{id}/message/{messageID}", async (string id, string messageID, SessionStore store, SessionQueries queries, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            var message = await queries.MessageAsync(sessionId, MessageId.FromExisting(messageID), ct);
            return message is not null ? Results.Ok(new { data = message }) : Results.Json(new
            {
                _tag = "MessageNotFoundError", sessionID = id, messageID, message = $"Message not found: {messageID}"
            }, statusCode: 404);
        });
        routes.MapGet("/{id}/context", async (string id, SessionQueries queries, CancellationToken ct) =>
            Results.Ok(new { data = await queries.ContextAsync(SessionId.FromExisting(id), ct) }));
        routes.MapPost("/{id}/revert/stage", async (string id, StageRevertApiRequest input, SessionRevertOperations reverts,
            SessionExecutionService execution, CancellationToken ct) =>
        {
            execution.RequireRecordingReady();
            return Results.Ok(new { data = await reverts.StageAsync(SessionId.FromExisting(id), input.MessageId, input.Files, ct) });
        });
        routes.MapPost("/{id}/revert/clear", async (string id, SessionExecutionService execution, CancellationToken ct) =>
        {
            await execution.ClearRevertAsync(SessionId.FromExisting(id), ct);
            return Results.NoContent();
        });
        routes.MapPost("/{id}/revert/commit", async (string id, SessionRevertOperations reverts, SessionExecutionService execution, CancellationToken ct) =>
        {
            execution.RequireRecordingReady();
            await reverts.CommitAsync(SessionId.FromExisting(id), ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/resume", async (string id, SessionStore store, SessionExecutionService execution, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            await execution.ResumeAsync(sessionId, ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/wait", async (string id, SessionStore store, SessionExecutionEngine engine, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            // Observation only: disconnect cancels waiting, never the active drain.
            await engine.AwaitIdleAsync(sessionId, ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/command", async (string id, CommandSessionApiRequest input, IServiceProvider services, CancellationToken ct) =>
        {
            if (input.Command is null) return Invalid("Command is required.", "command");
            if (input.Text is null) return Invalid("Text is required.", "text");
            if (input.Delivery is not ("queue" or "steer")) return Invalid("Delivery must be queue or steer.", "delivery");
            var commands = services.GetService<CommandHostService>()
                ?? throw new NotSupportedException("The shared Location command host is not configured.");
            await commands.ExecuteAsync(SessionId.FromExisting(id), input.Command, new PromptInput(input.Text, input.Files, input.Agents, input.Skills),
                input.Delivery == "queue" ? InboxDeliveryMode.Queue : InboxDeliveryMode.Steer, ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/compact", async (string id, CompactSessionApiRequest input, SessionStore store,
            SessionExecutionService execution, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            if (input.Id is not null && !input.Id.StartsWith("msg_", StringComparison.Ordinal))
                return Invalid("Compaction input ID must start with msg_.", "id");
            if (input.Delivery is not ("queue" or "steer")) return Invalid("Delivery must be queue or steer.", "delivery");
            execution.RequireRecordingReady();
            SessionInboxItem item;
            try
            {
                item = await store.AdmitCompactionAsync(sessionId, input.Id is null ? null : MessageId.FromExisting(input.Id),
                    input.Delivery == "queue" ? InboxDeliveryMode.Queue : InboxDeliveryMode.Steer, ct);
            }
            catch (InboxLifecycleConflictException error)
            {
                return Results.Json(new
                {
                    _tag = "ConflictError",
                    message = $"Compaction input ID conflicts with an existing durable record: {error.Id.Value}",
                    resource = error.Id.Value
                }, statusCode: 409);
            }
            // Compaction always wakes, including pending retries. Request cancellation
            // applies only to admission; Core owns delivery and the summary request.
            await execution.WakeAsync(sessionId);
            return Results.Ok(new { data = item });
        });

        routes.MapPost("/{id}/prompt", async (string id, PromptApiRequest input, SessionStore store,
            SessionExecutionEngine engine, SessionExecutionService execution, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            if (input.Id is { Length: 0 }) return Invalid("Message ID must not be empty.", "id");
            var messageId = input.Id is not null ? MessageId.FromExisting(input.Id) : MessageId.Create();
            var delivery = input.Delivery == "queue" ? InboxDeliveryMode.Queue : InboxDeliveryMode.Steer;
            var existing = await store.ReconcileInboxAsync(sessionId, messageId, "user", delivery, ct);
            if (existing is not null)
            {
                // A retry preserves first admission but still requests advisory execution.
                if (input.Resume != false) await execution.WakeAsync(sessionId);
                return Results.Ok(new { data = existing });
            }
            if (input.Text is null) return Invalid("Text is required.", "text");
            if (input.Delivery is not ("queue" or "steer")) return Invalid("Delivery must be queue or steer.", "delivery");
            execution.RequireRecordingReady();
            // Session.prompt admits durably before execution readiness/model resolution.
            // Unsupported preparation still fails before any new inbox item is committed.
            if (input.Model is not null || input.Variant is not null)
                return Invalid("Prompt model overrides are unsupported; select the session model before admission.", "model");
            var item = await engine.AdmitPromptAsync(sessionId, new PromptInput(input.Text, input.Files, input.Agents, input.Skills),
                messageId, input.Metadata, delivery, ct);
            // Only admission observes request cancellation. Advisory execution belongs to the host.
            if (input.Resume != false) await execution.WakeAsync(sessionId);
            // Core owns committed envelopes. Never reconstruct an enqueue event from this read model.
            return Results.Ok(new { data = item });
        });

        routes.MapPost("", async (CreateSessionApiRequest input, SessionStore store, SessionExecutionService execution, CancellationToken ct) =>
        {
            execution.RequireRecordingReady();
            var session = await store.CreateSessionAsync(input.Location?.Directory ?? input.Directory ?? Directory.GetCurrentDirectory(),
                title: input.Title, sessionId: input.Id is null ? null : SessionId.FromExisting(input.Id), ct: ct,
                agent: input.Agent, model: input.Model, location: input.Location, metadata: input.Metadata);
            return Results.Ok(new { data = session });
        });

        routes.MapPost("/{id}/rename", async (string id, RenameSessionApiRequest input,
            SessionMutations mutations, SessionExecutionService execution, SessionStore store, SessionTitleService titles, CancellationToken ct) =>
        {
            if (input.Title is null) return Invalid("Title is required.", "title");
            execution.RequireRecordingReady();
            if (input.Title.Length == 0)
            {
                var sessionId = SessionId.FromExisting(id);
                if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
                await titles.GenerateAsync(sessionId, ct);
                return Results.NoContent();
            }
            await mutations.RenameAsync(SessionId.FromExisting(id), input.Title, ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/agent", async (string id, SwitchAgentApiRequest input,
            SessionMutations mutations, SessionExecutionService execution, CancellationToken ct) =>
        {
            if (input.Agent is null) return Invalid("Agent is required.", "agent");
            execution.RequireRecordingReady();
            await mutations.SelectAgentAsync(SessionId.FromExisting(id), input.Agent, ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/model", async (string id, SwitchModelApiRequest input,
            SessionMutations mutations, SessionExecutionService execution, CancellationToken ct) =>
        {
            if (input.Model is null) return Invalid("Model is required.", "model");
            execution.RequireRecordingReady();
            await mutations.SelectModelAsync(SessionId.FromExisting(id), input.Model, ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/view", async (string id, ViewSessionApiRequest input,
            SessionMutations mutations, SessionExecutionService execution, CancellationToken ct) =>
        {
            execution.RequireRecordingReady();
            await mutations.ViewAsync(SessionId.FromExisting(id), input.Idle, ct);
            return Results.NoContent();
        });

        routes.MapDelete("/{id}", async (string id, SessionMutations mutations,
            SessionExecutionService execution, CancellationToken ct) =>
        {
            execution.RequireRecordingReady();
            await mutations.RemoveAsync(SessionId.FromExisting(id), ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/fork", async (string id, SessionForkInput input, IServiceProvider services,
            SessionExecutionService execution, CancellationToken ct) =>
        {
            execution.RequireRecordingReady();
            var transfer = services.GetService<SessionTransfer>()
                ?? throw new NotSupportedException("The native SessionTransfer service is not configured.");
            var fork = await transfer.ForkAsync(new SessionForkRequest(SessionId.FromExisting(id), input.Boundary), ct);
            return Results.Ok(new { data = fork });
        });

        routes.MapPost("/{id}/move", async (string id, SessionMoveInput input, SessionStore store,
            SessionExecutionService execution, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            if (input.Directory is null) return Invalid("Directory is required.", "directory");
            if (input.Delivery is not (null or InboxDeliveryMode.Queue or InboxDeliveryMode.Steer)) return Invalid("Delivery must be queue or steer.", "delivery");
            await execution.MoveAsync(new SessionMoveRequest(sessionId, input.Directory, input.WorkspaceId,
                input.Delivery ?? InboxDeliveryMode.Steer), ct);
            return Results.NoContent();
        });

        routes.MapPost("/{id}/background", async (string id, SessionBackgroundService background, CancellationToken ct) =>
        {
            await background.BackgroundAsync(SessionId.FromExisting(id), ct);
            return Results.NoContent();
        });
        routes.MapPost("/{id}/generate", async (string id, SessionGenerateApiRequest input, SessionExecutionEngine engine, CancellationToken ct) =>
        {
            if (input.Prompt is null) return Invalid("Prompt is required.", "prompt");
            var sessionId = SessionId.FromExisting(id);
            try { return Results.Ok(new { data = new { text = await engine.GenerateAsync(sessionId, input.Prompt, ct) } }); }
            catch (SessionMutationNotFoundException) { throw; }
            catch (Exception error) when (!ct.IsCancellationRequested)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "session generation", message = error.Message }, statusCode: 503); }
        });
        routes.MapGet("/{id}/instructions/entries", async (string id, SessionStore store,
            SessionInstructionEntries instructions, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            return Results.Ok(new { data = await instructions.ListAsync(sessionId, ct) });
        });
        routes.MapPut("/{id}/instructions/entries/{key}", async (string id, string key, InstructionEntryApiRequest input,
            SessionStore store, SessionInstructionEntries instructions, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            await instructions.PutAsync(sessionId, key, input.Value, ct);
            return Results.NoContent();
        });
        routes.MapDelete("/{id}/instructions/entries/{key}", async (string id, string key, SessionStore store,
            SessionInstructionEntries instructions, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            await instructions.RemoveAsync(sessionId, key, ct);
            return Results.NoContent();
        });
        routes.MapPut("/{id}/environment", async (string id, SessionEnvironmentApiRequest input,
            SessionStore store, SessionEnvironment environments, CancellationToken ct) =>
        {
            var sessionId = SessionId.FromExisting(id);
            if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
            if (input.Variables is null || input.Variables.Any(pair => pair.Value is null))
                return Invalid("Variables must be a record of string values.", "variables");
            environments.Set(sessionId, input.Variables);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> MutateInboxAsync(string id, string inboxID, InboxDeliveryMode? delivery,
        SessionStore store, SessionExecutionService execution, CancellationToken ct)
    {
        var sessionId = SessionId.FromExisting(id);
        var messageId = MessageId.FromExisting(inboxID);
        if (await store.GetSessionAsync(sessionId, ct) is null) return Missing(id);
        execution.RequireRecordingReady();
        ct.ThrowIfCancellationRequested();
        try
        {
            // Like Session.mutatePending, mutation and its advisory wake are not
            // abandoned when the submitting HTTP client disconnects.
            if (delivery is null) await store.CancelInboxAsync(sessionId, messageId, CancellationToken.None);
            else await store.ChangeInboxDeliveryAsync(sessionId, messageId, delivery.Value, CancellationToken.None);
        }
        catch (InboxLifecycleConflictException)
        {
            var message = delivery switch
            {
                null => "Pending input can no longer be cancelled",
                InboxDeliveryMode.Steer => "Pending input is no longer queued",
                _ => "Pending input is no longer a steer"
            };
            return Results.Json(new { _tag = "ConflictError", resource = inboxID, message = $"{message}: {inboxID}" }, statusCode: 409);
        }
        catch (InvalidOperationException error) when (error.Message == "Inbox operations require an existing session.")
        {
            return Missing(id);
        }
        if (delivery == InboxDeliveryMode.Steer) await execution.WakeAsync(sessionId);
        return Results.NoContent();
    }

    private static IResult Missing(string id) => Results.Json(new
    {
        _tag = "SessionNotFoundError", sessionID = id, message = $"Session not found: {id}"
    }, statusCode: 404);

    private static IResult Invalid(string message, string? field = null) => field is null
        ? Results.Json(new { _tag = "InvalidRequestError", message }, statusCode: 400)
        : Results.Json(new { _tag = "InvalidRequestError", message, field }, statusCode: 400);

    private static IResult Unavailable(string message) => Results.Json(new
    {
        _tag = "ServiceUnavailableError", message, service = "session"
    }, statusCode: 503);
}
