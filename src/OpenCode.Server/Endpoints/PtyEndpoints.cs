namespace OpenCode.Server.Endpoints;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Locations;
using OpenCode.Core.Pty;
using OpenCode.Schema;
using OpenCode.Server.Pty;

internal sealed record PtyListResponse(LocationInfo Location, IReadOnlyList<PtyInfo> Data);
internal sealed record PtyResponse(LocationInfo Location, PtyInfo Data);
internal sealed record PtyTicketResponse(LocationInfo Location, PtyConnectToken Data);
internal sealed record PtyError(string _tag, string Message, PtyId? PtyID = null, string? Service = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PtyListResponse))]
[JsonSerializable(typeof(PtyResponse))]
[JsonSerializable(typeof(PtyTicketResponse))]
[JsonSerializable(typeof(PtyError))]
internal partial class PtyEndpointJsonContext : JsonSerializerContext;

public static class PtyEndpoints
{
    public static void MapPtyEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/pty");
        routes.RequireCors("opencode-pty");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                var host = Host(invocation.HttpContext);
                if (!PtyRequestPolicy.HasPtyConnectTicketURL(invocation.HttpContext.Request))
                    host.Policy.RequireAuthentication(invocation.HttpContext.Request);
                return await next(invocation);
            }
            catch (PtyAuthenticationException)
            {
                invocation.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"opencode-dotnet\", charset=\"UTF-8\"";
                return Error(401, "UnauthorizedError", "Server authentication is required.");
            }
            catch (PtyForbiddenException error)
            {
                return invocation.HttpContext.Request.Path.Value?.EndsWith("/connect", StringComparison.Ordinal) == true
                    ? Results.StatusCode(403) : Error(403, "ForbiddenError", error.Message);
            }
            catch (PtyNotFoundException error)
            {
                return PtyRequestPolicy.HasPtyConnectTicketURL(invocation.HttpContext.Request)
                    || invocation.HttpContext.Request.Path.Value?.EndsWith("/connect", StringComparison.Ordinal) == true
                    ? Results.StatusCode(404) : Error(404, "PtyNotFoundError", error.Message, error.PtyId);
            }
            catch (JsonException error) { return Error(400, "InvalidRequestError", error.Message); }
            catch (ArgumentException error) { return Error(400, "InvalidRequestError", error.Message); }
            catch (CatalogLocationUnavailableException error) { return Error(503, "ServiceUnavailableError", error.Message, service: "pty"); }
            catch (NotSupportedException error) { return Error(503, "ServiceUnavailableError", error.Message, service: "pty"); }
            catch (ObjectDisposedException) { return Error(503, "ServiceUnavailableError", "The PTY Location is closing.", service: "pty"); }
            catch (Win32Exception error) { return Error(503, "ServiceUnavailableError", error.Message, service: "pty"); }
        });

        routes.MapGet("", async (HttpContext context) =>
        {
            var scope = await Host(context).ResolveAsync(context.Request, context.RequestAborted);
            return Results.Json(new PtyListResponse(scope.Location, scope.Pty.List()), PtyEndpointJsonContext.Default.PtyListResponse);
        });
        routes.MapPost("", async (HttpContext context) =>
        {
            var host = Host(context);
            var input = await context.Request.ReadFromJsonAsync(OpenCodeJsonContext.Default.PtyCreateInput, context.RequestAborted)
                ?? throw new RequestArgumentException("PTY create input is required.", nameof(context));
            var scope = await host.ResolveAsync(context.Request, context.RequestAborted);
            return Results.Json(new PtyResponse(scope.Location, await host.Integration(scope).CreateAsync(context, input)), PtyEndpointJsonContext.Default.PtyResponse);
        });
        routes.MapGet("/{ptyID}", async (HttpContext context, string ptyID) =>
        {
            var scope = await Host(context).ResolveAsync(context.Request, context.RequestAborted);
            return Results.Json(new PtyResponse(scope.Location, scope.Pty.Get(PtyId.FromExisting(ptyID))), PtyEndpointJsonContext.Default.PtyResponse);
        });
        routes.MapPut("/{ptyID}", async (HttpContext context, string ptyID) =>
        {
            var input = await context.Request.ReadFromJsonAsync(OpenCodeJsonContext.Default.PtyUpdateInput, context.RequestAborted)
                ?? throw new RequestArgumentException("PTY update input is required.", nameof(context));
            var scope = await Host(context).ResolveAsync(context.Request, context.RequestAborted);
            return Results.Json(new PtyResponse(scope.Location, scope.Pty.Update(PtyId.FromExisting(ptyID), input)), PtyEndpointJsonContext.Default.PtyResponse);
        });
        routes.MapDelete("/{ptyID}", async (HttpContext context, string ptyID) =>
        {
            var scope = await Host(context).ResolveAsync(context.Request, context.RequestAborted);
            await scope.Pty.RemoveAsync(PtyId.FromExisting(ptyID));
            return Results.NoContent();
        });
        routes.MapPost("/{ptyID}/connect-token", async (HttpContext context, string ptyID) =>
        {
            var host = Host(context);
            var scope = await host.ResolveAsync(context.Request, context.RequestAborted);
            return Results.Json(new PtyTicketResponse(scope.Location, await host.Integration(scope).IssueTicketAsync(context, PtyId.FromExisting(ptyID))), PtyEndpointJsonContext.Default.PtyTicketResponse);
        });
        routes.MapGet("/{ptyID}/connect", async (HttpContext context, string ptyID, IHostApplicationLifetime lifetime) =>
        {
            var host = Host(context);
            var scope = await host.ResolveAsync(context.Request, context.RequestAborted);
            var id = PtyId.FromExisting(ptyID);
            await host.Integration(scope).AuthorizeConnectAsync(context, id);
            if (!context.WebSockets.IsWebSocketRequest) return Results.StatusCode(400);
            await PtyWebSocket.RunAsync(context, scope.Pty, id, lifetime.ApplicationStopping);
            return Results.Empty;
        });
    }

    private static PtyHostServices Host(HttpContext context) => context.RequestServices.GetService<PtyHostServices>()
        ?? throw new NotSupportedException("Location-scoped PTY services have not been registered by the host.");

    private static IResult Error(int status, string tag, string message, PtyId? id = null, string? service = null) =>
        Results.Json(new PtyError(tag, message, id, service), PtyEndpointJsonContext.Default.PtyError, statusCode: status);
}
