namespace OpenCode.Core.Integrations.Wellknown;

using System.Net.Http.Headers;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>A dedicated host HTTP client. Never accepts the Console/client HTTP pipeline or ambient credentials.</summary>
public sealed class WellknownTransport : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, DefaultProxyCredentials = null
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static string Origin(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var origin = value.TrimEnd('/');
        var uri = HttpUri(origin);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new WellknownDiscoveryException("Wellknown source URLs cannot include a query or fragment.");
        return origin;
    }

    public async Task<WellknownEntry> InspectAsync(string value, CancellationToken ct = default)
    {
        var origin = Origin(value);
        var manifest = WellknownManifest.Decode(await GetAsync(new Uri(origin + "/.well-known/opencode"), null, ct).ConfigureAwait(false));
        return new(origin, OpenCode.Schema.IntegrationId.FromExisting(origin), manifest);
    }

    internal async Task<IReadOnlyList<JsonElement>> ResolveAsync(WellknownEntry entry, string? key, CancellationToken ct)
    {
        var configs = new List<JsonElement>();
        if (entry.Manifest.Config is { ValueKind: JsonValueKind.Object } inline) configs.Add(inline.Clone());
        if (entry.Manifest.RemoteConfig is not { } remote) return configs;
        // Credentials in URLs are not supported: they can leak through intermediaries and diagnostics.
        if (remote.Url.Contains("{env:", StringComparison.Ordinal) || remote.Url.Contains("{file:", StringComparison.Ordinal))
            throw new WellknownDiscoveryException("Wellknown remote URL substitution is not supported.");
        var uri = HttpUri(remote.Url);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in remote.Headers ?? ImmutableDictionary<string, string>.Empty)
        {
            if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                throw new WellknownDiscoveryException("Wellknown remote config cannot override host, cookies, or proxy authentication.");
            var authenticated = pair.Value.Contains("{env:", StringComparison.Ordinal) || pair.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase);
            if (authenticated && (key is null || !SameAuthority(HttpUri(entry.Origin), uri)))
                throw new WellknownDiscoveryException("Authenticated wellknown config requires this integration's key and the same source authority.");
            headers.Add(pair.Key, Substitute(pair.Value, entry.Manifest.Auth?.Env, key));
        }
        var document = await GetAsync(uri, headers, ct).ConfigureAwait(false);
        if (document.ValueKind != JsonValueKind.Object) throw new WellknownDiscoveryException("Wellknown remote config must be an object.");
        configs.Add(document.TryGetProperty("config", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested.Clone() : document);
        return configs;
    }

    internal static string Substitute(string value, string? env, string? key)
    {
        if (value.Contains("{file:", StringComparison.Ordinal)) throw new WellknownDiscoveryException("Wellknown config cannot read local files.");
        return Regex.Replace(value, @"\{env:(?<name>[^}]+)\}", match => match.Groups["name"].Value == env && key is not null ? key
            : throw new WellknownDiscoveryException("Wellknown config references an unavailable or undeclared credential variable."), RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture);
    }

    private async Task<JsonElement> GetAsync(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            if (headers is not null)
                foreach (var pair in headers) request.Headers.Add(pair.Key, pair.Value);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Redirects are reported, never followed with discovered headers or credentials.
            if (!response.IsSuccessStatusCode) throw new WellknownDiscoveryException($"Wellknown metadata request failed with HTTP {(int)response.StatusCode}.");
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var streamLifetime = stream.ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new WellknownDiscoveryException("Wellknown metadata request timed out."); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or FormatException or InvalidOperationException)
        { throw new WellknownDiscoveryException("Wellknown metadata could not be retrieved or decoded."); }
    }

    private static Uri HttpUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri
        : throw new WellknownDiscoveryException("Wellknown URLs must be absolute HTTP(S) URLs without user information or fragments.");
    private static bool SameAuthority(Uri source, Uri target) => source.Scheme == target.Scheme && source.IdnHost == target.IdnHost && source.Port == target.Port;
    public void Dispose() => _http.Dispose();
}
