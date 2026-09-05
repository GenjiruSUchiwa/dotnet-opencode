namespace OpenCode.Server.Pty;

using System.Text.RegularExpressions;
using OpenCode.Server.Endpoints;

public sealed class PtyAuthenticationException() : Exception("Server authentication is required.");
public sealed class PtyForbiddenException(string message) : Exception(message);

/// <summary>The credential callback must use the host's existing Basic-auth verifier.</summary>
public sealed partial class PtyRequestPolicy(Func<HttpRequest, bool> authorized, IReadOnlyList<string>? cors = null)
{
    public void RequireAuthentication(HttpRequest request)
    {
        if (!authorized(request)) throw new PtyAuthenticationException();
    }

    // This predicate grants no authorization and consumes nothing. Only the matching connect
    // handler may validate the origin and consume the ticket after the host skips Basic auth.
    public static bool HasPtyConnectTicketURL(HttpRequest request) =>
        ConnectPath().IsMatch(request.Path.Value ?? "") && !string.IsNullOrEmpty(RequestLocation.QueryValue(request, "ticket"));

    public static bool HasPersistentPtyConnectTicketURL(HttpRequest request) =>
        PersistentConnectPath().IsMatch(request.Path.Value ?? "") && !string.IsNullOrEmpty(RequestLocation.QueryValue(request, "ticket"));

    public bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Authority == request.Host.Value) return true;
        return IsAllowedCorsOrigin(origin);
    }

    public bool IsAllowedCorsOrigin(string origin) => origin.Length == 0
            || origin.StartsWith("http://localhost:", StringComparison.Ordinal)
            || origin.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
            || origin.StartsWith("oc://renderer", StringComparison.Ordinal)
            || origin is "tauri://localhost" or "http://tauri.localhost" or "https://tauri.localhost"
            || OpenCodeOrigin().IsMatch(origin)
            || (cors?.Contains(origin, StringComparer.Ordinal) ?? false);

    [GeneratedRegex(@"^/api/pty/[^/]+/connect$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ConnectPath();

    [GeneratedRegex(@"^/api/experimental/persistent-pty/[^/]+/connect$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PersistentConnectPath();

    [GeneratedRegex(@"^https://([a-z0-9-]+\.)*opencode\.ai$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex OpenCodeOrigin();
}
