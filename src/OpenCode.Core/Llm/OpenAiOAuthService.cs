namespace OpenCode.Core.Llm;

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using OpenCode.Core.Database;
using OpenCode.Schema;

public sealed class OpenAiBrowserAuthorization
{
    internal OpenAiBrowserAuthorization(Uri uri, Uri redirect, string verifier, string state, DateTimeOffset expires)
    { AuthorizationUri = uri; RedirectUri = redirect; Verifier = verifier; State = state; ExpiresAt = expires; }
    public Uri AuthorizationUri { get; }
    public Uri RedirectUri { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool MatchesState(string? state) => state is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(State));
    internal string Verifier { get; }
    internal string State { get; }
    internal int Completing;
}

public sealed class OpenAiDeviceAuthorization
{
    internal OpenAiDeviceAuthorization(string id, string code, TimeSpan interval, DateTimeOffset expires)
    { DeviceId = id; UserCode = code; PollInterval = interval; ExpiresAt = expires; }
    public Uri VerificationUri => new(OpenAiOAuthService.Issuer + "/codex/device");
    public string UserCode { get; }
    public TimeSpan PollInterval { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal string DeviceId { get; }
    internal int Completing;
}

/// <summary>Explicit OpenAI authorization and channel credential refresh. No listener or browser is started implicitly.</summary>
public sealed class OpenAiOAuthService(CredentialStore credentials, TimeProvider? timeProvider = null, string? userAgent = null)
{
    public const string IntegrationId = "openai";
    public const string BrowserMethodId = "chatgpt-browser";
    public const string HeadlessMethodId = "chatgpt-headless";
    public const string Issuer = "https://auth.openai.com";
    public const string BackendBaseUrl = "https://chatgpt.com/backend-api/codex";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal static string DefaultUserAgent => OpenCodeChannel.UserAgent;
    // Process-lifetime transport, with no default credentials/headers. Redirects
    // must not forward account/session headers or refresh form data to another host.
    internal static HttpClient PinnedHttp { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeProvider _clock = timeProvider ?? credentials.Clock;
    private readonly string _userAgent = userAgent ?? DefaultUserAgent;
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public OpenAiBrowserAuthorization BeginBrowserAuthorization(int callbackPort = 1455)
    {
        if (callbackPort is not (1455 or 1457)) throw Invalid("Use one of the source-defined loopback callback ports.");
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var verifier = new string(RandomNumberGenerator.GetBytes(43).Select(value => chars[value % chars.Length]).ToArray());
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = new Uri($"http://localhost:{callbackPort}/auth/callback");
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = redirect.AbsoluteUri,
            ["scope"] = "openid profile email offline_access", ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true", ["state"] = state, ["originator"] = "opencode"
        };
        var uri = new Uri(Issuer + "/oauth/authorize?" + string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        return new OpenAiBrowserAuthorization(uri, redirect, verifier, state, _clock.GetUtcNow().AddMinutes(10));
    }

    /// <summary>The caller owns the loopback listener and passes the callback code/state. No port takeover or /cancel request is performed.</summary>
    public async Task<CredentialMutation> CompleteBrowserAuthorizationAsync(OpenAiBrowserAuthorization authorization,
        string code, string state, string? label = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref authorization.Completing, 1) != 0) throw Invalid("This browser authorization has already been claimed.");
        if (string.IsNullOrEmpty(code) || !authorization.MatchesState(state))
            throw new LlmException(new LlmFailure.Authentication("OpenAI callback code or state is invalid."));
        using var deadline = Deadline(authorization.ExpiresAt);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var token = await ExchangeAsync(code, authorization.RedirectUri.AbsoluteUri, authorization.Verifier, lifetime.Token);
            return await SaveAsync(BrowserMethodId, token, label, authorization.ExpiresAt, lifetime.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new LlmException(new LlmFailure.Authentication("OpenAI authorization expired.")); }
    }

