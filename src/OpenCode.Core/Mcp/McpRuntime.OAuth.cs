namespace OpenCode.Core.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

public sealed record McpOAuthRegistration(string Server, string IntegrationId, Uri? RedirectUri, int? CallbackPort);

public sealed partial class McpRuntime
{
    /// <summary>Host integration declarations from effective runtime definitions; contains no headers/client secrets.</summary>
    public async Task<IReadOnlyList<McpOAuthRegistration>> OAuthRegistrationsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_oauth is null) return [];
            return _entries.Where(item => item.Value.Config is McpRemoteConfig { OAuth: not McpOAuthDisabled })
                .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
                {
                    var remote = (McpRemoteConfig)item.Value.Config;
                    var settings = remote.OAuth as McpOAuthConfig;
                    return new McpOAuthRegistration(item.Key, McpOAuthService.IntegrationId(item.Key, remote.Url),
                        settings?.RedirectUri is { } redirect ? new Uri(redirect) : null,
                        settings?.CallbackPort is { } port ? checked((int)port) : null);
                }).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>The host supplies an already bound callback URI and retains the returned attempt.</summary>
    public async Task<McpOAuthAuthorization> StartAuthorizationAsync(string server, Uri redirectUri, CancellationToken ct = default)
    {
        var config = await OAuthConfigAsync(server, ct);
        return await _oauth!.StartAsync(server, config, redirectUri, ct, _shutdown.Token);
    }

    /// <summary>Returns only the persisted credential ID, never access/refresh tokens. Switched events trigger reconnect.</summary>
    public async Task<CredentialId> CompleteAuthorizationAsync(McpOAuthAuthorization attempt, string code, string state,
        string? issuer = null, string? label = null, CancellationToken ct = default)
    {
        try
        {
            var config = await OAuthConfigAsync(attempt.Server, ct);
            if (config.Url != attempt.Config.Url || !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(config.OAuth), JsonSerializer.SerializeToNode(attempt.Config.OAuth)))
                throw new McpOAuthException("MCP OAuth configuration changed; start a new authorization attempt.");
            return await _oauth!.CompleteAsync(attempt, code, state, issuer, label, ct);
        }
        finally { await attempt.DisposeAsync(); }
    }

    public ValueTask CancelAuthorizationAsync(McpOAuthAuthorization attempt) =>
        (_oauth ?? throw new NotSupportedException("The host has not supplied MCP OAuth persistence.")).CancelAsync(attempt);

    public async Task<bool> RevokeAuthorizationAsync(string server, CancellationToken ct = default) =>
        await (_oauth ?? throw new NotSupportedException("The host has not supplied MCP OAuth persistence."))
            .RevokeAsync(server, await OAuthConfigAsync(server, ct), ct);

    /// <summary>
    /// Host hook for committed CredentialNotification.Switched events. Enqueue this work; do not
    /// await it inside a credential store callback that may have been invoked by this runtime.
    /// </summary>
    public Task<McpObservation> CredentialChangedAsync(string integrationId, CancellationToken ct = default) =>
        UpdateAsync(async token =>
        {
            if (_oauth is null) return;
            foreach (var item in _entries)
            {
                if (item.Value.Status is McpDisabledStatus || item.Value.Config is not McpRemoteConfig remote
                    || remote.OAuth is McpOAuthDisabled || McpOAuthService.IntegrationId(item.Key, remote.Url) != integrationId) continue;
                await StopAsync(item.Key, item.Value);
                await StartAsync(item.Key, item.Value, token, force: true);
            }
        }, ct);

    private async Task<McpRemoteConfig> OAuthConfigAsync(string server, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var entry = RequireServer(server);
            if (_oauth is null) throw new NotSupportedException("The host has not supplied MCP OAuth persistence.");
            if (entry.Config is not McpRemoteConfig remote || remote.OAuth is McpOAuthDisabled)
                throw new NotSupportedException("OAuth is not enabled for this MCP server.");
            return remote;
        }
        finally { _gate.Release(); }
    }

    private McpServer ServerInfo(string name, Entry entry) => new(name, entry.Status,
        _oauth is not null && entry.Config is McpRemoteConfig { OAuth: not McpOAuthDisabled } remote
            ? IntegrationId.FromExisting(McpOAuthService.IntegrationId(name, remote.Url)) : null);
}
