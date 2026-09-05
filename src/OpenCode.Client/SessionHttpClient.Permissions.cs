namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Evaluate/create a permission request; allow/deny decisions need not create a pending request.</summary>
    /// <returns>The actual data envelope containing the request ID and allow/deny/ask decision, not a pending-request substitute.</returns>
    public async Task<ApiResult<PermissionDecisionInfo>> CreatePermissionAsync(SessionId sessionId,
        PermissionCreateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        var result = await RequestAsync(HttpMethod.Post, SessionPath(sessionId) + "/permission",
            PermissionProtocolJsonContext.Default.DecisionResult, ct,
            JsonContent.Create(input, PermissionProtocolJsonContext.Default.PermissionCreateInput)).ConfigureAwait(false);
        if (input.Id is PermissionId requested && result.Data.Id != requested)
            throw Malformed("session.permission.create", "Permission decision does not match the supplied request ID.");
        return result;
    }

    /// <summary>LocationQuery uses location[directory] and location[workspace], not Location.Ref's workspaceID field.</summary>
    /// <returns>The resolved Location and its actual pending-request array in Data. The envelope is not unwrapped.</returns>
    public async Task<LocationResponse<IReadOnlyList<PermissionRequest>>> ListPermissionRequestsAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/permission/request" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            PermissionProtocolJsonContext.Default.LocationRequestsResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(item => item is null))
            throw Malformed("permission.request.list", "Permission request list requires a non-null data array.");
        return result;
    }

    /// <summary>Lists pending requests owned by the supplied Session; this is the dialog's session-scoped ListPending operation.</summary>
    /// <returns>The actual data envelope, preserving server order and every canonical request field.</returns>
    public async Task<ApiResult<IReadOnlyList<PermissionRequest>>> ListSessionPermissionsAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, SessionPath(sessionId) + "/permission",
            PermissionProtocolJsonContext.Default.SessionRequestsResult, ct).ConfigureAwait(false);
        if (result.Data.Any(item => item is null || item.SessionId != sessionId))
            throw Malformed("session.permission.list", "Permission list contains a null or cross-session request.");
        return result;
    }

    /// <summary>Gets an authoritative pending request and checks its Session/request identity.</summary>
    /// <returns>The request in Data; a missing request remains an HTTP error, not null or a fabricated request.</returns>
    public async Task<ApiResult<PermissionRequest>> GetPermissionAsync(SessionId sessionId, PermissionId requestId, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, PermissionPath(sessionId, requestId),
            PermissionProtocolJsonContext.Default.RequestResult, ct).ConfigureAwait(false);
        if (result.Data.SessionId != sessionId || result.Data.Id != requestId)
            throw Malformed("session.permission.get", "Permission request does not match the requested session and ID.");
        return result;
    }

    /// <summary>Send the user's exact reply. Never downgrades always, generates feedback, or retries.</summary>
    /// <param name="sessionId">The pending request's owning Session.</param>
    /// <param name="requestId">The permission request ID, not its tool source/caller ID.</param>
    /// <param name="reply">The reply selected by the user.</param>
    /// <param name="message">User feedback, encoded as message. Null omits the field; empty text is retained.</param>
    /// <param name="ct">Cancels the HTTP operation, not an already accepted server decision.</param>
    /// <returns>Completes only after HTTP 204. It does not return a persisted-grant claim.</returns>
    public Task ReplyPermissionAsync(SessionId sessionId, PermissionId requestId, PermissionReply reply,
        string? message = null, CancellationToken ct = default)
    {
        if (reply is not (PermissionReply.Once or PermissionReply.Always or PermissionReply.Reject))
            throw new ArgumentOutOfRangeException(nameof(reply), "Reply must be once, always, or reject.");
        return NoContentAsync(HttpMethod.Post, PermissionPath(sessionId, requestId) + "/reply", ct,
            JsonContent.Create(new PermissionReplyInput(reply, message), PermissionProtocolJsonContext.Default.PermissionReplyInput));
    }

    /// <summary>Lists actual saved grants when supported; an unavailable backend remains an error.</summary>
    /// <returns>The server's data envelope. Pending requests and memory-only grants are not substituted.</returns>
    public Task<PermissionSavedListResponse> ListSavedPermissionsAsync(ProjectId? projectId = null, CancellationToken ct = default,
        string? directory = null, string? workspace = null)
    {
        if (projectId is ProjectId project) ArgumentNullException.ThrowIfNull(project.Value, nameof(projectId));
        return RequestAsync(HttpMethod.Get, "/api/permission/saved" + Query(("projectID", projectId?.Value),
            ("location[directory]", directory), ("location[workspace]", workspace)),
            PermissionProtocolJsonContext.Default.PermissionSavedListResponse, ct);
    }

    /// <summary>Requests removal of a saved grant, without fallback or retry.</summary>
    /// <returns>Completes only after HTTP 204; unsupported removal is not reported as success.</returns>
    public Task RemoveSavedPermissionAsync(PermissionSavedId id, CancellationToken ct = default)
    {
        // PermissionSaved.ID is an unrestricted string brand, not a required psv_ prefix.
        ArgumentNullException.ThrowIfNull(id.Value, nameof(id));
        return NoContentAsync(HttpMethod.Delete, "/api/permission/saved/" + Uri.EscapeDataString(id.Value), ct);
    }

    private static string PermissionPath(SessionId sessionId, PermissionId requestId)
    {
        if (!requestId.IsInitialized() || !requestId.Value.StartsWith("per", StringComparison.Ordinal))
            throw new ArgumentException("Permission request ID must start with per.", nameof(requestId));
        return SessionPath(sessionId) + "/permission/" + Uri.EscapeDataString(requestId.Value);
    }

    /// <summary>Native host extension; only reports true for the actual shared durable grant store.</summary>
    public Task<ApiResult<PermissionCapabilities>> PermissionCapabilitiesAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/permission/capabilities", PermissionCapabilitiesJsonContext.Default.CapabilitiesResult, ct);
}

public sealed record PermissionCapabilities([property: JsonPropertyName("persistentGrants"), JsonRequired] bool PersistentGrants);

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ApiResult<PermissionCapabilities>), TypeInfoPropertyName = "CapabilitiesResult")]
internal partial class PermissionCapabilitiesJsonContext : JsonSerializerContext;
