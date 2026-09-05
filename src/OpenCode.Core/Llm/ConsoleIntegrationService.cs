namespace OpenCode.Core.Llm;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Schema;

public sealed class ConsoleDeviceAuthorization
{
    internal ConsoleDeviceAuthorization(string server, ConsoleDeviceResponse device, Uri verificationUri, DateTimeOffset expiresAt)
    {
        Server = server;
        DeviceCode = device.DeviceCode;
        UserCode = device.UserCode;
        VerificationUri = verificationUri;
        PollInterval = TimeSpan.FromSeconds(device.Interval);
        DeviceLifetime = TimeSpan.FromSeconds(device.ExpiresIn);
        ExpiresAt = expiresAt;
    }
    internal string DeviceCode { get; }
    internal int Completing;
    public string Server { get; }
    public string UserCode { get; }
    public Uri VerificationUri { get; }
    public TimeSpan PollInterval { get; }
    public TimeSpan DeviceLifetime { get; }
    // The upstream integration manager bounds attempts to ten minutes; the token
    // endpoint independently enforces the device grant's advertised lifetime.
    public DateTimeOffset ExpiresAt { get; }
}

public sealed record ConsoleModelConfiguration(string ProviderId, string ModelId, ProviderConfig Provider, ModelConfig Model);

public sealed class ConsoleProviderCatalog(string credentialId, IReadOnlyDictionary<string, ProviderConfig> providers)
{
    // Bind catalog provenance and transport auth to the same resolution snapshot.
    internal StoredCredential? Credential { get; init; }
    internal JsonObject? ProviderDocument { get; init; }
    public string CredentialId { get; } = credentialId;
    public string IntegrationId => ConsoleIntegrationService.IntegrationId;
    public IReadOnlyDictionary<string, ProviderConfig> Providers { get; } = providers;

    public ConsoleModelConfiguration Select(string providerId, string modelId)
    {
        if (!Providers.TryGetValue(providerId, out var provider) || provider.Models is null || !provider.Models.TryGetValue(modelId, out var model))
            throw new LlmException(new LlmFailure.InvalidRequest("The selected console model is not present in the discovered catalog."));
        if (model.Disabled == true) throw new LlmException(new LlmFailure.InvalidRequest("The selected console model is disabled."));
        if (!ProviderResolver.SupportsPackage(model.Package ?? provider.Package))
            throw new LlmException(new LlmFailure.Unsupported("The discovered model requires an unsupported provider protocol."));
        return new ConsoleModelConfiguration(providerId, modelId, provider, model);
    }
}

/// <summary>Explicit console authorization/discovery. Construction never starts login, polling, or discovery.</summary>
public sealed class ConsoleIntegrationService(HttpClient http, CredentialStore credentials, TimeProvider? timeProvider = null)
{
    public const string IntegrationId = "opencode";
    public const string MethodId = "device";
    public const string DefaultServer = "https://opencode.ai/console";
    private const string ClientId = "opencode-cli";
    private readonly TimeProvider _clock = timeProvider ?? credentials.Clock;

    public async Task<ConsoleDeviceAuthorization> BeginDeviceAuthorizationAsync(string? server = null, CancellationToken ct = default)
    {
        server = NormalizeServer(server ?? DefaultServer);
        using var request = Post(server + "/auth/device/code", new ConsoleDeviceRequest(ClientId), ConsoleJsonContext.Default.ConsoleDeviceRequest);
        var response = await SendAsync(request, ConsoleJsonContext.Default.ConsoleDeviceResponse, true, ct);
        var device = response.Value;
        if (device.DeviceCode is null || device.UserCode is null || device.VerificationUri is null
            || !double.IsFinite(device.Interval) || device.Interval < 0 || device.Interval >= TimeSpan.MaxValue.TotalSeconds
            || !double.IsFinite(device.ExpiresIn) || device.ExpiresIn < 0 || device.ExpiresIn >= TimeSpan.MaxValue.TotalSeconds)
            throw InvalidResponse(response.Http);
        if (!Uri.TryCreate(new Uri(server + "/"), device.VerificationUri, out var verification)
            || verification.Scheme is not ("http" or "https")) throw InvalidResponse(response.Http);
        return new ConsoleDeviceAuthorization(server, device, verification, _clock.GetUtcNow().AddMinutes(10));
    }

