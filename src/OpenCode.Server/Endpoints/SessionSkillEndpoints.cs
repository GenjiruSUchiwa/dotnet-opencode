namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCode.Core.Database;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Skills;
using OpenCode.Schema;
using OpenCode.Server.Services;

public static class SessionSkillEndpoints
{
    /// <summary>Requires the Core/Event owner's actual ISessionSkillPublisher. Resolution is lazy; no fake publisher is registered.</summary>
    public static IServiceCollection AddSessionSkillServices(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => new SessionSkillService(
            provider.GetRequiredService<SessionStore>(),
            provider.GetService<ISessionSkillPublisher>() ?? throw new NotSupportedException("Standalone skill activation requires the canonical Session skill publisher."),
            provider.GetRequiredService<SessionExecutionEngine>(),
            provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        return services;
    }

    public static void MapSessionSkillEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/session/{sessionID}/skill", async (string sessionID, HttpRequest request, SessionStore sessions,
            SessionExecutionService execution, IServiceProvider services, CancellationToken ct) =>
        {
            if (request.Query.Keys.Any(key => key != "auth_token"))
                throw new ArgumentException("session.skill derives placement from the stored Session and has no Location/model/delivery query.");
            var input = await request.ReadFromJsonAsync(SessionSkillJsonContext.Default.SessionSkillRequest, ct)
                ?? throw new ArgumentException("A skill activation payload is required.");
            var id = SessionId.FromExisting(sessionID);
            _ = await sessions.GetSessionAsync(id, ct) ?? throw new SessionMutationNotFoundException(id);
            // Recording must work even for resume:false or an unavailable model. Do not turn this
            // into a model-readiness check before the skill fact can be committed.
            execution.RequireRecordingReady();
            var skills = services.GetService<SessionSkillService>()
                ?? throw new NotSupportedException("Standalone skill activation is not configured by this host.");
            await skills.ActivateAsync(id, input, ct);
            return Results.NoContent();
        }).AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SessionMutationNotFoundException error)
            { return Results.Json(new { _tag = "SessionNotFoundError", sessionID = error.SessionId.Value, message = error.Message }, statusCode: 404); }
            catch (SessionSkillNotFoundException error)
            { return Results.Json(new { _tag = "SkillNotFoundError", skill = error.Skill.Value, message = error.Message }, statusCode: 404); }
            catch (NotSupportedException error)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "skill", message = error.Message }, statusCode: 503); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "skill", message = "The registered skill source could not be read." }, statusCode: 503); }
            catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
            { return Results.Json(new { _tag = "InvalidRequestError", message = "Invalid skill activation request." }, statusCode: 400); }
            // Source duplicate-event publication is a failure, not an idempotent success or inbox
            // retry. Do not catch it and return 204, or schedule resume after failed publication.
        });
    }
}
