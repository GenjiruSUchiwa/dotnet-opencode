namespace OpenCode.Client;

using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Native deployment capability only; does not start or probe a daemon.</summary>
    public Task<ApiResult<PersistentPtyDeploymentInfo>> PersistentPtyCapabilitiesAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/experimental/persistent-pty/capabilities", PersistentPtyHttpJsonContext.Default.Deployment, ct);

    public Task<ApiResult<IReadOnlyList<PersistentPtyInfo>>> ListPersistentPtysAsync(SessionId sessionId, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, PersistentSessionPath(sessionId), PersistentPtyHttpJsonContext.Default.Terminals, ct);

    public Task<ApiResult<PersistentPtyInfo>> CreatePersistentPtyAsync(SessionId sessionId, PersistentPtyCreateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RequestAsync(HttpMethod.Post, PersistentSessionPath(sessionId), PersistentPtyHttpJsonContext.Default.Terminal, ct,
            JsonContent.Create(input, PersistentPtyHttpJsonContext.Default.PersistentPtyCreateInput));
    }

    public Task<PersistentPtyReadResponse> ReadPersistentPtyAsync(SessionId sessionId, int? lines = null, CancellationToken ct = default)
    {
        if (lines is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(lines));
        return RequestAsync(HttpMethod.Get, PersistentSessionPath(sessionId) + "/read" + Query(("lines", lines?.ToString(CultureInfo.InvariantCulture))),
            PersistentPtyHttpJsonContext.Default.PersistentPtyReadResponse, ct);
    }

    public Task<ApiResult<PersistentPtyInfo>> GetPersistentPtyAsync(PtyId id, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, PersistentPath(id), PersistentPtyHttpJsonContext.Default.Terminal, ct);

    public Task<ApiResult<PersistentPtyInfo>> UpdatePersistentPtyAsync(PtyId id, PersistentPtyUpdateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RequestAsync(HttpMethod.Put, PersistentPath(id), PersistentPtyHttpJsonContext.Default.Terminal, ct,
            JsonContent.Create(input, PersistentPtyHttpJsonContext.Default.PersistentPtyUpdateInput));
    }

    public Task<ApiResult<PersistentPtySnapshot>> SnapshotPersistentPtyAsync(PtyId id, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, PersistentPath(id) + "/snapshot", PersistentPtyHttpJsonContext.Default.Snapshot, ct);

    public Task RemovePersistentPtyAsync(PtyId id, CancellationToken ct = default) => NoContentAsync(HttpMethod.Delete, PersistentPath(id), ct);
    public Task ShutdownPersistentPtysAsync(CancellationToken ct = default) => NoContentAsync(HttpMethod.Post, "/api/experimental/persistent-pty/shutdown", ct);
    public Task<PersistentPtyHandoffResponse> HandoffPersistentPtysAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, "/api/experimental/persistent-pty/handoff", PersistentPtyHttpJsonContext.Default.PersistentPtyHandoffResponse, ct);

    public Task<ApiResult<PtyConnectToken>> IssuePersistentPtyConnectTokenAsync(PtyId id, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, PersistentPath(id) + "/connect-token", PersistentPtyHttpJsonContext.Default.Ticket, ct,
            configure: request => request.Headers.Add("x-opencode-ticket", "1"));

    /// <summary>Returns the real socket. Binary output is raw terminal bytes; text messages are persistent-PTY control frames.</summary>
    public async Task<ClientWebSocket> ConnectPersistentPtyAsync(PtyId id, string attachmentId, long cursor = 0,
        bool observer = false, bool takeover = false, bool framedInput = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentId);
        if (cursor is < 0 or > 9007199254740991) throw new ArgumentOutOfRangeException(nameof(cursor));
        var ticket = await IssuePersistentPtyConnectTokenAsync(id, ct);
        var path = PersistentPath(id) + "/connect" + Query(("ticket", ticket.Data.Ticket), ("attachment_id", attachmentId),
            ("cursor", cursor.ToString(CultureInfo.InvariantCulture)), ("role", observer ? "observer" : "controller"),
            ("takeover", takeover ? "true" : "false"), ("input_protocol", framedInput ? "1" : "0"));
        var uri = new UriBuilder(new Uri(_origin, path)) { Scheme = _origin.Scheme == "https" ? "wss" : "ws" };
        var socket = new ClientWebSocket();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { await socket.ConnectAsync(uri.Uri, cancellation.Token); return socket; }
        catch { socket.Dispose(); throw; }
    }

    private static string PersistentPath(PtyId id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id.Value);
        return "/api/experimental/persistent-pty/" + Uri.EscapeDataString(id.Value);
    }
    private static string PersistentSessionPath(SessionId id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id.Value);
        return "/api/experimental/session/" + Uri.EscapeDataString(id.Value) + "/terminal";
    }
}

public sealed record PersistentPtyReadResponse([property: JsonPropertyName("data"), JsonRequired] PersistentPtyReadResult? Data);
public sealed record PersistentPtyHandoffResponse([property: JsonPropertyName("handoff"), JsonRequired] PersistentPtyHandoff? Handoff);
public sealed record PersistentPtyDeploymentInfo(string RuntimeIdentifier, bool PlatformArtifactAvailable, bool Packaged,
    bool OverrideConfigured, bool CanAttempt, string? Version, string? Reason, bool AutomaticLaunchSupported);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PersistentPtyCreateInput))]
[JsonSerializable(typeof(PersistentPtyUpdateInput))]
[JsonSerializable(typeof(ApiResult<IReadOnlyList<PersistentPtyInfo>>), TypeInfoPropertyName = "Terminals")]
[JsonSerializable(typeof(ApiResult<PersistentPtyInfo>), TypeInfoPropertyName = "Terminal")]
[JsonSerializable(typeof(ApiResult<PersistentPtySnapshot>), TypeInfoPropertyName = "Snapshot")]
[JsonSerializable(typeof(ApiResult<PtyConnectToken>), TypeInfoPropertyName = "Ticket")]
[JsonSerializable(typeof(PersistentPtyReadResponse))]
[JsonSerializable(typeof(PersistentPtyHandoffResponse))]
[JsonSerializable(typeof(ApiResult<PersistentPtyDeploymentInfo>), TypeInfoPropertyName = "Deployment")]
internal partial class PersistentPtyHttpJsonContext : JsonSerializerContext;
