namespace OpenCode.Core.Tools;

using System.Net;

/// <summary>Dedicated anonymous HTTP transport. Never reuse a provider-authenticated HttpClient for webfetch.
/// The host owns disposal; construction performs no network access.</summary>
public sealed class WebFetchTransport : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal async Task<HttpResponseMessage> GetAsync(Uri url, string accept, string userAgent, CancellationToken ct)
    {
        for (var redirects = 0; ; redirects++)
        {
            Validate(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
                return response;
            try
            {
                if (redirects >= 20) throw new ToolExecutionException("Unable to fetch URL: too many redirects.");
                url = new Uri(url, response.Headers.Location);
            }
            finally { response.Dispose(); }
        }
    }

    internal static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) throw new ToolExecutionException("URL must be an absolute HTTP or HTTPS URL.");
        Validate(url);
        return url;
    }

    private static void Validate(Uri url)
    {
        if (url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
            throw new ToolExecutionException("URL must use http:// or https://");
        if (url.UserInfo.Length != 0) throw new ToolExecutionException("URLs containing credentials are not supported.");
    }

    public void Dispose() => _http.Dispose();
}
