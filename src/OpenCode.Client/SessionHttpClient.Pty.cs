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
    public Task<LocationResponse<IReadOnlyList<PtyInfo>>> ListPtysAsync(string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, PtyPath(null, null, directory, workspace), PtyHttpJsonContext.Default.PtysResult, ct);

    public Task<LocationResponse<PtyInfo>> CreatePtyAsync(PtyCreateInput input, string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RequestAsync(HttpMethod.Post, PtyPath(null, null, directory, workspace), PtyHttpJsonContext.Default.PtyResult, ct,
            JsonContent.Create(input, PtyHttpJsonContext.Default.PtyCreateInput));
    }

    public Task<LocationResponse<PtyInfo>> GetPtyAsync(PtyId id, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, PtyPath(id, null, directory, workspace), PtyHttpJsonContext.Default.PtyResult, ct);

    public Task<LocationResponse<PtyInfo>> UpdatePtyAsync(PtyId id, PtyUpdateInput input, string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RequestAsync(HttpMethod.Put, PtyPath(id, null, directory, workspace), PtyHttpJsonContext.Default.PtyResult, ct,
            JsonContent.Create(input, PtyHttpJsonContext.Default.PtyUpdateInput));
    }

    public Task RemovePtyAsync(PtyId id, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, PtyPath(id, null, directory, workspace), ct);

    public Task<LocationResponse<PtyConnectToken>> IssuePtyConnectTokenAsync(PtyId id, string? directory = null,
        string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Post, PtyPath(id, "/connect-token", directory, workspace), PtyHttpJsonContext.Default.PtyTokenResult, ct,
            configure: request => request.Headers.Add("x-opencode-ticket", "1"));

    /// <summary>One explicit attachment; never starts a daemon, creates a terminal, or reconnects automatically.</summary>
    public async Task<PtyConnection> ConnectPtyAsync(PtyId id, long? cursor = null, string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        if (cursor is < -1 or > 9007199254740991) throw new ArgumentOutOfRangeException(nameof(cursor));
        var ticket = await IssuePtyConnectTokenAsync(id, directory, workspace, ct).ConfigureAwait(false);
        var path = "/api/pty/" + Uri.EscapeDataString(id.Value) + "/connect" + Query(
            ("location[directory]", ticket.Location.Directory), ("location[workspace]", ticket.Location.WorkspaceId?.Value),
            ("ticket", ticket.Data.Ticket), ("cursor", cursor?.ToString(CultureInfo.InvariantCulture)));
        var url = new UriBuilder(new Uri(_origin, path)) { Scheme = _origin.Scheme == "https" ? "wss" : "ws" };
        var socket = new ClientWebSocket();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await socket.ConnectAsync(url.Uri, cancellation.Token).ConfigureAwait(false);
            return new PtyConnection(socket, _lifetime.Token);
        }
        catch { socket.Dispose(); throw; }
    }

    private static string PtyPath(PtyId? id, string? operation, string? directory, string? workspace)
    {
        if (id is { } value) ArgumentException.ThrowIfNullOrEmpty(value.Value, nameof(id));
        return "/api/pty" + (id is { } terminal ? "/" + Uri.EscapeDataString(terminal.Value) : "") + operation
            + Query(("location[directory]", directory), ("location[workspace]", workspace));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(PtyCreateInput))]
[JsonSerializable(typeof(PtyUpdateInput))]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<PtyInfo>>), TypeInfoPropertyName = "PtysResult")]
[JsonSerializable(typeof(LocationResponse<PtyInfo>), TypeInfoPropertyName = "PtyResult")]
[JsonSerializable(typeof(LocationResponse<PtyConnectToken>), TypeInfoPropertyName = "PtyTokenResult")]
[JsonSerializable(typeof(PtyCursorFrame))]
internal partial class PtyHttpJsonContext : JsonSerializerContext;
