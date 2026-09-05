namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Core.Locations;
using OpenCode.Core.Tools;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<SkillInfo>>), TypeInfoPropertyName = "SkillListResult")]
internal partial class SkillEndpointJsonContext : JsonSerializerContext;

public static class SkillEndpoints
{
    /// <summary>Replace FeatureEndpoints' older /skill mapping before mounting this group. Never mount both.</summary>
    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/skill", async (HttpRequest request, IDatabase database, ToolLocationFactory factory,
            PermissionLocationMap locations, CancellationToken ct) =>
        {
            var info = await FeatureEndpoints.ResolveLocationAsync(request, database, ct);
            await using var lease = await factory.AcquireAsync(locations, new LocationRef(info.Directory, info.WorkspaceId), ct);
            // Reuse instruction/prompt discovery and precedence. Do not start MCP connections,
            // read an arbitrary supplied skill path, or silently omit configured plugin sources.
            var skills = await InstructionCatalog.ListSkillsAsync(lease.Location.Directory, ct);
            return Results.Json(new LocationResponse<IReadOnlyList<SkillInfo>>(lease.Location, skills), SkillEndpointJsonContext.Default.SkillListResult);
        }).AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (NotSupportedException error) { return Unavailable(error.Message); }
            catch (CatalogLocationUnavailableException error) { return Unavailable(error.Message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { return Unavailable("The complete skill catalog could not be read from its configured sources."); }
            catch (ArgumentException error)
            { return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400); }
        });
    }

    private static IResult Unavailable(string message) => Results.Json(new { _tag = "ServiceUnavailableError", service = "skill", message }, statusCode: 503);
}