    public async Task<OpenAiDeviceAuthorization> BeginHeadlessAuthorizationAsync(CancellationToken ct = default)
    {
        using var request = JsonPost("/api/accounts/deviceauth/usercode", new OpenAiDeviceRequest(ClientId), OpenAiOAuthJsonContext.Default.OpenAiDeviceRequest);
        var device = await SendJsonAsync(request, OpenAiOAuthJsonContext.Default.OpenAiDeviceCode, ct);
        if (string.IsNullOrEmpty(device.DeviceAuthId) || string.IsNullOrEmpty(device.UserCode) || device.Interval is null)
            throw new LlmException(new LlmFailure.InvalidProviderOutput("OpenAI returned an invalid device authorization."));
        var match = Regex.Match(device.Interval, @"^\s*([+-]?\d+)", RegexOptions.CultureInvariant);
        var parsed = match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var value) && value != 0 ? value : 5;
        var seconds = Math.Max(parsed, 1);
        if (!double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds - 3)
            throw new LlmException(new LlmFailure.InvalidProviderOutput("OpenAI returned an invalid polling interval."));
        return new OpenAiDeviceAuthorization(device.DeviceAuthId, device.UserCode, TimeSpan.FromSeconds(seconds + 3), _clock.GetUtcNow().AddMinutes(10));
    }

    public async Task<CredentialMutation> CompleteHeadlessAuthorizationAsync(OpenAiDeviceAuthorization authorization,
        string? label = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref authorization.Completing, 1) != 0) throw Invalid("This device authorization has already been claimed.");
        using var deadline = Deadline(authorization.ExpiresAt);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (true)
            {
                using var request = JsonPost("/api/accounts/deviceauth/token", new OpenAiDevicePoll(authorization.DeviceId, authorization.UserCode), OpenAiOAuthJsonContext.Default.OpenAiDevicePoll);
                AddUserAgent(request);
                using var response = await SendAsync(request, lifetime.Token);
                if (response.IsSuccessStatusCode)
                {
                    var granted = await DecodeAsync(response, OpenAiOAuthJsonContext.Default.OpenAiDeviceGrant, lifetime.Token);
                    if (string.IsNullOrEmpty(granted.AuthorizationCode) || string.IsNullOrEmpty(granted.CodeVerifier))
                        throw new LlmException(new LlmFailure.InvalidProviderOutput("OpenAI returned an invalid authorization grant.") { Http = LlmHttpContext.From(response) });
                    var token = await ExchangeAsync(granted.AuthorizationCode, Issuer + "/deviceauth/callback", granted.CodeVerifier, lifetime.Token);
                    return await SaveAsync(HeadlessMethodId, token, label, authorization.ExpiresAt, lifetime.Token);
                }
                if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
                    await FailAsync(response, lifetime.Token);
                // Unlike the console flow, headless OpenAI polls immediately and
                // sleeps only after a 403/404, including the source's 3-second margin.
                await Task.Delay(authorization.PollInterval, _clock, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new LlmException(new LlmFailure.Authentication("OpenAI authorization expired.")); }
    }

    public async Task<StoredCredential?> ResolveCredentialAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            var credential = await credentials.GetActiveCredentialAsync(IntegrationId, ct);
            if (credential is null || credential.Value.GetProperty("type").GetString() == "key") return credential;
            if (!IsChatGptCredential(credential)) throw new LlmException(new LlmFailure.Unsupported("The selected OpenAI OAuth method is not implemented."));
            if (credential.Value.GetProperty("expires").GetDecimal() > _clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds()) return credential;
            return (await RefreshCoreAsync(credential, ct)).Credential;
        }
        finally { _refresh.Release(); }
    }

    public async Task<ILlmClient> CreateResponsesClientAsync(string? sessionId = null,
        IReadOnlyDictionary<string, string>? headers = null, JsonObject? body = null, JsonObject? providerOptions = null,
        CancellationToken ct = default)
    {
        var credential = await ResolveCredentialAsync(ct);
        if (credential is null || !IsChatGptCredential(credential))
            throw new LlmException(new LlmFailure.Authentication("Select a supported ChatGPT OAuth credential in this channel first."));
        return new OpenAiResponsesLlmClient(credential, headers, body, providerOptions, sessionId);
    }

    public async Task<CredentialMutation> RefreshCredentialAsync(string credentialId, CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            var credential = await credentials.GetCredentialAsync(credentialId, ct)
                ?? throw new LlmException(new LlmFailure.Authentication("The OpenAI credential no longer exists."));
            if (!IsChatGptCredential(credential)) throw new LlmException(new LlmFailure.Unsupported("Only the supported OpenAI OAuth methods can be refreshed."));
            return await RefreshCoreAsync(credential, ct);
        }
        finally { _refresh.Release(); }
    }

    private async Task<CredentialMutation> RefreshCoreAsync(StoredCredential credential, CancellationToken ct)
    {
        using var request = FormPost(new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["refresh_token"] = credential.Value.GetProperty("refresh").GetString()!, ["client_id"] = ClientId });
        var token = await SendJsonAsync(request, OpenAiOAuthJsonContext.Default.OpenAiOAuthTokens, ct);
        var value = Credential(credential.Value.GetProperty("methodID").GetString()!, token);
        if (!value.ContainsKey("metadata") && credential.Value.TryGetProperty("metadata", out var metadata)) value["metadata"] = JsonNode.Parse(metadata.GetRawText());
        var result = await credentials.UpdateAsync(credential.Id, value: JsonSerializer.SerializeToElement(value), ct: ct);
        if (result.Credential is null) throw new LlmException(new LlmFailure.Authentication("The OpenAI credential was removed during refresh."));
        return result;
    }

    private async Task<OpenAiOAuthTokens> ExchangeAsync(string code, string redirect, string verifier, CancellationToken ct)
    {
        using var request = FormPost(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["client_id"] = ClientId, ["code_verifier"] = verifier
        });
        return await SendJsonAsync(request, OpenAiOAuthJsonContext.Default.OpenAiOAuthTokens, ct);
    }

    private async Task<CredentialMutation> SaveAsync(string method, OpenAiOAuthTokens tokens, string? label, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var value = Credential(method, tokens);
        if (label is null)
        {
            var labels = (await credentials.ListCredentialsForIntegrationAsync(IntegrationId, ct)).Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
            label = Enumerable.Range(0, labels.Count + 1).Select(index => index == 0 ? "OpenAI" : "OpenAI " + (index + 1).ToString(CultureInfo.InvariantCulture))
                .First(candidate => !labels.Contains(candidate));
        }
        ct.ThrowIfCancellationRequested();
        if (_clock.GetUtcNow() >= expiresAt) throw new LlmException(new LlmFailure.Authentication("OpenAI authorization expired."));
        return await credentials.CreateAsync(IntegrationId, JsonSerializer.SerializeToElement(value), label, ct);
    }

    private JsonObject Credential(string method, OpenAiOAuthTokens tokens)
    {
        if (string.IsNullOrEmpty(tokens.AccessToken) || string.IsNullOrEmpty(tokens.RefreshToken))
            throw new LlmException(new LlmFailure.InvalidProviderOutput("OpenAI returned incomplete token material."));
        var expires = _clock.GetUtcNow().ToUnixTimeMilliseconds() + (tokens.ExpiresIn ?? 3600) * 1000;
        if (!double.IsFinite(expires) || expires < 0 || Math.Truncate(expires) != expires)
            throw new LlmException(new LlmFailure.InvalidProviderOutput("OpenAI returned an invalid credential expiry."));
        var result = new JsonObject { ["type"] = "oauth", ["methodID"] = method, ["access"] = tokens.AccessToken, ["refresh"] = tokens.RefreshToken, ["expires"] = expires };
        var account = Claim(tokens.IdToken) ?? Claim(tokens.AccessToken);
        if (!string.IsNullOrEmpty(account)) result["metadata"] = new JsonObject { ["accountID"] = account };
        return result;
    }

    // Claims are routing metadata only, not local proof of token authenticity.
    private static string? Claim(string? token)
    {
        var part = token?.Split('.').ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(part)) return null;
        try
        {
            var base64 = part.Replace('-', '+').Replace('_', '/');
            using var document = JsonDocument.Parse(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) return null;
            if (value.TryGetProperty("chatgpt_account_id", out var direct) && direct.ValueKind != JsonValueKind.String) return null;
            string? namespaced = null;
            if (value.TryGetProperty("https://api.openai.com/auth", out var auth))
            {
                if (auth.ValueKind != JsonValueKind.Object) return null;
                if (auth.TryGetProperty("chatgpt_account_id", out var account))
                {
                    if (account.ValueKind != JsonValueKind.String) return null;
                    namespaced = account.GetString();
                }
            }
            string? organization = null;
            if (value.TryGetProperty("organizations", out var organizations))
            {
                if (organizations.ValueKind != JsonValueKind.Array) return null;
                foreach (var org in organizations.EnumerateArray())
                {
                    if (org.ValueKind != JsonValueKind.Object || !org.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
                    organization ??= id.GetString();
                }
            }
            return direct.ValueKind == JsonValueKind.String ? direct.GetString() : namespaced ?? organization;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    internal static bool IsChatGptCredential(StoredCredential value) => value.IntegrationId == IntegrationId
        && value.Value.ValueKind == JsonValueKind.Object && value.Value.GetProperty("type").GetString() == "oauth"
        && value.Value.GetProperty("methodID").GetString() is BrowserMethodId or HeadlessMethodId;

    internal static bool Eligible(string modelId, JsonNode? body)
    {
        if (body?["reasoning"] is JsonObject reasoning && reasoning["mode"] is JsonValue mode && mode.TryGetValue<string>(out var text) && text == "pro") return false;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId))).ToLowerInvariant();
        if (OpenAiOAuthPolicyData.Allowed(digest)) return true;
        if (OpenAiOAuthPolicyData.Denied(digest)) return false;
        var match = Regex.Match(modelId, @"^gpt-(\d+\.\d+)", RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var version) && version > OpenAiOAuthPolicyData.LegacyVersionCeiling;
    }

    internal static void ApplyCatalog(JsonObject provider)
    {
        provider["settings"] ??= new JsonObject();
        provider["settings"]!["baseURL"] = BackendBaseUrl;
        // Account routing is materialized only by the pinned backend client, not
        // placed in a catalog header dictionary that could be rebound elsewhere.
        if (provider["models"] is not JsonObject models) return;
        foreach (var (id, node) in models)
        {
            var model = node!.AsObject();
            if (!Eligible(model["modelID"]?.GetValue<string>() ?? id, model["body"]))
            {
                model["disabled"] = true;
                continue;
            }
            model["cost"] = new JsonArray();
            model["limit"] ??= new JsonObject();
            model["limit"]!["context"] = OpenAiOAuthPolicyData.ContextLimit;
            model["limit"]!["input"] = OpenAiOAuthPolicyData.InputLimit;
        }
    }

    internal static string? AccountId(StoredCredential credential) => credential.Value.TryGetProperty("metadata", out var metadata)
        && metadata.TryGetProperty("accountID", out var account) && account.ValueKind == JsonValueKind.String ? account.GetString() : null;

    private CancellationTokenSource Deadline(DateTimeOffset expires)
    {
        var remaining = expires - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new LlmException(new LlmFailure.Authentication("OpenAI authorization expired."));
        return new CancellationTokenSource(remaining, _clock);
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static HttpRequestMessage FormPost(Dictionary<string, string> fields) => new(HttpMethod.Post, Issuer + "/oauth/token") { Content = new FormUrlEncodedContent(fields) };
    private static HttpRequestMessage JsonPost<T>(string path, T value, JsonTypeInfo<T> type) => new(HttpMethod.Post, Issuer + path)
    { Content = new StringContent(JsonSerializer.Serialize(value, type), Encoding.UTF8, "application/json") };
    private void AddUserAgent(HttpRequestMessage request)
    {
        if (_userAgent.Contains('\r') || _userAgent.Contains('\n')) throw Invalid("Invalid OAuth User-Agent.");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        ProviderUserAgent.Apply(request);
    }
    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> type, CancellationToken ct)
    {
        AddUserAgent(request);
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) await FailAsync(response, ct);
        return await DecodeAsync(response, type, ct);
    }
    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.RequestUri?.GetLeftPart(UriPartial.Authority) != Issuer) throw Invalid("OAuth requests must use the configured public issuer.");
        try { return await PinnedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException error) { throw new LlmException(new LlmFailure.Transport("OpenAI authorization transport failed."), error); }
    }
    private static async Task<T> DecodeAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try { return JsonSerializer.Deserialize(body, type) ?? throw new JsonException(); }
        catch (JsonException error)
        { throw new LlmException(new LlmFailure.InvalidProviderOutput("OpenAI returned an invalid authorization response.") { Body = body, Http = LlmHttpContext.From(response) }, error); }
    }
    private static async Task FailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new LlmException(ResponsesStreamParser.HttpFailure(LlmHttpContext.From(response), body) with { Body = body, Http = LlmHttpContext.From(response) });
    }
    private static LlmException Invalid(string message) => new(new LlmFailure.InvalidRequest(message));
}

