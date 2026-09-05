namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Integrations;
using OpenCode.Core.Llm;
using OpenCode.Core.Locations;
using OpenCode.Core.Mcp;
using OpenCode.Core.Tools;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Integrations;

internal sealed record IntegrationGetResponse(
    [property: JsonPropertyName("location"), JsonRequired] LocationInfo Location,
    [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IntegrationInfo? Data = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<IntegrationInfo>>), TypeInfoPropertyName = "ListResult")]
[JsonSerializable(typeof(IntegrationGetResponse))]
[JsonSerializable(typeof(LocationResponse<IntegrationAttempt>), TypeInfoPropertyName = "AttemptResult")]
[JsonSerializable(typeof(LocationResponse<IntegrationAttemptStatus>), TypeInfoPropertyName = "StatusResult")]
[JsonSerializable(typeof(IntegrationKeyConnectPayload))]
[JsonSerializable(typeof(IntegrationOAuthConnectPayload))]
[JsonSerializable(typeof(IntegrationOAuthCompletePayload))]
[JsonSerializable(typeof(IntegrationCommandConnectPayload))]
[JsonSerializable(typeof(LocationResponse<IntegrationCommandAttempt>), TypeInfoPropertyName = "CommandResult")]
[JsonSerializable(typeof(LocationResponse<IntegrationCommandAttemptStatus>), TypeInfoPropertyName = "CommandStatusResult")]
internal partial class IntegrationEndpointJsonContext : JsonSerializerContext;

