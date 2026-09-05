namespace OpenCode.Server.Endpoints;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Pty;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Pty;

internal sealed record PersistentReadResponse([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PersistentPtyReadResult? Data);
internal sealed record PersistentHandoffResponse([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PersistentPtyHandoff? Handoff);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<PersistentPtyInfo>>), TypeInfoPropertyName = "Terminals")]
[JsonSerializable(typeof(ApiResult<PersistentPtyInfo>), TypeInfoPropertyName = "Terminal")]
[JsonSerializable(typeof(ApiResult<PersistentPtySnapshot>), TypeInfoPropertyName = "Snapshot")]
[JsonSerializable(typeof(ApiResult<PtyConnectToken>), TypeInfoPropertyName = "Ticket")]
[JsonSerializable(typeof(PersistentReadResponse))]
[JsonSerializable(typeof(PersistentHandoffResponse))]
internal partial class PersistentEndpointJsonContext : JsonSerializerContext;

public static class PersistentPtyEndpoints
{
    public static void MapPersistentPtyEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/experimental");
        routes.MapGet("/persistent-pty/capabilities", (PersistentPtyDaemon daemon) => Results.Ok(new { data = daemon.Deployment }));
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (PtyNotFoundException error)
            { return Results.Json(new { _tag = "PtyNotFoundError", ptyID = error.PtyId, message = error.Message }, statusCode: 404); }
            catch (ArgumentException error)
            { return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or Win32Exception or NotSupportedException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "opencode-pty", message = error.Message }, statusCode: 503); }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "opencode-pty", message = "opencode-pty operation timed out" }, statusCode: 503); }
        });
        routes.MapGet("/session/{sessionID}/terminal", async (string sessionID, PersistentPtyService pty, CancellationToken ct) =>
            Results.Json(new ApiResult<IReadOnlyList<PersistentPtyInfo>>(await pty.ListAsync(SessionId.FromExisting(sessionID), ct)), PersistentEndpointJsonContext.Default.Terminals));
        routes.MapPost("/session/{sessionID}/terminal", async (string sessionID, PersistentPtyCreateInput input, PersistentPtyService pty, CancellationToken ct) =>
            Results.Json(new ApiResult<PersistentPtyInfo>(await pty.CreateAsync(SessionId.FromExisting(sessionID), input, ct)), PersistentEndpointJsonContext.Default.Terminal));
        routes.MapGet("/session/{sessionID}/terminal/read", async (string sessionID, HttpRequest request, PersistentPtyService pty, CancellationToken ct) =>
        {
            var raw = RequestLocation.QueryValue(request, "lines", single: true);
            var lines = raw is null ? null : PtyWebSocket.ParseNumber(raw, 1, 65535);
            if (raw is not null && lines is null)
                return Results.Json(new { _tag = "InvalidRequestError", message = "lines must be an integer between 1 and 65535" }, statusCode: 400);
            return Results.Json(new PersistentReadResponse(await pty.ReadAsync(SessionId.FromExisting(sessionID), (int?)lines, ct)),
                PersistentEndpointJsonContext.Default.PersistentReadResponse);
        });
        routes.MapPost("/persistent-pty/shutdown", async (PersistentPtyService pty, CancellationToken ct) =>
        { await pty.ShutdownAsync(ct); return Results.NoContent(); });
        routes.MapPost("/persistent-pty/handoff", async (PersistentPtyService pty, CancellationToken ct) =>
            Results.Json(new PersistentHandoffResponse(await pty.HandoffAsync(ct)), PersistentEndpointJsonContext.Default.PersistentHandoffResponse));
        routes.MapGet("/persistent-pty/{ptyID}", async (string ptyID, PersistentPtyService pty, CancellationToken ct) =>
            Results.Json(new ApiResult<PersistentPtyInfo>(await pty.GetAsync(PtyId.FromExisting(ptyID), ct)), PersistentEndpointJsonContext.Default.Terminal));
        routes.MapPut("/persistent-pty/{ptyID}", async (string ptyID, PersistentPtyUpdateInput input, PersistentPtyService pty, CancellationToken ct) =>
        {
            var id = PtyId.FromExisting(ptyID);
            await pty.ResizeAsync(id, input.Size, input.AttachmentId, ct);
            return Results.Json(new ApiResult<PersistentPtyInfo>(await pty.GetAsync(id, ct)), PersistentEndpointJsonContext.Default.Terminal);
        });
        routes.MapGet("/persistent-pty/{ptyID}/snapshot", async (string ptyID, PersistentPtyService pty, CancellationToken ct) =>
            Results.Json(new ApiResult<PersistentPtySnapshot>(await pty.SnapshotAsync(PtyId.FromExisting(ptyID), ct)), PersistentEndpointJsonContext.Default.Snapshot));
        routes.MapDelete("/persistent-pty/{ptyID}", async (string ptyID, PersistentPtyService pty, CancellationToken ct) =>
        { await pty.RemoveAsync(PtyId.FromExisting(ptyID), ct); return Results.NoContent(); });
        routes.MapPost("/persistent-pty/{ptyID}/connect-token", async (string ptyID, HttpRequest request, PersistentPtyService pty,
            PtyTickets tickets, PtyRequestPolicy policy, CancellationToken ct) =>
        {
            if (request.Headers["x-opencode-ticket"] != "1" || !policy.IsAllowedOrigin(request))
                return Results.Json(new { _tag = "ForbiddenError", message = "Invalid persistent PTY connect token request" }, statusCode: 403);
            var id = PtyId.FromExisting(ptyID);
            await pty.GetAsync(id, ct);
            return Results.Json(new ApiResult<PtyConnectToken>(tickets.Issue(new(id))), PersistentEndpointJsonContext.Default.Ticket);
        });
        routes.MapGet("/persistent-pty/{ptyID}/connect", async (string ptyID, HttpContext context, PersistentPtyService pty,
            PtyTickets tickets, PtyRequestPolicy policy, IHostApplicationLifetime lifetime) =>
        {
            var id = PtyId.FromExisting(ptyID);
            var ticket = RequestLocation.QueryValue(context.Request, "ticket");
            if (!string.IsNullOrEmpty(ticket) && (!policy.IsAllowedOrigin(context.Request) || !tickets.Consume(ticket, new(id))))
                return Results.StatusCode(403);
            if (!context.WebSockets.IsWebSocketRequest) return Results.StatusCode(400);
            var cursor = RequestLocation.QueryValue(context.Request, "cursor") ?? "0";
            if (PtyWebSocket.ParseNumber(cursor, 0, 9007199254740991) is not { } offset) return Results.StatusCode(400);
            await PersistentPtyWebSocket.RunAsync(context, pty, id, offset, lifetime.ApplicationStopping);
            return Results.Empty;
        });
    }
}
