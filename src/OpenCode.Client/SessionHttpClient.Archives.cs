namespace OpenCode.Client;

using System.Net.Http.Json;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Projected transcript only. Sanitization matches source and is not a complete secret scrubber.</summary>
    public Task<ApiResult<SessionTransferData>> ExportAsync(SessionId sessionId, bool? sanitize = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/export" + Query(("sanitize", sanitize is null ? null : sanitize.Value ? "true" : "false")),
            SessionArchiveProtocolJsonContext.Default.ArchiveResult, ct);

    /// <summary>Never automatically retries: an existing Session ID conflicts, even for identical data.</summary>
    public Task<ApiResult<SessionInfo>> ImportAsync(SessionTransferData data, LocationRef? location = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Info);
        ArgumentNullException.ThrowIfNull(data.Messages);
        return RequestAsync(HttpMethod.Post, "/api/session/import", SessionProtocolJsonContext.Default.SessionResult, ct,
            JsonContent.Create(new SessionArchiveImportInput(data.Info, data.Messages, location), SessionArchiveProtocolJsonContext.Default.SessionArchiveImportInput));
    }
}
