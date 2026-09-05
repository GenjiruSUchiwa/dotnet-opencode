namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Protocol.Errors;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Services;

public sealed record PermissionReplyApiRequest(
    [property: JsonPropertyName("reply"), JsonRequired] string Reply,
    [property: JsonPropertyName("message")] string? Message = null);
public sealed record PermissionCreateApiRequest(
    [property: JsonPropertyName("action"), JsonRequired] string Action,
    [property: JsonPropertyName("resources"), JsonRequired] IReadOnlyList<string> Resources,
    [property: JsonPropertyName("id")] PermissionId? Id = null,
    [property: JsonPropertyName("agent")] AgentId? Agent = null,
    [property: JsonPropertyName("save")] IReadOnlyList<string>? Save = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    [property: JsonPropertyName("source")] PermissionSource? Source = null);
internal sealed record PermissionLocationResponse(LocationInfo Location, IReadOnlyList<PermissionRequest> Data);
internal sealed record SessionPermissionsResponse(IReadOnlyList<PermissionRequest> Data);
internal sealed record SessionPermissionResponse(PermissionRequest Data);
internal sealed record PermissionDecisionResponse(PermissionDecision Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PermissionLocationResponse))]
[JsonSerializable(typeof(SessionPermissionsResponse))]
[JsonSerializable(typeof(SessionPermissionResponse))]
[JsonSerializable(typeof(PermissionDecisionResponse))]
internal partial class PermissionEndpointJsonContext : JsonSerializerContext;

