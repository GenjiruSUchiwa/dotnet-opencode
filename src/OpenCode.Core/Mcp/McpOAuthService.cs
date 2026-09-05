namespace OpenCode.Core.Mcp;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using OpenCode.Schema;

public sealed class McpOAuthRequiredException() : Exception("MCP server requires authentication.");
public sealed class McpOAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Host-retained authorization attempt, not a wire DTO. SDK state/PKCE and temporary tokens remain
/// in memory. The host owns the callback listener and disposes this object on cancellation/removal.
/// </summary>
public sealed class McpOAuthAuthorization : IAsyncDisposable
{
    internal readonly TaskCompletionSource<AuthorizationCallbackContext> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<AuthorizationResult> Callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<TokenContainer> Tokens = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly CancellationTokenSource Lifetime;
    internal readonly CancellationToken Cancellation;
    internal readonly McpOAuthService Owner;
    internal readonly McpRemoteConfig Config;
    internal Task Driver = Task.CompletedTask;
    internal int Completing;
    private readonly Lock _gate = new();
    private Task? _disposal;

    internal McpOAuthAuthorization(McpOAuthService owner, string server, McpRemoteConfig config, Uri redirect, CancellationToken lifetime)
    {
        Owner = owner;
        Server = server;
        Config = config;
        RedirectUri = redirect;
        IntegrationId = McpOAuthService.IntegrationId(server, config.Url);
        ExpiresAt = owner.Clock.GetUtcNow().AddMinutes(10);
        Lifetime = owner.Clock.CreateLinkedCancellationTokenSource(lifetime);
        Lifetime.CancelAfter(TimeSpan.FromMinutes(10));
        Cancellation = Lifetime.Token;
    }

    public string Server { get; }
    public string IntegrationId { get; }
    public Uri RedirectUri { get; }
    public DateTimeOffset ExpiresAt { get; }
    public Uri AuthorizationUri => Ready.Task.IsCompletedSuccessfully ? Ready.Task.Result.AuthorizationUri
        : throw new InvalidOperationException("MCP authorization URL is not available.");

    internal async Task<AuthorizationResult> RedirectAsync(AuthorizationCallbackContext context, CancellationToken ct)
    {
        if (!Ready.TrySetResult(context)) throw new McpOAuthException("MCP requested another authorization flow; start a new attempt.");
        return await Callback.Task.WaitAsync(ct);
    }

    internal void Fail(Exception error)
    {
        if (error is OperationCanceledException)
        {
            Ready.TrySetCanceled(Cancellation);
            Tokens.TrySetCanceled(Cancellation);
            return;
        }
        Ready.TrySetException(error);
        Tokens.TrySetException(error);
        // Either Start or Complete observes these tasks; a failed Start must also observe Tokens.
        _ = Tokens.Task.Exception;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        await Lifetime.CancelAsync();
        await Driver;
        Lifetime.Dispose();
    }
}

