namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Database;
using OpenCode.Core.Filesystem;
using OpenCode.Core.Locations;
using OpenCode.Core.Tools;
using OpenCode.Schema;
using Microsoft.AspNetCore.Http.Features;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record FilesystemEntriesResponse(LocationInfo Location, IReadOnlyList<FileSystemEntry> Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FilesystemEntriesResponse))]
[JsonSerializable(typeof(LocationInfo))]
internal partial class FilesystemEndpointJsonContext : JsonSerializerContext;

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        var lifetime = app.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        var searches = new FilesystemLocations(lifetime.ApplicationStopping, app.ServiceProvider.GetRequiredService<TimeProvider>());
        lifetime.ApplicationStopped.Register(() => searches.DisposeAsync().AsTask().GetAwaiter().GetResult());
        var routes = app.MapGroup("/api");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (CatalogLocationUnavailableException error)
            {
                return Results.Json(new { _tag = "ServiceUnavailableError", service = "location", message = error.Message }, statusCode: 503);
            }
            catch (NotSupportedException error)
            {
                return Results.Json(new { _tag = "ServiceUnavailableError", service = "fs", message = error.Message }, statusCode: 503);
            }
            catch (ArgumentException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ToolExecutionException or System.ComponentModel.Win32Exception)
            {
                // Upstream fs.read/list/search defects are server errors, not invented empty lists.
                var reference = "err_" + Guid.NewGuid().ToString("N")[..8];
                invocation.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(LocationEndpoints))
                    .LogError(error, "Filesystem operation failed ({Reference})", reference);
                return Results.Json(new { _tag = "UnknownError", message = "Unexpected server error. Check server logs for details.", @ref = reference }, statusCode: 500);
            }
        });

        routes.MapGet("/location", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
            Results.Json(await RequestLocation.ResolveAsync(request, database, ct), FilesystemEndpointJsonContext.Default.LocationInfo));

        routes.MapGet("/fs/list", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            var entries = new LocalFileSystem(location).List(RequestLocation.QueryValue(request, "path", single: true), ct);
            return Results.Json(new FilesystemEntriesResponse(location, entries), FilesystemEndpointJsonContext.Default.FilesystemEntriesResponse);
        });
        routes.MapGet("/fs/find", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var query = RequestLocation.QueryValue(request, "query", single: true) ?? throw new RequestArgumentException("Query is required.", nameof(request));
            var type = RequestLocation.QueryValue(request, "type", single: true) switch
            {
                null => (FileSystemEntryType?)null,
                "file" => FileSystemEntryType.File,
                "directory" => FileSystemEntryType.Directory,
                _ => throw new RequestArgumentException("Type must be file or directory.", nameof(request))
            };
            var limit = Limit(RequestLocation.QueryValue(request, "limit", single: true));
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            var entries = await searches.Search(location).FindAsync(query, type, limit, ct);
            return Results.Json(new FilesystemEntriesResponse(location, entries), FilesystemEndpointJsonContext.Default.FilesystemEntriesResponse);
        });
        routes.MapGet("/fs/read/{**path}", async (HttpContext context, IDatabase database, CancellationToken ct) =>
        {
            // RawTarget avoids double-decoding percent escapes already decoded by routing.
            var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget
                ?? throw new NotSupportedException("The HTTP server did not supply the raw file request path.");
            var path = raw.Split('?', 2)[0];
            if (!path.StartsWith("/api/fs/read/", StringComparison.Ordinal)) throw new RequestArgumentException("Invalid file request path.", nameof(context));
            var location = await RequestLocation.ResolveAsync(context.Request, database, ct);
            var file = new LocalFileSystem(location).Read(RequestLocation.DecodeDirectory(path[13..], strict: true));
            return Results.Stream(file.Content, file.Mime, enableRangeProcessing: false);
        });
    }

    private static int Limit(string? value)
    {
        if (value is null) return 50;
        value = value.Trim();
        double number;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.StartsWith("0o", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            try { number = Convert.ToUInt64(value[2..], char.ToLowerInvariant(value[1]) is 'x' ? 16 : char.ToLowerInvariant(value[1]) is 'o' ? 8 : 2); }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { throw new RequestArgumentException("Limit must be a positive integer.", nameof(value)); }
        }
        else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new RequestArgumentException("Limit must be a positive integer.", nameof(value));
        if (!double.IsFinite(number) || number < 1 || Math.Truncate(number) != number) throw new RequestArgumentException("Limit must be a positive integer.", nameof(value));
        if (number > int.MaxValue) throw new NotSupportedException("Native filesystem search limits above Int32.MaxValue are not implemented.");
        return (int)number;
    }
}
