namespace OpenCode.Client;

using System.Globalization;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task<ApiResult<SessionStatsInfo>> StatsAsync(SessionStatsQuery? query = null, CancellationToken ct = default)
    {
        query ??= new();
        query.Validate();
        var tools = query.Tools switch
        {
            null => null,
            SessionStatsToolMode.None => "none",
            SessionStatsToolMode.Summary => "summary",
            SessionStatsToolMode.Detail => "detail",
            _ => throw new ArgumentException("Unknown stats tools mode.", nameof(query))
        };
        return RequestAsync(HttpMethod.Get, "/api/session/stats" + Query(
            ("from", query.From?.ToString("R", CultureInfo.InvariantCulture)),
            ("to", query.To?.ToString("R", CultureInfo.InvariantCulture)),
            ("project", query.Project?.Value), ("timezone", query.Timezone), ("tools", tools)),
            SessionStatsProtocolJsonContext.Default.StatsResult, ct);
    }
}
