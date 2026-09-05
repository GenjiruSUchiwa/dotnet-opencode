namespace OpenCode.Client;

using System.Net.Http.Json;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Copies projected history at the requested boundary. A lost response is not automatically retried.</summary>
    public Task<ApiResult<SessionInfo>> ForkAsync(SessionId sessionId, ForkRequestBoundary boundary,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        if (boundary is ForkRequestBoundaryBefore before)
            ArgumentException.ThrowIfNullOrEmpty(before.MessageId.Value, nameof(boundary));
        return RequestAsync(HttpMethod.Post, SessionPath(sessionId) + "/fork",
            SessionProtocolJsonContext.Default.SessionResult, ct,
            JsonContent.Create(new SessionForkInput(boundary), SessionForkProtocolJsonContext.Default.SessionForkInput));
    }
}
