namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Vcs;
using OpenCode.Protocol.Groups;
using OpenCode.Protocol;
using OpenCode.Schema;
using OpenCode.Server.Pty;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LocationResponse<VcsInfo>), TypeInfoPropertyName = "Info")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<VcsFileStatus>>), TypeInfoPropertyName = "Status")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<string>>), TypeInfoPropertyName = "Branches")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<FileDiffInfo>>), TypeInfoPropertyName = "Diff")]
internal partial class VcsEndpointJsonContext : JsonSerializerContext;

public static class VcsEndpoints
{
    public static void MapVcsEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/vcs");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException error)
            { return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400); }
            catch (JsonException)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "vcs", message = "VCS configuration could not be read." }, statusCode: 503); }
            catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException)
            { return Results.Json(new { _tag = "ServiceUnavailableError", service = "vcs", message = error.Message }, statusCode: 503); }
        });
        routes.MapGet("", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            var vcs = await LocalVcs.OpenAsync(location, ct: ct);
            return Results.Json(new LocationResponse<VcsInfo>(location, await vcs.InfoAsync(ct)), VcsEndpointJsonContext.Default.Info);
        });
        routes.MapGet("/status", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            var vcs = await LocalVcs.OpenAsync(location, ct: ct);
            return Results.Json(new LocationResponse<IReadOnlyList<VcsFileStatus>>(location, await vcs.StatusAsync(ct)), VcsEndpointJsonContext.Default.Status);
        });
        routes.MapGet("/base", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            await FlushAsync(location, request, ct);
            var vcs = await LocalVcs.OpenAsync(location, ct: ct);
            return Results.Json(new LocationResponse<VcsBase?>(location, await vcs.BaseAsync(ct)), VcsProtocolJsonContext.Default.BaseResult);
        });
        routes.MapGet("/branches", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var limit = RequestLocation.QueryValue(request, "limit", single: true);
            var parsed = limit is null ? 50 : PtyWebSocket.ParseNumber(limit, 1, 9007199254740991) ?? throw new RequestArgumentException("Branch limit must be a positive integer.", nameof(request));
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            var vcs = await LocalVcs.OpenAsync(location, ct: ct);
            return Results.Json(new LocationResponse<IReadOnlyList<string>>(location,
                await vcs.BranchesAsync(RequestLocation.QueryValue(request, "search", single: true), (int)Math.Min(parsed, 100), ct)), VcsEndpointJsonContext.Default.Branches);
        });
        routes.MapGet("/diff", async (HttpRequest request, IDatabase database, CancellationToken ct) =>
        {
            var mode = RequestLocation.QueryValue(request, "mode", single: true);
            if (mode is not ("working" or "branch" or "committed")) throw new RequestArgumentException("VCS mode must be working, branch, or committed.", nameof(request));
            var raw = RequestLocation.QueryValue(request, "context", single: true);
            var context = raw is null ? LocalVcs.PatchContext : PtyWebSocket.ParseNumber(raw, 0, int.MaxValue)
                ?? throw new RequestArgumentException("Patch context must be a supported nonnegative integer.", nameof(request));
            var location = await RequestLocation.ResolveAsync(request, database, ct);
            await FlushAsync(location, request, ct);
            var vcs = await LocalVcs.OpenAsync(location, ct: ct);
            return Results.Json(new LocationResponse<IReadOnlyList<FileDiffInfo>>(location,
                await vcs.DiffAsync(mode switch { "working" => VcsDiffMode.Working, "branch" => VcsDiffMode.Branch, _ => VcsDiffMode.Committed },
                    RequestLocation.QueryValue(request, "base", single: true), (int)context, ct)), VcsEndpointJsonContext.Default.Diff);
        });
    }

    private static async Task FlushAsync(LocationInfo location, HttpRequest request, CancellationToken ct)
    {
        using var readiness = request.HttpContext.RequestServices.GetRequiredService<TimeProvider>().CreateLinkedCancellationTokenSource(ct);
        readiness.CancelAfter(TimeSpan.FromSeconds(5));
        try { await ServerHost.FlushNativeLocationAsync(location, request.HttpContext.RequestServices, readiness.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new VcsUnavailableException("VCS initialization timed out"); }
    }
}
