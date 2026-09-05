namespace OpenCode.Client;

using System.Net;
using System.Text;

/// <summary>Raw selected-origin HTTP, without typed-success assumptions or redirects
/// delegated to an unknown handler. Caller owns and disposes each returned response.</summary>
public sealed class ApiHttpClient : IDisposable
{
    private readonly ServiceEndpoint _endpoint;
    private readonly Uri _origin;
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public ApiHttpClient(ServiceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
            || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("API endpoint must be an HTTP(S) origin without credentials, path, query or fragment.");
        _origin = origin;
        _endpoint = endpoint;
    }

    public async Task<HttpResponseMessage> SendAsync(string method, string path, string? body = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) throw new ArgumentException("API request path must start with '/'.");
        var address = Selected(new Uri(_origin, path));
        var verb = method.ToUpperInvariant();
        if (verb is not ("DELETE" or "GET" or "HEAD" or "OPTIONS" or "PATCH" or "POST" or "PUT"))
            throw new ArgumentException("Unsupported API request method.");
        if (body is not null && verb is "GET" or "HEAD") throw new ArgumentException("Request with GET/HEAD method cannot have body.");
        var supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
            foreach (var pair in headers)
            {
                ValidateHeader(pair.Key, pair.Value);
                // A Host override must not use the selected listener as a proxy to
                // route its private Authorization header to another authority.
                if (pair.Key.Equals("host", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(_origin.Scheme + "://" + pair.Value, UriKind.Absolute, out var host)
                        || host.AbsolutePath != "/" || host.Query.Length != 0 || host.Fragment.Length != 0)
                        throw new ArgumentException("Host must match the selected server origin.");
                    Selected(host);
                }
                supplied[pair.Key] = pair.Value;
            }
        if (body is not null && !supplied.ContainsKey("content-type")) supplied["content-type"] = "application/json";
        for (var redirects = 0; ; redirects++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(new HttpMethod(verb), address);
            if (body is not null) request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Headers.Accept.ParseAdd("*/*");
            _endpoint.ApplyAuth(request);
            foreach (var pair in supplied)
            {
                if (pair.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)) request.Headers.Authorization = null;
                if (pair.Key.Equals("accept", StringComparison.OrdinalIgnoreCase)) request.Headers.Accept.Clear();
                if (request.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) continue;
                // Content headers also apply without a payload, as Headers.set does.
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.Remove(pair.Key);
                if (!request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                    throw new ArgumentException($"Unsupported request header: {pair.Key}");
            }
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || response.Headers.Location is null)
                return response;
            using (response)
            {
                if (redirects >= 20) throw new HttpRequestException("API redirect limit exceeded.");
                // Validate before creating a redirected request or attaching auth.
                address = Selected(new Uri(address, response.Headers.Location));
                if ((response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && verb == "POST")
                    || response.StatusCode == HttpStatusCode.SeeOther && verb is not ("GET" or "HEAD"))
                {
                    verb = "GET";
                    body = null;
                    foreach (var name in new[] { "content-encoding", "content-language", "content-location", "content-type", "content-length" })
                        supplied.Remove(name);
                }
            }
        }
    }

    private Uri Selected(Uri address)
    {
        if (address.Scheme != _origin.Scheme || !address.IdnHost.Equals(_origin.IdnHost, StringComparison.OrdinalIgnoreCase)
            || address.Port != _origin.Port || address.UserInfo.Length != 0)
            throw new ArgumentException("API targets and redirects must stay on the selected server origin.");
        return address;
    }
    public static void ValidateHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (name.Length == 0 || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character)))
            || value.Any(character => character is '\r' or '\n' or '\0' || character > 255))
            throw new ArgumentException($"Invalid request header name or value: {name}");
    }
    public void Dispose() => _http.Dispose();
}
