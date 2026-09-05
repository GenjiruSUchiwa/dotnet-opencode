namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Archive;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public static class SessionArchiveEndpoints
{
    /// <summary>Composition owner mounts this under the existing authenticated server pipeline.</summary>
    public static void MapSessionArchiveExportEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/session/{sessionID}/export", async (string sessionID, HttpRequest request,
            [FromServices] SessionArchiveService archive, CancellationToken ct) =>
        {
            try
            {
                var sanitize = RequestLocation.QueryValue(request, "sanitize");
                if (sanitize is not (null or "true" or "false")) throw new RequestArgumentException("sanitize must be true or false.", nameof(request));
                var data = await archive.ExportAsync(SessionId.FromExisting(sessionID), sanitize == "true", ct);
                return Results.Json(new ApiResult<SessionTransferData>(data), SessionArchiveProtocolJsonContext.Default.ArchiveResult);
            }
            catch (SessionMutationNotFoundException error) { return Missing(error.SessionId); }
            catch (SessionMessageReadException error)
            {
                request.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SessionArchiveEndpoints))
                    .LogError(error, "Archive export failed to decode a projected message.");
                return Results.Json(new { _tag = "UnknownError", message = "Failed to decode a projected session message." }, statusCode: 500);
            }
            catch (ArgumentException error) { return Invalid(error.Message); }
        });
    }

    /// <summary>Mount only after a canonical ISessionArchivePersistence adapter is registered.
    /// There is deliberately no SQL fallback or fake successful import.</summary>
    public static void MapSessionArchiveImportEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/session/import", async (HttpRequest request, [FromServices] SessionArchiveService archive,
            [FromServices] ISessionArchivePersistence persistence, CancellationToken ct) =>
        {
            try
            {
                var input = await request.ReadFromJsonAsync(SessionArchiveProtocolJsonContext.Default.SessionArchiveImportInput, ct)
                    ?? throw new JsonException("Missing archive payload.");
                // Unlike Location-scoped routes, upstream import defaults to server
                // cwd, not query/header placement. Reuse the existing cwd fallback.
                var location = input.Location ?? new LocationRef(Directory.GetCurrentDirectory());
                var result = await archive.ImportAsync(new(input.Info, input.Messages), location, persistence, ct);
                return Results.Json(new ApiResult<SessionInfo>(result), SessionProtocolJsonContext.Default.SessionResult);
            }
            catch (SessionArchiveConflictException error)
            { return Results.Json(new { _tag = "ConflictError", message = error.Message, resource = error.SessionId.Value }, statusCode: 409); }
            catch (SessionMutationNotFoundException error) { return Missing(error.SessionId); }
            catch (JsonException error) { return Invalid(error.Message); }
            catch (ArgumentException error) { return Invalid(error.Message); }
        });
    }

    private static IResult Missing(SessionId id) => Results.Json(new { _tag = "SessionNotFoundError", sessionID = id.Value,
        message = $"Session not found: {id.Value}" }, statusCode: 404);
    private static IResult Invalid(string message) => Results.Json(new { _tag = "InvalidRequestError", message }, statusCode: 400);
}
