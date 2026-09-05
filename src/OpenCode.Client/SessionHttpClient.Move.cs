namespace OpenCode.Client;

using System.Net.Http.Json;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Admits a location move. Completion does not mean the queued control has been delivered.</summary>
    public Task MoveAsync(SessionId sessionId, LocationRef location, InboxDeliveryMode? delivery = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(location.Directory);
        return NoContentAsync(HttpMethod.Post, SessionPath(sessionId) + "/move", ct,
            JsonContent.Create(new SessionMoveInput(location.Directory, location.WorkspaceId, delivery),
                SessionMoveProtocolJsonContext.Default.SessionMoveInput));
    }
}
