namespace OpenCode.Server.Endpoints;

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Core.Shell;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Server.Shell;

internal sealed record SessionShellPayload(
    [property: JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Command,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<EventId>))] EventId? Id = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<ShellInfo>>), TypeInfoPropertyName = "ListResult")]
[JsonSerializable(typeof(LocationResponse<ShellInfo>), TypeInfoPropertyName = "InfoResult")]
[JsonSerializable(typeof(LocationResponse<ShellOutput>), TypeInfoPropertyName = "OutputResult")]
[JsonSerializable(typeof(ShellCreateInput))]
[JsonSerializable(typeof(ShellTimeoutInput))]
[JsonSerializable(typeof(SessionShellPayload))]
internal partial class ShellEndpointJsonContext : JsonSerializerContext;

public static class ShellEndpoints
{
    /// <summary>Mount under the host's existing authentication middleware. No anonymous or alternate shell route is installed.</summary>
    public static void MapShellEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/shell");
        routes.AddEndpointFilter(FilterAsync);
        routes.MapGet("", (HttpRequest request, CancellationToken ct) => UseAsync(request, false, async (runtime, location, token) =>
            Results.Json(new LocationResponse<IReadOnlyList<ShellInfo>>(location, await runtime.ListAsync(token)), ShellEndpointJsonContext.Default.ListResult), ct));

        routes.MapPost("", (HttpRequest request, CancellationToken ct) => UseAsync(request, false, async (runtime, location, token) =>
        {
            var input = await request.ReadFromJsonAsync(ShellEndpointJsonContext.Default.ShellCreateInput, token)
                ?? throw new ArgumentException("A shell command payload is required.");
            return Results.Json(new LocationResponse<ShellInfo>(location, await runtime.CreateAsync(input, token)), ShellEndpointJsonContext.Default.InfoResult);
        }, ct));

        routes.MapGet("/{id}", (string id, HttpRequest request, CancellationToken ct) => UseAsync(request, false, async (runtime, location, token) =>
            Results.Json(new LocationResponse<ShellInfo>(location, await runtime.GetAsync(ShellId.FromExisting(id), token)), ShellEndpointJsonContext.Default.InfoResult), ct));

        routes.MapPatch("/{id}/timeout", (string id, HttpRequest request, CancellationToken ct) => UseAsync(request, false, async (runtime, location, token) =>
        {
            var input = await request.ReadFromJsonAsync(ShellEndpointJsonContext.Default.ShellTimeoutInput, token)
                ?? throw new ArgumentException("A shell timeout payload is required.");
            return Results.Json(new LocationResponse<ShellInfo>(location, await runtime.TimeoutAsync(ShellId.FromExisting(id), input.Timeout, token)), ShellEndpointJsonContext.Default.InfoResult);
        }, ct));

        routes.MapGet("/{id}/output", (string id, HttpRequest request, CancellationToken ct) => UseAsync(request, true, async (runtime, location, token) =>
            Results.Json(new LocationResponse<ShellOutput>(location, await runtime.OutputAsync(ShellId.FromExisting(id),
                new ShellOutputInput(Number(request, "cursor"), Number(request, "limit")), token)), ShellEndpointJsonContext.Default.OutputResult), ct));

        routes.MapDelete("/{id}", (string id, HttpRequest request, CancellationToken ct) => UseAsync(request, false, async (runtime, _, token) =>
        {
            await runtime.RemoveAsync(ShellId.FromExisting(id), token);
            return Results.NoContent();
        }, ct));

        app.MapPost("/api/session/{sessionID}/shell", async (string sessionID, HttpRequest request, SessionShellHostService host, CancellationToken ct) =>
        {
            if (request.Query.Keys.Any(key => key != "auth_token")) throw new ArgumentException("session.shell derives its Location from the Session.");
            var input = await request.ReadFromJsonAsync(ShellEndpointJsonContext.Default.SessionShellPayload, ct)
                ?? throw new ArgumentException("A session shell payload is required.");
            await host.RunAsync(SessionId.FromExisting(sessionID), input.Command, input.Id, ct);
            return Results.NoContent();
        }).AddEndpointFilter(FilterAsync);
    }

    private static async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ShellNotFoundException error) { return Results.Json(new { _tag = "ShellNotFoundError", id = error.Id.Value, message = error.Message }, statusCode: 404); }
        catch (SessionMutationNotFoundException error) { return Results.Json(new { _tag = "SessionNotFoundError", sessionID = error.SessionId.Value, message = error.Message }, statusCode: 404); }
        catch (NotSupportedException error) { return Unavailable(error.Message); }
        catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
        catch (ShellOutputUnavailableException error) { return Unavailable(error.Message); }
        catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
        { return Results.Json(new { _tag = "InvalidRequestError", message = "Invalid shell request." }, statusCode: 400); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { return Unavailable("The shell command or capture is unavailable."); }
        catch (OperationCanceledException) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
        { return Unavailable("The shell Location was closed."); }
    }

    private static async Task<IResult> UseAsync(HttpRequest request, bool output,
        Func<ShellRuntime, LocationInfo, CancellationToken, Task<IResult>> action, CancellationToken ct)
    {
        foreach (var pair in request.Query)
        {
            if (pair.Key == "auth_token") continue;
            if (pair.Value.Count != 1 || pair.Key is not ("location[directory]" or "location[workspace]") && !(output && pair.Key is ("cursor" or "limit")))
                throw new ArgumentException("Invalid shell query.");
        }
        var services = request.HttpContext.RequestServices;
        var info = await CatalogLocation.ResolveAsync(services.GetRequiredService<IDatabase>(),
            request.Query.TryGetValue("location[directory]", out var directory) ? directory[0] : null,
            request.Query.TryGetValue("location[workspace]", out var workspace) ? workspace[0] : null, ct);
        await using var lease = await services.GetRequiredService<ShellLocationServices>().AcquireAsync(new(info.Directory, info.WorkspaceId), ct);
        return await action(lease.Shell, lease.Location, ct);
    }

    private static double? Number(HttpRequest request, string name)
    {
        if (!request.Query.TryGetValue(name, out var value)) return null;
        return double.TryParse(value[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) && number >= 0 && Math.Truncate(number) == number ? number : throw new ArgumentException("Expected a nonnegative integer shell cursor/limit.");
    }
    private static IResult Unavailable(string message) => Results.Json(new { _tag = "ServiceUnavailableError", service = "shell", message }, statusCode: 503);
}