public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SessionNotFoundException error)
            {
                return Results.Json(new { _tag = "SessionNotFoundError", sessionID = error.SessionId, message = $"Session not found: {error.SessionId}" }, statusCode: 404);
            }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
            catch (ObjectDisposedException) { return Unavailable("The permission Location is shutting down."); }
            catch (PermissionLocationMismatchException)
            {
                return Unavailable("The Session Location changed; resolve its permission service again.");
            }
            catch (ArgumentException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400);
            }
        });

        routes.MapGet("/permission/request", async (HttpRequest request, IServiceProvider services, CancellationToken ct) =>
        {
            foreach (var pair in request.Query)
                if (pair.Key != "auth_token" && (pair.Key is not ("location[directory]" or "location[workspace]") || pair.Value.Count != 1))
                    throw new RequestArgumentException("Use a single location[directory] and optional location[workspace].", nameof(request));
            await using var location = await Locations(services).AcquireAsync(
                request.Query.TryGetValue("location[directory]", out var directory) ? directory[0] : null,
                request.Query.TryGetValue("location[workspace]", out var workspace) ? workspace[0] : null, ct);
            return Results.Json(new PermissionLocationResponse(location.Location, await location.Permissions.ListAsync(ct: ct)),
                PermissionEndpointJsonContext.Default.PermissionLocationResponse);
        });

        routes.MapGet("/session/{sessionID}/permission", async (string sessionID, SessionStore store,
            IServiceProvider services, CancellationToken ct) =>
        {
            var session = await SessionAsync(store, sessionID, ct);
            await using var location = await Locations(services).TryAcquireLoadedAsync(session.Location, ct);
            // Only an authoritative unloaded Location may produce an empty pending list.
            return Results.Json(new SessionPermissionsResponse(location is null ? [] : await location.Permissions.ListAsync(session.Id, ct)),
                PermissionEndpointJsonContext.Default.SessionPermissionsResponse);
        });

        routes.MapGet("/session/{sessionID}/permission/{requestID}", async (string sessionID, string requestID,
            SessionStore store, IServiceProvider services, CancellationToken ct) =>
        {
            var session = await SessionAsync(store, sessionID, ct);
            var id = RequestId(requestID);
            await using var location = await Locations(services).AcquireAsync(session.Location, ct);
            var pending = await location.Permissions.GetAsync(id, ct);
            return pending is null || pending.SessionId != session.Id ? Missing(id)
                : Results.Json(new SessionPermissionResponse(pending), PermissionEndpointJsonContext.Default.SessionPermissionResponse);
        });

        routes.MapPost("/session/{sessionID}/permission/{requestID}/reply", async (string sessionID, string requestID,
            PermissionReplyApiRequest input, SessionStore store, IServiceProvider services, CancellationToken ct) =>
        {
            var session = await SessionAsync(store, sessionID, ct);
            var id = RequestId(requestID);
            var reply = input.Reply switch
            {
                "once" => PermissionReply.Once,
                "always" => PermissionReply.Always,
                "reject" => PermissionReply.Reject,
                _ => throw new RequestArgumentException("Reply must be once, always, or reject.", nameof(input))
            };
            await using var location = await Locations(services).AcquireAsync(session.Location, ct);
            var pending = await location.Permissions.GetAsync(id, ct);
            if (pending is null || pending.SessionId != session.Id) return Missing(id);
            if (reply == PermissionReply.Always && pending.Save is { Count: > 0 } && !location.Permissions.PersistentGrants)
                return Unavailable("Persistent permission grants are not implemented for this Location; use once or reject.");
            try { await location.Permissions.ReplyAsync(id, session.Id, reply, input.Message, ct); }
            catch (KeyNotFoundException) { return Missing(id); }
            return Results.NoContent();
        });

        routes.MapPost("/session/{sessionID}/permission", async (string sessionID, PermissionCreateApiRequest input,
            SessionStore store, IServiceProvider services, CancellationToken ct) =>
        {
            var session = await SessionAsync(store, sessionID, ct);
            await using var location = await Locations(services).AcquireAsync(session.Location, ct);
            try
            {
                var decision = await location.Permissions.AskAsync(new PermissionAskInput(session.Id, input.Action,
                    input.Resources, input.Id, input.Agent, input.Save, input.Metadata, input.Source), ct);
                return Results.Json(new PermissionDecisionResponse(decision), PermissionEndpointJsonContext.Default.PermissionDecisionResponse);
            }
            catch (PermissionSessionNotFoundException) { throw new SessionNotFoundException(sessionID); }
        });
        routes.MapGet("/permission/capabilities", (IServiceProvider services) => Results.Ok(new
        {
            data = new { persistentGrants = services.GetService<IPermissionGrantStore>() is IPermissionSavedStore { Persistent: true } }
        }));
        routes.MapGet("/permission/saved", async (HttpRequest request, IDatabase database, IServiceProvider services, CancellationToken ct) =>
        {
            var saved = services.GetService<IPermissionSavedStore>() ?? throw new NotSupportedException("Durable saved-permission storage is not configured.");
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            var project = RequestLocation.QueryValue(request, "projectID", single: true);
            return Results.Json(new PermissionSavedListResponse(await saved.ListSavedAsync(project is null ? location.Project.Id : ProjectId.FromExisting(project), ct)),
                PermissionProtocolJsonContext.Default.PermissionSavedListResponse);
        });
        routes.MapDelete("/permission/saved/{id}", async (string id, HttpRequest request, IDatabase database, IServiceProvider services, CancellationToken ct) =>
        {
            var saved = services.GetService<IPermissionSavedStore>() ?? throw new NotSupportedException("Durable saved-permission storage is not configured.");
            await RequestLocation.ResolveAsync(request, database, ct);
            await saved.RemoveAsync(PermissionSavedId.FromExisting(id), ct);
            return Results.NoContent();
        });
    }

    private static IPermissionLocationServices Locations(IServiceProvider services) =>
        services.GetService<IPermissionLocationServices>() ?? throw new NotSupportedException(
            "Location-scoped permission services and live Session/Agent rules are not configured.");

    private static async Task<SessionInfo> SessionAsync(SessionStore store, string id, CancellationToken ct) =>
        await store.GetSessionAsync(SessionId.FromExisting(id), ct) ?? throw new SessionNotFoundException(id);

    private static PermissionId RequestId(string id) => id.StartsWith("per", StringComparison.Ordinal)
        ? PermissionId.FromExisting(id) : throw new RequestArgumentException("Permission ID must start with per.", nameof(id));

    private static IResult Missing(PermissionId id) => Results.Json(new
    {
        _tag = "PermissionNotFoundError", requestID = id.Value, message = $"Permission request not found: {id.Value}"
    }, statusCode: 404);

    private static IResult Unavailable(string message) => Results.Json(new
    {
        _tag = "ServiceUnavailableError", service = "permission", message
    }, statusCode: 503);
}
