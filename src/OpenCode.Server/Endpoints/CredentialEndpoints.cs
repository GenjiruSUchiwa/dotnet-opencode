namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Integrations;
using OpenCode.Core.Locations;
using OpenCode.Core.Tools;
using OpenCode.Schema;
using OpenCode.Server.Integrations;

internal sealed record CredentialLabelPayload(
    [property: JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Label);

[JsonSerializable(typeof(CredentialLabelPayload))]
internal partial class CredentialEndpointJsonContext : JsonSerializerContext;

/// <summary>Canonical credential protocol mutations. Read projections belong to integration.list/get; secrets are never returned.</summary>
public static class CredentialEndpoints
{
    public static void MapCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/credential");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
            catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
            { return Results.Json(new { _tag = "InvalidRequestError", message = "Invalid credential request." }, statusCode: 400); }
            catch (IOException) { return Unavailable("The channel credential store is unavailable."); }
        });

        routes.MapPatch("/{credentialID}", (string credentialID, HttpRequest request, CancellationToken ct) =>
            MutateAsync(request, credentialID, async (runtime, id, token) =>
            {
                var input = await request.ReadFromJsonAsync(CredentialEndpointJsonContext.Default.CredentialLabelPayload, token)
                    ?? throw new ArgumentException("A label payload is required.");
                await runtime.UpdateCredentialAsync(id, input.Label, token);
            }, ct));
        routes.MapPost("/{credentialID}/activate", (string credentialID, HttpRequest request, CancellationToken ct) =>
            MutateAsync(request, credentialID, (runtime, id, token) => runtime.ActivateCredentialAsync(id, token), ct));
        routes.MapDelete("/{credentialID}", (string credentialID, HttpRequest request, CancellationToken ct) =>
            MutateAsync(request, credentialID, (runtime, id, token) => runtime.RemoveCredentialAsync(id, token), ct));
    }

    private static async Task<IResult> MutateAsync(HttpRequest request, string id,
        Func<IntegrationRuntime, CredentialId, CancellationToken, Task> mutate, CancellationToken ct)
    {
        var services = request.HttpContext.RequestServices;
        var info = await FeatureEndpoints.ResolveLocationAsync(request, services.GetRequiredService<IDatabase>(), ct);
        await using var location = await services.GetRequiredService<IntegrationHostService>().AcquireAsync(new(info.Directory, info.WorkspaceId),
            services.GetRequiredService<ToolLocationFactory>(), services.GetRequiredService<PermissionLocationMap>(), observe: false, ct: ct);
        await mutate(location.Runtime, CredentialId.FromExisting(id), ct);
        return Results.NoContent();
    }

    private static IResult Unavailable(string message) => Results.Json(new { _tag = "ServiceUnavailableError", service = "credential", message }, statusCode: 503);
}