internal sealed record OpenAiDeviceRequest([property: JsonPropertyName("client_id")] string ClientId);
internal sealed record OpenAiDeviceCode(
    [property: JsonPropertyName("device_auth_id"), JsonRequired] string DeviceAuthId,
    [property: JsonPropertyName("user_code"), JsonRequired] string UserCode,
    [property: JsonPropertyName("interval"), JsonRequired] string Interval);
internal sealed record OpenAiDevicePoll([property: JsonPropertyName("device_auth_id")] string DeviceAuthId, [property: JsonPropertyName("user_code")] string UserCode);
internal sealed record OpenAiDeviceGrant(
    [property: JsonPropertyName("authorization_code"), JsonRequired] string AuthorizationCode,
    [property: JsonPropertyName("code_verifier"), JsonRequired] string CodeVerifier);
internal sealed record OpenAiOAuthTokens(
    [property: JsonPropertyName("access_token"), JsonRequired] string AccessToken,
    [property: JsonPropertyName("refresh_token"), JsonRequired] string RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken = null,
    [property: JsonPropertyName("expires_in")] double? ExpiresIn = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAiDeviceRequest))]
[JsonSerializable(typeof(OpenAiDeviceCode))]
[JsonSerializable(typeof(OpenAiDevicePoll))]
[JsonSerializable(typeof(OpenAiDeviceGrant))]
[JsonSerializable(typeof(OpenAiOAuthTokens))]
internal partial class OpenAiOAuthJsonContext : JsonSerializerContext;
