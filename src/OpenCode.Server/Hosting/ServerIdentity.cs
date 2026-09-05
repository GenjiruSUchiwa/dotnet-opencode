namespace OpenCode.Server.Hosting;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using OpenCode.Server.Endpoints;

internal interface IServerIdentity
{
    string Id { get; }
    string? Url { get; }
    string State { get; }
    bool Authorized(HttpRequest request);
}

internal sealed class ServerCredentials(string password)
{
    private readonly byte[] _authorization = Encoding.UTF8.GetBytes($"opencode:{password}");
    internal bool Authorized(HttpRequest request)
    {
        var token = RequestLocation.QueryValue(request, "auth_token");
        return Authorized(string.IsNullOrEmpty(token) ? request.Headers.Authorization.ToString() : "Basic " + token);
    }
    internal bool Authorized(string header)
    {
        if (!AuthenticationHeaderValue.TryParse(header, out var value)
            || !value.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) || value.Parameter is null) return false;
        var bytes = new byte[_authorization.Length];
        return Convert.TryFromBase64String(value.Parameter, bytes, out var written)
            && written == bytes.Length && CryptographicOperations.FixedTimeEquals(bytes, _authorization);
    }
}
