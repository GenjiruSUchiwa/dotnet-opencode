namespace OpenCode.Core.Session;

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using OpenCode.Schema;

/// <summary>Actual embedding-host metadata, not inferred provider/model identity or a fabricated product version.</summary>
public sealed record SessionRequestIdentity(string ClientName, string UserAgent)
{
    internal static string PromptCacheKey(SessionInfo session)
    {
        // Source uses the immediate fork lineage, not an inferred root ancestor.
        var lineage = (session.Fork?.SessionId ?? session.Id).Value;
        return Regex.IsMatch(lineage, "^ses_[0-9a-f]{64}$", RegexOptions.NonBacktracking) ? lineage[4..] : lineage;
    }

    internal static ImmutableDictionary<string, string> Headers(SessionInfo session, IReadOnlyDictionary<string, string> configured,
        SessionRequestIdentity? identity)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-session-affinity"] = session.Id.Value, ["X-Session-Id"] = session.Id.Value,
            ["x-opencode-project"] = session.ProjectId.Value, ["x-opencode-session"] = session.Id.Value
        };
        if (session.ParentId is { } parent) headers["x-parent-session-id"] = parent.Value;
        if (identity is not null)
        {
            headers["User-Agent"] = identity.UserAgent;
            headers["x-opencode-client"] = identity.ClientName;
        }
        foreach (var header in configured) headers[header.Key] = header.Value;
        return headers.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
