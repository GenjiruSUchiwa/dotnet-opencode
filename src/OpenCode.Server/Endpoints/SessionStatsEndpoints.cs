namespace OpenCode.Server.Endpoints;

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using OpenCode.Core.Session.Statistics;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public static class SessionStatsEndpoints
{
    /// <summary>Composition owner registers SessionStatistics and mounts this under
    /// the existing authenticated API. It is independent of Skill and Job routes.</summary>
    public static void MapSessionStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/session/stats", async (HttpRequest request, [FromServices] SessionStatistics statistics, CancellationToken ct) =>
        {
            try
            {
                var project = RequestLocation.QueryValue(request, "project");
                var tools = RequestLocation.QueryValue(request, "tools") switch
                {
                    null or "summary" => SessionStatsToolMode.Summary,
                    "none" => SessionStatsToolMode.None,
                    "detail" => SessionStatsToolMode.Detail,
                    _ => throw new ArgumentException("tools must be none, summary, or detail.")
                };
                var data = await statistics.GetAsync(new(Number(request, "from"), Number(request, "to"),
                    project is null ? null : ProjectId.FromExisting(project), RequestLocation.QueryValue(request, "timezone"), tools), ct);
                return Results.Json(new ApiResult<SessionStatsInfo>(data), SessionStatsProtocolJsonContext.Default.StatsResult);
            }
            catch (ArgumentException error)
            {
                return Results.Json(new { _tag = "InvalidRequestError", message = error.Message }, statusCode: 400);
            }
            // SQL, JSON, database readiness and IO failures deliberately propagate
            // to the host error boundary; they are not an empty statistics result.
        });
    }

    private static double? Number(HttpRequest request, string name)
    {
        var value = RequestLocation.QueryValue(request, name);
        if (value is null) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new ArgumentException($"{name} must be a finite number.");
        return number;
    }
}