/// <summary>Official SDK OAuth orchestration over host-owned channel persistence. Construction does no I/O.</summary>
public sealed class McpOAuthService(IMcpOAuthCredentials credentials, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public static string IntegrationId(string server, string originalUrl) => "mcp_" +
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(server + "\0" + originalUrl))).ToLowerInvariant()[..16];

    /// <summary>
    /// The host must bind its callback listener first and supply the actual redirect URI. This method
    /// performs discovery/registration and returns only when the SDK has produced an authorization URL.
    /// It never starts a browser or listener and never persists a credential at the start boundary.
    /// </summary>
    public async Task<McpOAuthAuthorization> StartAsync(string server, McpRemoteConfig config, Uri redirectUri,
        CancellationToken ct = default, CancellationToken lifetime = default)
    {
        RequireOAuth(config);
        if (!redirectUri.IsAbsoluteUri || redirectUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("MCP OAuth requires an absolute HTTP(S) callback URI.", nameof(redirectUri));
        if (config.OAuth is McpOAuthConfig oauth)
        {
            if (oauth.RedirectUri is { } configured && new Uri(configured) != redirectUri)
                throw new ArgumentException("The bound MCP callback URI must match redirect_uri.", nameof(redirectUri));
            if (oauth.CallbackPort is > 0 && redirectUri.Port != oauth.CallbackPort)
                throw new ArgumentException("The bound MCP callback port must match callback_port.", nameof(redirectUri));
        }
        ct.ThrowIfCancellationRequested();
        var attempt = new McpOAuthAuthorization(this, server, config, redirectUri, lifetime);
        attempt.Driver = AuthorizeAsync(attempt);
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Cancellation);
            await attempt.Ready.Task.WaitAsync(wait.Token);
            return attempt;
        }
        catch { await attempt.DisposeAsync(); throw; }
    }

    /// <summary>Pass code, state, and optional iss from the real callback. The SDK validates state/issuer before token exchange.</summary>
    public async Task<CredentialId> CompleteAsync(McpOAuthAuthorization attempt, string code, string state,
        string? issuer = null, string? label = null, CancellationToken ct = default)
    {
        RequireOwner(attempt);
        if (Interlocked.Exchange(ref attempt.Completing, 1) != 0) throw new McpOAuthException("MCP authorization attempt has already been claimed.");
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Cancellation);
            linked.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) throw new McpOAuthException("MCP callback code and state are required.");
            attempt.Callback.TrySetResult(new AuthorizationResult { Code = code, State = state, Iss = issuer });
            var tokens = await attempt.Tokens.Task.WaitAsync(linked.Token);
            var value = ToCredential(attempt.IntegrationId, attempt.Config.Url, tokens);
            linked.Token.ThrowIfCancellationRequested();
            return await credentials.CreateAsync(attempt.IntegrationId, value, label, linked.Token);
        }
        finally { await attempt.DisposeAsync(); }
    }

    public ValueTask CancelAsync(McpOAuthAuthorization attempt)
    {
        RequireOwner(attempt);
        return attempt.DisposeAsync();
    }

    /// <summary>Forget only the selected local OAuth credential. This does not claim remote RFC 7009 revocation.</summary>
    public async Task<bool> RevokeAsync(string server, McpRemoteConfig config, CancellationToken ct = default)
    {
        RequireOAuth(config);
        var identity = IntegrationId(server, config.Url);
        var selected = await credentials.SelectedAsync(identity, ct);
        if (selected is null) return false;
        if (selected.IntegrationId != identity || selected.Value is not CredentialOAuth oauth || oauth.MethodId != identity)
            throw new McpOAuthException("The selected credential is not an OAuth credential for this MCP integration.");
        return await credentials.RemoveAsync(selected.Id, null, ct);
    }

    internal async Task<ClientOAuthOptions?> ConnectionOptionsAsync(string server, McpRemoteConfig config, CancellationToken ct)
    {
        if (config.OAuth is McpOAuthDisabled) return null;
        RequireOAuth(config);
        var identity = IntegrationId(server, config.Url);
        var selected = await credentials.SelectedAsync(identity, ct);
        var cache = selected is { Value: CredentialOAuth oauth } && selected.IntegrationId == identity && oauth.MethodId == identity
            ? new McpOAuthTokenCache(credentials, selected.Id, identity, config.Url, Clock) : null;
        var options = Options(config, new Uri((config.OAuth as McpOAuthConfig)?.RedirectUri ?? "http://127.0.0.1/callback"), cache);
        // A background connection may refresh existing tokens but must never prompt/open a browser.
        options.AuthorizationCallbackHandler = (_, _) => throw new McpOAuthRequiredException();
        return options;
    }

    private static async Task AuthorizeAsync(McpOAuthAuthorization attempt)
    {
        try
        {
            var options = Options(attempt.Config, attempt.RedirectUri, new AuthorizationCache(attempt));
            options.AuthorizationCallbackHandler = async (context, token) => await attempt.RedirectAsync(context, token);
            // The public .NET SDK exposes OAuth through the HTTP transport, not TS auth(). A
            // temporary handshake obtains the real challenge; no fabricated 401 or endpoint is used.
            await using var transport = new HttpClientTransport(new()
            {
                Name = attempt.Server, Endpoint = new Uri(attempt.Config.Url), TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = attempt.Config.Headers?.ToDictionary(item => item.Key, item => item.Value),
                OAuth = options
            });
            await using var client = await McpClient.CreateAsync(transport, new()
            {
                ClientInfo = new() { Name = "opencode", Version = "dotnet" }, InitializationTimeout = TimeSpan.FromMinutes(10)
            }, cancellationToken: attempt.Cancellation);
            if (!attempt.Tokens.Task.IsCompletedSuccessfully)
                throw new McpOAuthException("The MCP endpoint did not request OAuth authorization.");
        }
        catch (OperationCanceledException error) { attempt.Fail(error); }
        catch (Exception error) { attempt.Fail(new McpOAuthException("MCP OAuth authorization failed.", error)); }
    }

    private static ClientOAuthOptions Options(McpRemoteConfig config, Uri redirect, ITokenCache? cache)
    {
        var oauth = config.OAuth as McpOAuthConfig;
        return new()
        {
            RedirectUri = redirect, ClientId = string.IsNullOrEmpty(oauth?.ClientId) ? null : oauth.ClientId,
            ClientSecret = string.IsNullOrEmpty(oauth?.ClientId) ? null : oauth.ClientSecret,
            Scopes = oauth?.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries), TokenCache = cache,
            ScopeSelector = oauth?.Scope is { Length: > 0 } scope ? _ => scope.Split(' ', StringSplitOptions.RemoveEmptyEntries) : null,
            DynamicClientRegistration = new() { ClientName = "opencode", ClientUri = new Uri("https://opencode.ai") }
        };
    }

    private static void RequireOAuth(McpRemoteConfig config)
    {
        if (config.OAuth is McpOAuthDisabled) throw new NotSupportedException("OAuth is disabled for this MCP server.");
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("MCP OAuth requires an absolute HTTP(S) server URL.");
    }

    private void RequireOwner(McpOAuthAuthorization attempt)
    {
        if (!ReferenceEquals(attempt.Owner, this)) throw new ArgumentException("MCP authorization belongs to another host.", nameof(attempt));
    }

    internal static CredentialOAuth ToCredential(string methodId, string serverUrl, TokenContainer tokens)
    {
        if (string.IsNullOrEmpty(tokens.AccessToken)) throw new McpOAuthException("MCP OAuth did not return an access token.");
        var metadata = new JsonObject { ["serverUrl"] = serverUrl, ["tokenType"] = tokens.TokenType ?? "Bearer" };
        if (!string.IsNullOrEmpty(tokens.Scope)) metadata["scope"] = tokens.Scope;
        if (tokens.ClientId is { } client)
            metadata["client"] = new JsonObject
            {
                ["client_id"] = client, ["client_secret"] = tokens.ClientSecret,
                ["token_endpoint_auth_method"] = tokens.TokenEndpointAuthMethod
            };
        // The .NET SDK binds restored DCR credentials to this issuer. Keep this SDK-specific
        // metadata alongside the source's client information, never in MCP config or API replies.
        if (tokens.AuthorizationServer is { } issuer) metadata["authorizationServer"] = JsonSerializer.SerializeToNode(issuer);
        return new(methodId, tokens.RefreshToken ?? "", tokens.AccessToken,
            tokens.ExpiresIn is > 0 ? tokens.ObtainedAt.AddSeconds(tokens.ExpiresIn.Value).ToUnixTimeMilliseconds() : 0,
            metadata.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal));
    }

    internal static TokenContainer ToTokens(CredentialOAuth value, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var client = value.Metadata?.GetValueOrDefault("client");
        return new()
        {
            AccessToken = value.Access, RefreshToken = string.IsNullOrEmpty(value.Refresh) ? null : value.Refresh,
            TokenType = MetadataString(value, "tokenType") ?? "Bearer", Scope = MetadataString(value, "scope"), ObtainedAt = now,
            ExpiresIn = value.Expires == 0 ? null : (int)Math.Clamp(Math.Floor((value.Expires - now.ToUnixTimeMilliseconds()) / 1000), 0, int.MaxValue),
            ClientId = ClientString(client, "client_id"), ClientSecret = ClientString(client, "client_secret"),
            TokenEndpointAuthMethod = ClientString(client, "token_endpoint_auth_method"),
            AuthorizationServer = MetadataString(value, "authorizationServer")
        };
    }

    private static string? MetadataString(CredentialOAuth value, string key) => value.Metadata is { } metadata
        && metadata.TryGetValue(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static string? ClientString(JsonElement? client, string key) => client is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private sealed class AuthorizationCache(McpOAuthAuthorization attempt) : ITokenCache
    {
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(attempt.Tokens.Task.IsCompletedSuccessfully ? attempt.Tokens.Task.Result : null);

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken)) throw new McpOAuthException("MCP OAuth did not return tokens.");
            attempt.Tokens.TrySetResult(tokens);
            return ValueTask.CompletedTask;
        }
    }
}
