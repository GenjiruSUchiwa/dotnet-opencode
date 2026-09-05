namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Schema;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IReadOnlyList<ConfigEntry>))]
internal partial class ConfigEndpointJsonContext : JsonSerializerContext;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", async (HttpRequest request, IDatabase database, ILoggerFactory logging, CancellationToken ct) =>
        {
            var logger = logging.CreateLogger(nameof(ConfigEndpoints));
            try
            {
                var location = await RequestLocation.ResolveAsync(request, database, ct);
                var snapshot = await ConfigLoader.LoadSnapshotAsync(location.Directory, ct);
                var entries = snapshot.Entries(diagnostic => logger.LogWarning(
                    "Configuration normalization diagnostic: {Kind} {Path} {Action}", diagnostic.Kind, diagnostic.Path, diagnostic.Message));
                // The source response is a bare Config.Entry[], not { location, data }.
                return Results.Json(entries, ConfigEndpointJsonContext.Default.IReadOnlyListConfigEntry);
            }
            catch (CatalogLocationUnavailableException error)
            {
                return Results.Json(new { _tag = "ServiceUnavailableError", service = "location", message = error.Message }, statusCode: 503);
            }
            catch (NotSupportedException error)
            {
                return Results.Json(new { _tag = "ServiceUnavailableError", service = "config", message = error.Message }, statusCode: 503);
            }
            catch (ArgumentException)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = "Invalid configuration Location." }, statusCode: 400);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                var reference = "err_" + Guid.NewGuid().ToString("N")[..8];
                // Parser/substitution exceptions can contain configuration values. Log identity only.
                logger.LogError("Configuration catalog could not be loaded ({Reference}, {ErrorType})", reference, error.GetType().Name);
                return Results.Json(new { _tag = "UnknownError", message = "Unexpected server error. Check server logs for details.", @ref = reference }, statusCode: 500);
            }
        });
    }
}