public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/integration");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (IntegrationAuthorizationException error) { return Invalid(error.Message, error.Kind); }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
            catch (Exception error) when (error is McpOAuthException or McpOAuthRequiredException or LlmException or HttpRequestException or IOException)
            { return Invalid("Authentication or integration discovery failed.", "integration_authorization"); }
            catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
            { return Invalid("Invalid integration request.", "integration_authorization"); }
            catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
            { return Invalid("Authentication expired or was cancelled.", "integration_authorization"); }
        });

        routes.MapGet("", (HttpRequest request, CancellationToken ct) => UseAsync(request, true, async (runtime, location, token) =>
            Results.Json(new LocationResponse<IReadOnlyList<IntegrationInfo>>(location, await runtime.ListAsync(token)), IntegrationEndpointJsonContext.Default.ListResult), ct));

        routes.MapGet("/{integrationID}", (string integrationID, HttpRequest request, CancellationToken ct) => UseAsync(request, true, async (runtime, location, token) =>
            Results.Json(new IntegrationGetResponse(location, await runtime.GetAsync(integrationID, token)), IntegrationEndpointJsonContext.Default.IntegrationGetResponse), ct));

        routes.MapPost("/{integrationID}/connect/key", (string integrationID, HttpRequest request, CancellationToken ct) => UseAsync(request, true, async (runtime, _, token) =>
        {
            var input = await request.ReadFromJsonAsync(IntegrationEndpointJsonContext.Default.IntegrationKeyConnectPayload, token)
                ?? throw new IntegrationAuthorizationException("A key payload is required.");
            await runtime.ConnectKeyAsync(integrationID, input, token);
            return Results.NoContent();
        }, ct));

        routes.MapPost("/{integrationID}/connect/oauth", (string integrationID, HttpRequest request, CancellationToken ct) => UseAsync(request, true, async (runtime, location, token) =>
        {
            var input = await request.ReadFromJsonAsync(IntegrationEndpointJsonContext.Default.IntegrationOAuthConnectPayload, token)
                ?? throw new IntegrationAuthorizationException("An OAuth payload is required.");
            return Results.Json(new LocationResponse<IntegrationAttempt>(location, await runtime.ConnectOAuthAsync(integrationID, input, token)),
                IntegrationEndpointJsonContext.Default.AttemptResult);
        }, ct));

        routes.MapGet("/{integrationID}/connect/oauth/{attemptID}", (string integrationID, string attemptID, HttpRequest request, CancellationToken ct) =>
            UseAsync(request, false, (runtime, location, _) => Task.FromResult<IResult>(Results.Json(
                new LocationResponse<IntegrationAttemptStatus>(location, runtime.Status(integrationID, IntegrationAttemptId.FromExisting(attemptID))),
                IntegrationEndpointJsonContext.Default.StatusResult)), ct));

        routes.MapPost("/{integrationID}/connect/oauth/{attemptID}/complete", (string integrationID, string attemptID, HttpRequest request, CancellationToken ct) =>
            UseAsync(request, false, async (runtime, _, token) =>
            {
                var input = await request.ReadFromJsonAsync(IntegrationEndpointJsonContext.Default.IntegrationOAuthCompletePayload, token)
                    ?? throw new IntegrationAuthorizationException("An OAuth completion payload is required.");
                await runtime.CompleteAsync(integrationID, IntegrationAttemptId.FromExisting(attemptID), input.Code, token);
                return Results.NoContent();
            }, ct));

        routes.MapDelete("/{integrationID}/connect/oauth/{attemptID}", (string integrationID, string attemptID, HttpRequest request, CancellationToken ct) =>
            UseAsync(request, false, async (runtime, _, _) =>
            {
                await runtime.CancelAsync(integrationID, IntegrationAttemptId.FromExisting(attemptID));
                return Results.NoContent();
            }, ct));

        routes.MapPost("/{integrationID}/connect/command", (string integrationID, HttpRequest request, CancellationToken ct) =>
            UseAsync(request, true, async (runtime, location, token) =>
            {
                var input = await request.ReadFromJsonAsync(IntegrationEndpointJsonContext.Default.IntegrationCommandConnectPayload, token)
                    ?? throw new IntegrationAuthorizationException("A command payload is required.");
                return Results.Json(new LocationResponse<IntegrationCommandAttempt>(location, await runtime.ConnectCommandAsync(integrationID, input, token)),
                    IntegrationEndpointJsonContext.Default.CommandResult);
            }, ct));
        routes.MapGet("/{integrationID}/connect/command/{attemptID}", (string integrationID, string attemptID, HttpRequest request, CancellationToken ct) =>
            UseAsync(request, false, (runtime, location, _) => Task.FromResult<IResult>(Results.Json(
                new LocationResponse<IntegrationCommandAttemptStatus>(location, runtime.CommandStatus(integrationID, IntegrationAttemptId.FromExisting(attemptID))),
                IntegrationEndpointJsonContext.Default.CommandStatusResult)), ct));
        routes.MapDelete("/{integrationID}/connect/command/{attemptID}", (string integrationID, string attemptID, HttpRequest request, CancellationToken ct) =>
            UseAsync(request, false, async (runtime, _, _) =>
            {
                await runtime.CancelCommandAsync(integrationID, IntegrationAttemptId.FromExisting(attemptID));
                return Results.NoContent();
            }, ct));
        app.MapPost("/api/experimental/integration/wellknown", () => Unavailable("Wellknown integration discovery is not implemented."));
    }

    private static async Task<IResult> UseAsync(HttpRequest request, bool observe,
        Func<IntegrationRuntime, LocationInfo, CancellationToken, Task<IResult>> action, CancellationToken ct)
    {
        var services = request.HttpContext.RequestServices;
        var location = await FeatureEndpoints.ResolveLocationAsync(request, services.GetRequiredService<IDatabase>(), ct);
        await using var lease = await services.GetRequiredService<IntegrationHostService>().AcquireAsync(new(location.Directory, location.WorkspaceId),
            services.GetRequiredService<ToolLocationFactory>(), services.GetRequiredService<PermissionLocationMap>(), observe, ct);
        return await action(lease.Runtime, lease.Location, ct);
    }

    private static IResult Invalid(string message, string kind) => Results.Json(new { _tag = "InvalidRequestError", message, kind }, statusCode: 400);
    private static IResult Unavailable(string message) => Results.Json(new { _tag = "ServiceUnavailableError", service = "integration", message }, statusCode: 503);
}
