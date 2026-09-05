namespace OpenCode.Cli.Auth;

using System.Net;
using System.Text;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;

internal static class BrowserLogin
{
    internal static async Task<CredentialMutation> RunAsync(OpenAiOAuthService service, CancellationToken ct, TimeProvider clock)
    {
        var binding = await BindAsync(ct, clock).ConfigureAwait(true);
        using var listener = binding.Listener;
        var authorization = service.BeginBrowserAuthorization(binding.Port);
        using var deadline = new CancellationTokenSource(authorization.ExpiresAt - clock.GetUtcNow(), clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        // Stop releases the pending accept. We await it directly, rather than
        // abandoning GetContextAsync with WaitAsync and leaking its completion.
        var stop = lifetime.Token.Register(listener.Stop);
        await using var stopLifetime = stop.ConfigureAwait(true);
        Console.WriteLine($"Go to: {authorization.AuthorizationUri}");
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
            AuthCommands.OpenBrowser(authorization.AuthorizationUri, lifetime.Token);
        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var context = await listener.GetContextAsync().ConfigureAwait(true);
                using var response = context.Response;
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["Referrer-Policy"] = "no-referrer";
                response.ContentType = "text/plain; charset=utf-8";
                if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/auth/callback"
                    || context.Request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address))
                {
                    response.StatusCode = 404;
                    continue;
                }
                var query = context.Request.QueryString;
                var states = query.GetValues("state");
                var codes = query.GetValues("code");
                if (states is not { Length: 1 } || !authorization.MatchesState(states[0]))
                {
                    response.StatusCode = 400;
                    continue;
                }
                if (query["error"] is not null || query["error_description"] is not null || codes is not { Length: 1 } || string.IsNullOrEmpty(codes[0]))
                {
                    response.StatusCode = 400;
                    throw new InvalidOperationException("Authorization callback was rejected.");
                }
                // Respond before token exchange, as upstream does. A browser
                // disconnect must not change the outcome of credential persistence.
                var page = Encoding.UTF8.GetBytes("Authorization callback received. Return to the terminal to check completion.");
                response.ContentLength64 = page.Length;
                try { await response.OutputStream.WriteAsync(page, lifetime.Token).ConfigureAwait(true); }
                catch (Exception error) when (error is HttpListenerException or IOException) { }
                response.Close();
                listener.Stop();
                return await service.CompleteBrowserAuthorizationAsync(authorization, codes[0], states[0], ct: lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested && error is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        { throw new OperationCanceledException(lifetime.Token); }
        finally { listener.Stop(); }
    }

    private static async Task<(HttpListener Listener, int Port)> BindAsync(CancellationToken ct, TimeProvider clock)
    {
        foreach (var port in new[] { 1455, 1457 })
            for (var attempt = 0; attempt < 10; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                try { listener.Start(); return (listener, port); }
                catch (HttpListenerException error) when (error.ErrorCode is 183 or 32 or 98 or 10048)
                {
                    listener.Close();
                    // Do not invoke upstream's /cancel takeover: this process
                    // owns neither the other listener nor its authorization attempt.
                    if (attempt < 9) await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct).ConfigureAwait(true);
                }
                catch
                {
                    listener.Close();
                    throw;
                }
            }
        throw new InvalidOperationException("Both source-defined OAuth callback ports are in use.");
    }
}
