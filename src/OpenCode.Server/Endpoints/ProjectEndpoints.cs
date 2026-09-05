namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Projects;
using OpenCode.Schema;

public sealed record ProjectUpdateApiRequest(
    [property: JsonPropertyName("name"), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null,
    [property: JsonPropertyName("icon"), JsonConverter(typeof(NonNullPromptJsonConverter<ProjectIcon>))] ProjectIcon? Icon = null,
    [property: JsonPropertyName("commands"), JsonConverter(typeof(NonNullPromptJsonConverter<ProjectCommands>))] ProjectCommands? Commands = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IReadOnlyList<ProjectInfo>))]
[JsonSerializable(typeof(LocationProjectInfo))]
[JsonSerializable(typeof(ProjectInfo))]
internal partial class ProjectEndpointJsonContext : JsonSerializerContext;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/project");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ProjectNotFoundException error)
            { return Results.Json(new { _tag = "ProjectNotFoundError", projectID = error.ProjectId, message = error.Message }, statusCode: 404); }
            catch (CatalogLocationUnavailableException error)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "project", message = error.Message }, statusCode: 503); }
            catch (ArgumentException error)
            { return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400); }
            catch (Exception error) when (error is SqliteException or JsonException or IOException or UnauthorizedAccessException)
            {
                var reference = "err_" + Guid.NewGuid().ToString("N")[..8];
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ProjectEndpoints))
                    .LogError(error, "Project read failed ({Reference})", reference);
                return Results.Json(new { _tag = "UnknownError", message = "Unexpected server error. Check server logs for details.", @ref = reference }, statusCode: 500);
            }
        });
        routes.MapGet("", async (ProjectQueries projects, CancellationToken ct) =>
            Results.Json(await projects.ListAsync(ct), ProjectEndpointJsonContext.Default.IReadOnlyListProjectInfo));
        routes.MapGet("/current", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
            Results.Json((await RequestLocation.ResolveAsync(request, database, ct)).Project, ProjectEndpointJsonContext.Default.LocationProjectInfo));
        routes.MapPatch("/{projectID}", async (string projectID, ProjectUpdateApiRequest input, ProjectMutations projects, CancellationToken ct) =>
            Results.Json(await projects.UpdateAsync(ProjectId.FromExisting(projectID), input.Name, input.Icon, input.Commands, ct), ProjectEndpointJsonContext.Default.ProjectInfo));
    }
}
