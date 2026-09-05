namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Forms;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Services;

public static class FormEndpoints
{
    public static void MapFormEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (FormNotFoundException error) { return Results.Json(new { _tag = "FormNotFoundError", id = error.Id, message = error.Message }, statusCode: 404); }
            catch (FormAlreadyExistsException error) { return Results.Json(new { _tag = "ConflictError", resource = error.Id, message = error.Message }, statusCode: 409); }
            catch (FormAlreadySettledException error) { return Results.Json(new { _tag = "FormAlreadySettledError", id = error.Id, message = error.Message }, statusCode: 409); }
            catch (FormInvalidAnswerException error) { return Results.Json(new { _tag = "FormInvalidAnswerError", id = error.Id, message = error.Message }, statusCode: 400); }
            catch (FormInvalidException error) { return Results.Json(new { _tag = "InvalidRequestError", field = "fields", message = error.Message }, statusCode: 400); }
            catch (SessionMutationNotFoundException error) { return Results.Json(new { _tag = "SessionNotFoundError", sessionID = error.SessionId, message = $"Session not found: {error.SessionId}" }, statusCode: 404); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            { return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400); }
            catch (Exception error) when (error is NotSupportedException or CatalogLocationUnavailableException or ObjectDisposedException)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "form", message = error.Message }, statusCode: 503); }
        });

        routes.MapGet("/form/request", async (HttpRequest request, IDatabase database, FormLocationServices forms, CancellationToken ct) =>
        {
            var info = await RequestLocation.ResolveAsync(request, database, ct);
            await using var lease = await forms.AcquireAsync(new(info.Directory, info.WorkspaceId), ct: ct);
            return Results.Json(new LocationResponse<IReadOnlyList<FormInfo>>(lease!.Location, lease.Forms.List()), FormProtocolJsonContext.Default.LocationFormsResult);
        });
        routes.MapGet("/session/{sessionID}/form", async (string sessionID, HttpRequest request, IDatabase database,
            SessionStore sessions, FormLocationServices forms, CancellationToken ct) =>
        {
            var reference = await ReferenceAsync(sessionID, request, database, sessions, ct);
            await using var lease = await forms.AcquireAsync(reference, loadedOnly: true, ct);
            return Results.Json(new ApiResult<IReadOnlyList<FormInfo>>(lease?.Forms.List(sessionID) ?? []), FormProtocolJsonContext.Default.FormsResult);
        });
        routes.MapPost("/session/{sessionID}/form", async (string sessionID, FormCreatePayload input, HttpRequest request,
            IDatabase database, SessionStore sessions, FormLocationServices forms, CancellationToken ct) =>
        {
            var reference = await ReferenceAsync(sessionID, request, database, sessions, ct);
            await using var lease = await forms.AcquireAsync(reference, ct: ct);
            return Results.Json(new ApiResult<FormInfo>(lease!.Forms.Create(sessionID, input)), FormProtocolJsonContext.Default.FormResult);
        });
        routes.MapGet("/session/{sessionID}/form/{formID}", async (string sessionID, string formID, HttpRequest request,
            IDatabase database, SessionStore sessions, FormLocationServices forms, CancellationToken ct) =>
        {
            var reference = await ReferenceAsync(sessionID, request, database, sessions, ct);
            await using var lease = await forms.AcquireAsync(reference, ct: ct);
            return Results.Json(new ApiResult<FormInfo>(Owned(lease!.Forms, sessionID, FormId.FromExisting(formID))), FormProtocolJsonContext.Default.FormResult);
        });
        routes.MapGet("/session/{sessionID}/form/{formID}/state", async (string sessionID, string formID, HttpRequest request,
            IDatabase database, SessionStore sessions, FormLocationServices forms, CancellationToken ct) =>
        {
            var reference = await ReferenceAsync(sessionID, request, database, sessions, ct);
            await using var lease = await forms.AcquireAsync(reference, ct: ct);
            var info = Owned(lease!.Forms, sessionID, FormId.FromExisting(formID));
            return Results.Json(new ApiResult<FormState>(lease.Forms.State(info.Id)), FormProtocolJsonContext.Default.StateResult);
        });
        routes.MapPost("/session/{sessionID}/form/{formID}/reply", async (string sessionID, string formID, FormReply input,
            HttpRequest request, IDatabase database, SessionStore sessions, FormLocationServices forms, CancellationToken ct) =>
        {
            var reference = await ReferenceAsync(sessionID, request, database, sessions, ct);
            await using var lease = await forms.AcquireAsync(reference, ct: ct);
            var info = Owned(lease!.Forms, sessionID, FormId.FromExisting(formID));
            lease.Forms.Reply(info.Id, input.Answer);
            return Results.NoContent();
        });
        routes.MapPost("/session/{sessionID}/form/{formID}/cancel", async (string sessionID, string formID, HttpRequest request,
            IDatabase database, SessionStore sessions, FormLocationServices forms, CancellationToken ct) =>
        {
            var reference = await ReferenceAsync(sessionID, request, database, sessions, ct);
            await using var lease = await forms.AcquireAsync(reference, ct: ct);
            var info = Owned(lease!.Forms, sessionID, FormId.FromExisting(formID));
            lease.Forms.Cancel(info.Id);
            return Results.NoContent();
        });
    }

    private static async Task<LocationRef> ReferenceAsync(string owner, HttpRequest request, IDatabase database, SessionStore sessions, CancellationToken ct)
    {
        if (owner == "global")
        {
            var info = await RequestLocation.ResolveAsync(request, database, ct);
            return new(info.Directory, info.WorkspaceId);
        }
        var id = SessionId.FromExisting(owner);
        return (await sessions.GetSessionAsync(id, ct) ?? throw new SessionMutationNotFoundException(id)).Location;
    }

    private static FormInfo Owned(FormService forms, string owner, FormId id)
    {
        var info = forms.Get(id);
        return info.SessionId == owner ? info : throw new FormNotFoundException(id);
    }
}
