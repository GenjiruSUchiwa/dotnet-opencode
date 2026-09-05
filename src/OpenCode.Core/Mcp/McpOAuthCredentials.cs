namespace OpenCode.Core.Mcp;

using System.Text.Json;
using ModelContextProtocol.Authentication;
using OpenCode.Core.Database;
using OpenCode.Schema;

/// <summary>Host-owned, channel-isolated credentials. Implementations must support concurrent SDK reads.</summary>
public interface IMcpOAuthCredentials
{
    Task<CredentialInfo?> SelectedAsync(string integrationId, CancellationToken ct);
    Task<CredentialInfo?> GetAsync(CredentialId id, CancellationToken ct);
    Task<CredentialId> CreateAsync(string integrationId, CredentialOAuth value, string? label, CancellationToken ct);
    Task<bool> UpdateAsync(CredentialId id, CredentialOAuth value, CancellationToken ct);
    Task<bool> RemoveAsync(CredentialId id, string? expectedRefresh, CancellationToken ct);
}

/// <summary>
/// Adapts an already constructed channel CredentialStore. Never opens a database or chooses a path.
/// The host publishes the returned store notifications after commit; the callback must enqueue
/// reconnect work rather than synchronously reenter an MCP runtime.
/// </summary>
public sealed class McpOAuthCredentialStore(CredentialStore store, Func<CredentialMutation, CancellationToken, Task> publish) : IMcpOAuthCredentials
{
    public async Task<CredentialInfo?> SelectedAsync(string integrationId, CancellationToken ct) =>
        Convert(await store.GetActiveCredentialAsync(integrationId, ct).ConfigureAwait(true));

    public async Task<CredentialInfo?> GetAsync(CredentialId id, CancellationToken ct) =>
        Convert(await store.GetCredentialAsync(id.Value, ct).ConfigureAwait(true));

    public async Task<CredentialId> CreateAsync(string integrationId, CredentialOAuth value, string? label, CancellationToken ct)
    {
        var mutation = await store.CreateAsync(integrationId, JsonSerializer.SerializeToElement(value, OpenCodeJsonContext.Default.CredentialValue), label, ct).ConfigureAwait(true);
        await publish(mutation, CancellationToken.None).ConfigureAwait(true);
        return CredentialId.FromExisting(mutation.Credential?.Id ?? throw new InvalidOperationException("MCP credential creation did not return a credential."));
    }

    public async Task<bool> UpdateAsync(CredentialId id, CredentialOAuth value, CancellationToken ct)
    {
        var mutation = await store.UpdateAsync(id.Value, value: JsonSerializer.SerializeToElement(value, OpenCodeJsonContext.Default.CredentialValue), ct: ct).ConfigureAwait(true);
        await publish(mutation, CancellationToken.None).ConfigureAwait(true);
        return mutation.Credential is not null;
    }

    public async Task<bool> RemoveAsync(CredentialId id, string? expectedRefresh, CancellationToken ct)
    {
        var current = await GetAsync(id, ct).ConfigureAwait(true);
        if (current is null || expectedRefresh is not null && current.Value is not CredentialOAuth) return false;
        if (expectedRefresh is not null && ((CredentialOAuth)current.Value).Refresh != expectedRefresh) return false;
        var mutation = await store.RemoveAsync(id.Value, ct).ConfigureAwait(true);
        await publish(mutation, CancellationToken.None).ConfigureAwait(true);
        return mutation.Notifications.Length != 0;
    }

    private static CredentialInfo? Convert(StoredCredential? value) => value is null ? null : new(
        CredentialId.FromExisting(value.Id), value.IntegrationId, value.Label,
        JsonSerializer.Deserialize(value.ValueJson, OpenCodeJsonContext.Default.CredentialValue)
            ?? throw new InvalidDataException("Stored MCP credential is invalid."));
}

internal sealed class McpOAuthTokenCache(IMcpOAuthCredentials credentials, CredentialId id, string integrationId, string serverUrl, TimeProvider clock) : ITokenCache
{
    private string? _presentedRefresh;

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken).ConfigureAwait(true);
        if (current is null) return null;
        Volatile.Write(ref _presentedRefresh, current.Refresh);
        return McpOAuthService.ToTokens(current, clock);
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        if (tokens is null)
        {
            // The expected refresh token prevents dropping a newer rotated credential when the
            // host supports this conditional removal. It is not a clustered refresh lock.
            if (Volatile.Read(ref _presentedRefresh) is { } refresh)
                await credentials.RemoveAsync(id, refresh, cancellationToken).ConfigureAwait(true);
            return;
        }
        if (await ReadAsync(cancellationToken).ConfigureAwait(true) is null)
            throw new McpOAuthRequiredException();
        var value = McpOAuthService.ToCredential(integrationId, serverUrl, tokens);
        if (!await credentials.UpdateAsync(id, value, cancellationToken).ConfigureAwait(true)) throw new McpOAuthRequiredException();
        Volatile.Write(ref _presentedRefresh, value.Refresh);
    }

    private async Task<CredentialOAuth?> ReadAsync(CancellationToken ct)
    {
        var current = await credentials.GetAsync(id, ct).ConfigureAwait(true);
        if (current is null) return null;
        if (current.IntegrationId != integrationId || current.Value is not CredentialOAuth oauth || oauth.MethodId != integrationId)
            throw new McpOAuthRequiredException();
        return oauth;
    }
}