    public async Task<CredentialMutation> CompleteDeviceAuthorizationAsync(ConsoleDeviceAuthorization authorization,
        string? label = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var remaining = authorization.ExpiresAt - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) throw new LlmException(new LlmFailure.Authentication("Device authorization expired."));
        using var deadline = new CancellationTokenSource(remaining, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try { return await CompleteDeviceAuthorizationCoreAsync(authorization, label, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new LlmException(new LlmFailure.Authentication("Device authorization expired.")); }
    }

    private async Task<CredentialMutation> CompleteDeviceAuthorizationCoreAsync(ConsoleDeviceAuthorization authorization,
        string? label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref authorization.Completing, 1) != 0)
            throw new LlmException(new LlmFailure.InvalidRequest("This device authorization has already been completed or is being completed."));
        var wait = authorization.PollInterval;
        while (true)
        {
            var remaining = authorization.ExpiresAt - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new LlmException(new LlmFailure.Authentication("Device authorization expired."));
            await Task.Delay(wait < remaining ? wait : remaining, _clock, ct);
            if (_clock.GetUtcNow() >= authorization.ExpiresAt) throw new LlmException(new LlmFailure.Authentication("Device authorization expired."));
            using var request = Post(authorization.Server + "/auth/device/token",
                new ConsoleDeviceTokenRequest("urn:ietf:params:oauth:grant-type:device_code", authorization.DeviceCode, ClientId),
                ConsoleJsonContext.Default.ConsoleDeviceTokenRequest);
            // The device endpoint carries pending/slow_down in JSON on non-2xx responses.
            var response = await SendAsync(request, ConsoleJsonContext.Default.ConsoleTokenResponse, false, ct);
            if (response.Value.AccessToken is not null)
            {
                ValidateToken(response.Value, response.Http);
                var userTask = GetAsync(authorization.Server + "/api/user", response.Value.AccessToken, ConsoleJsonContext.Default.ConsoleUser, ct);
                var orgsTask = GetAsync(authorization.Server + "/api/orgs", response.Value.AccessToken, ConsoleJsonContext.Default.ConsoleOrgArray, ct);
                await Task.WhenAll(userTask, orgsTask);
                var user = (await userTask).Value;
                var orgs = (await orgsTask).Value;
                if (user.Id is null || user.Email is null) throw InvalidResponse((await userTask).Http);
                if (orgs.Any(org => org is null || org.Id is null || org.Name is null)) throw InvalidResponse((await orgsTask).Http);
                var org = orgs.OrderBy(item => item.Name, StringComparer.CurrentCulture).ThenBy(item => item.Id, StringComparer.CurrentCulture).FirstOrDefault();
                var metadata = new JsonObject { ["server"] = authorization.Server, ["accountID"] = user.Id, ["email"] = user.Email };
                if (org is not null) { metadata["orgID"] = org.Id; metadata["orgName"] = org.Name; }
                if (label is null && org is not null) label = org.Name;
                if (label is null)
                {
                    var labels = (await credentials.ListCredentialsForIntegrationAsync(IntegrationId, ct)).Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
                    label = Enumerable.Range(0, labels.Count + 1).Select(index => index == 0 ? "OpenCode" : "OpenCode " + (index + 1).ToString(CultureInfo.InvariantCulture))
                        .First(candidate => !labels.Contains(candidate));
                }
                var value = new JsonObject
                {
                    ["type"] = "oauth", ["methodID"] = MethodId, ["access"] = response.Value.AccessToken,
                    ["refresh"] = response.Value.RefreshToken, ["expires"] = Expires(response.Value.ExpiresIn!.Value, response.Http), ["metadata"] = metadata
                };
                ct.ThrowIfCancellationRequested();
                if (_clock.GetUtcNow() >= authorization.ExpiresAt)
                    throw new LlmException(new LlmFailure.Authentication("Device authorization expired."));
                return await credentials.CreateAsync(IntegrationId, JsonSerializer.SerializeToElement(value), label, ct);
            }
            if (response.Value.Error == "authorization_pending") continue;
            if (response.Value.Error == "slow_down") { wait += TimeSpan.FromSeconds(5); continue; }
            if (response.Value.Error is null) throw InvalidResponse(response.Http);
            throw new LlmException(new LlmFailure.Authentication("Device authorization failed.")
            { Body = response.Body, Http = response.Http });
        }
    }

    /// <summary>Resolve the selected channel credential; refresh only this console's device-method OAuth credentials.</summary>
    public async Task<StoredCredential?> ResolveCredentialAsync(CancellationToken ct = default)
    {
        var credential = await credentials.GetActiveCredentialAsync(IntegrationId, ct);
        if (credential is null || credential.Value.GetProperty("type").GetString() == "key") return credential;
        if (credential.Value.GetProperty("methodID").GetString() != MethodId)
            throw new LlmException(new LlmFailure.Unsupported("The selected console OAuth method is not supported."));
        if (credential.Value.GetProperty("expires").GetDecimal() > _clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds()) return credential;
        return (await RefreshCredentialAsync(credential.Id, ct)).Credential;
    }

    public async Task<CredentialMutation> RefreshCredentialAsync(string credentialId, CancellationToken ct = default)
    {
        var credential = await credentials.GetCredentialAsync(credentialId, ct)
            ?? throw new LlmException(new LlmFailure.Authentication("The console credential no longer exists."));
        if (credential.IntegrationId != IntegrationId || credential.Value.GetProperty("type").GetString() != "oauth"
            || credential.Value.GetProperty("methodID").GetString() != MethodId)
            throw new LlmException(new LlmFailure.Unsupported("Only console device-method OAuth credentials can be refreshed by this service."));
        using var request = Post(Server(credential.Value) + "/auth/device/token",
            new ConsoleRefreshRequest("refresh_token", credential.Value.GetProperty("refresh").GetString()!, ClientId),
            ConsoleJsonContext.Default.ConsoleRefreshRequest);
        var response = await SendAsync(request, ConsoleJsonContext.Default.ConsoleTokenResponse, true, ct);
        ValidateToken(response.Value, response.Http);
        var value = JsonNode.Parse(credential.Value.GetRawText())!.AsObject();
        value["access"] = response.Value.AccessToken;
        value["refresh"] = response.Value.RefreshToken;
        value["expires"] = Expires(response.Value.ExpiresIn!.Value, response.Http);
        // Value-only updates intentionally have no upstream credential.updated/switched event.
        var mutation = await credentials.UpdateAsync(credential.Id, value: JsonSerializer.SerializeToElement(value), ct: ct);
        if (mutation.Credential is null) throw new LlmException(new LlmFailure.Authentication("The console credential was removed during refresh."));
        return mutation;
    }

    public async Task<ConsoleProviderCatalog?> DiscoverProvidersAsync(CancellationToken ct = default)
    {
        var credential = await ResolveCredentialAsync(ct);
        if (credential is null) return null;
        var value = credential.Value;
        var token = value.GetProperty(value.GetProperty("type").GetString() == "oauth" ? "access" : "key").GetString()!;
        using var request = Get(Server(value) + "/api/config", token);
        if (MetadataString(value, "orgID") is { Length: > 0 } orgId)
        {
            if (orgId.Contains('\r') || orgId.Contains('\n'))
                throw new LlmException(new LlmFailure.InvalidRequest("Console organization metadata contains an invalid header value."));
            request.Headers.Add("x-org-id", orgId);
        }
        var response = await SendAsync(request, ConsoleJsonContext.Default.ConsoleConfigResponse, true, ct, allowNotFound: true);
        if (response.Http.Status == HttpStatusCode.NotFound) return null;
        if (response.Value.Config.ValueKind != JsonValueKind.Object) throw InvalidResponse(response.Http);
        if (!response.Value.Config.TryGetProperty("provider", out var providers)) return null;
        try
        {
            var document = NormalizeProviders(providers);
            return new ConsoleProviderCatalog(credential.Id, document.Deserialize<Dictionary<string, ProviderConfig>>()!)
            { Credential = credential, ProviderDocument = document };
        }
        catch (LlmException error)
        { throw new LlmException(error.Reason with { Http = response.Http, Body = response.Body }, error.InnerException); }
        catch (NotSupportedException)
        { throw new LlmException(new LlmFailure.Unsupported("Console configuration requires unsupported legacy normalization.") { Http = response.Http }); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new LlmException(new LlmFailure.InvalidProviderOutput("Console returned invalid provider configuration.") { Http = response.Http, Body = response.Body }, error); }
    }

    private static JsonObject NormalizeProviders(JsonElement providers)
    {
        if (providers.ValueKind != JsonValueKind.Object) throw new LlmException(new LlmFailure.InvalidProviderOutput("Console providers must be an object."));
        var legacy = JsonNode.Parse(providers.GetRawText())!.AsObject();
        var variantHeaders = new Dictionary<(string Provider, string Model, string Variant), JsonNode>();
        var providerOptionBodies = new Dictionary<string, JsonNode>();
        foreach (var (providerId, providerNode) in legacy)
        {
            var provider = providerNode?.AsObject() ?? throw new JsonException("Invalid console provider.");
            if (provider["api"] is JsonValue api && api.TryGetValue<string>(out var endpoint) && endpoint.Length == 0) provider.Remove("api");
            if (provider["options"] is JsonObject options)
            {
                options.Remove("apiKey");
                // Console options.body remains a setting, unlike local V1 migration.
                // Unsupported settings are rejected for the selected model, not the entire catalog.
                if (options.Remove("body", out var body) && body is not null) providerOptionBodies[providerId] = body;
            }
            if (provider["models"] is not JsonObject models) continue;
            foreach (var (modelId, modelNode) in models)
            {
                var model = modelNode?.AsObject() ?? throw new JsonException("Invalid console model.");
                if (model["release_date"] is JsonValue released && released.TryGetValue<string>(out var date))
                    model["time"] = new JsonObject { ["released"] = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUnixTimeMilliseconds() : 0 };
                if (model["provider"] is JsonObject modelTransport && modelTransport["api"] is JsonValue modelApi
                    && modelApi.TryGetValue<string>(out var modelEndpoint) && modelEndpoint.Length == 0) modelTransport.Remove("api");
                if (model["options"] is JsonObject modelOptions)
                {
                    modelOptions.Remove("apiKey");
                    modelOptions.Remove("headers");
                    // Console model options override the model provider endpoint, unlike local V1 migration.
                    if (modelOptions.ContainsKey("baseURL") && model["provider"] is JsonObject transport) transport.Remove("api");
                }
                if (model.Remove("interleaved", out var interleaved))
                {
                    var field = interleaved is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text
                        : interleaved is JsonObject obj ? obj["field"]?.GetValue<string>() : null;
                    if (field is not null) model["compatibility"] = new JsonObject { ["reasoningField"] = field };
                }
                if (model["variants"] is not JsonObject variants) continue;
                foreach (var (variantId, variantNode) in variants)
                {
                    var variant = variantNode?.AsObject() ?? throw new JsonException("Invalid console variant.");
                    variant.Remove("apiKey");
                    if (variant.Remove("headers", out var headers) && headers is not null)
                        variantHeaders[(providerId, modelId, variantId)] = headers;
                }
            }
        }
        // Reuse the existing pure migration: npm -> aisdk package, id -> modelID,
        // options -> settings, variants map -> canonical variants array. No local substitutions.
        var normalized = ConfigLoader.NormalizeDocument(new JsonObject { ["provider"] = legacy }, legacyProviderIds: false);
        foreach (var (providerId, body) in providerOptionBodies)
            normalized["providers"]![providerId]!["settings"]!["body"] = body.DeepClone();
        foreach (var (key, headers) in variantHeaders)
        {
            var variants = normalized["providers"]![key.Provider]!["models"]![key.Model]!["variants"]!.AsArray();
            variants.Single(item => item!["id"]!.GetValue<string>() == key.Variant)!["headers"] = headers.DeepClone();
        }
        return normalized["providers"]!.DeepClone().AsObject();
    }

    private long Expires(double seconds, LlmHttpContext context)
    {
        try { return _clock.GetUtcNow().AddSeconds(seconds).ToUnixTimeMilliseconds(); }
        catch (ArgumentOutOfRangeException) { throw InvalidResponse(context); }
    }
    private static string? MetadataString(JsonElement value, string key) => value.TryGetProperty("metadata", out var metadata)
        && metadata.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static string Server(JsonElement value) => NormalizeServer(MetadataString(value, "server") ?? DefaultServer);

    public static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new LlmException(new LlmFailure.InvalidRequest("Console server must be an absolute HTTP(S) URL."));
        return url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + url.AbsolutePath.TrimEnd('/');
    }

    private static void ValidateToken(ConsoleTokenResponse token, LlmHttpContext context)
    {
        if (token.AccessToken is null || token.RefreshToken is null || token.ExpiresIn is not { } seconds
            || !double.IsFinite(seconds) || seconds < 0) throw InvalidResponse(context);
    }

    private static HttpRequestMessage Get(string url, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Contains('\r') || token.Contains('\n'))
            throw new LlmException(new LlmFailure.Authentication("Console credential cannot be encoded as a Bearer token."));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Post<T>(string url, T body, JsonTypeInfo<T> typeInfo)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(JsonSerializer.Serialize(body, typeInfo), System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<(T Value, LlmHttpContext Http, string Body)> GetAsync<T>(string url, string token, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var request = Get(url, token);
        return await SendAsync(request, typeInfo, true, ct);
    }

    private async Task<(T Value, LlmHttpContext Http, string Body)> SendAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> typeInfo,
        bool requireSuccess, CancellationToken ct, bool allowNotFound = false)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            ProviderUserAgent.Apply(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var context = LlmHttpContext.From(response);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return (default!, context, "");
            var body = await response.Content.ReadAsStringAsync(ct);
            if (requireSuccess && !response.IsSuccessStatusCode) throw HttpFailure(context, body);
            try
            {
                var value = JsonSerializer.Deserialize(body, typeInfo);
                return (value ?? throw InvalidResponse(context), context, body);
            }
            catch (JsonException error)
            { throw new LlmException(new LlmFailure.InvalidProviderOutput("Console returned an invalid JSON response.") { Http = context, Body = body }, error); }
        }
        catch (HttpRequestException error) { throw new LlmException(new LlmFailure.Transport("Console HTTP transport failed."), error); }
    }

    private static LlmException InvalidResponse(LlmHttpContext context) => new(new LlmFailure.InvalidProviderOutput("Console returned an invalid response.") { Http = context });
    private static LlmException HttpFailure(LlmHttpContext context, string body)
    {
        LlmFailure reason = context.Status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LlmFailure.Authentication("Console authorization failed."),
            HttpStatusCode.TooManyRequests => new LlmFailure.RateLimit("Console rate limit exceeded."),
            >= HttpStatusCode.InternalServerError => new LlmFailure.ProviderInternal("Console service failed."),
            _ => new LlmFailure.Provider("Console HTTP request failed.")
        };
        return new LlmException(reason with { Http = context, Body = body });
    }
}

internal sealed record ConsoleDeviceRequest([property: JsonPropertyName("client_id")] string ClientId);
internal sealed record ConsoleDeviceTokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("client_id")] string ClientId);
internal sealed record ConsoleRefreshRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("client_id")] string ClientId);
internal sealed record ConsoleDeviceResponse(
    [property: JsonPropertyName("device_code"), JsonRequired] string DeviceCode,
    [property: JsonPropertyName("user_code"), JsonRequired] string UserCode,
    [property: JsonPropertyName("verification_uri_complete"), JsonRequired] string VerificationUri,
    [property: JsonPropertyName("expires_in"), JsonRequired] double ExpiresIn,
    [property: JsonPropertyName("interval"), JsonRequired] double Interval);
internal sealed record ConsoleTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken = null,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    [property: JsonPropertyName("expires_in")] double? ExpiresIn = null,
    [property: JsonPropertyName("error")] string? Error = null);
internal sealed record ConsoleUser([property: JsonPropertyName("id"), JsonRequired] string Id, [property: JsonPropertyName("email"), JsonRequired] string Email);
internal sealed record ConsoleOrg([property: JsonPropertyName("id"), JsonRequired] string Id, [property: JsonPropertyName("name"), JsonRequired] string Name);
internal sealed record ConsoleConfigResponse([property: JsonPropertyName("config"), JsonRequired] JsonElement Config);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConsoleDeviceRequest))]
[JsonSerializable(typeof(ConsoleDeviceTokenRequest))]
[JsonSerializable(typeof(ConsoleRefreshRequest))]
[JsonSerializable(typeof(ConsoleDeviceResponse))]
[JsonSerializable(typeof(ConsoleTokenResponse))]
[JsonSerializable(typeof(ConsoleUser))]
[JsonSerializable(typeof(ConsoleOrg[]))]
[JsonSerializable(typeof(ConsoleConfigResponse))]
internal partial class ConsoleJsonContext : JsonSerializerContext;
