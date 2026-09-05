namespace OpenCode.Server.Integrations;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using OpenCode.Core.Integrations;

/// <summary>Explicit-login-only loopback listeners. Does not open a browser or displace another process.</summary>
public sealed class IntegrationCallbackFactory(TimeProvider? clock = null) : IIntegrationCallbackFactory
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<IIntegrationCallbackListener> BindAsync(IntegrationCallbackOptions options, CancellationToken ct)
    {
        var redirect = options.RedirectUri;
        if (options.Host is not ("localhost" or "127.0.0.1"))
            throw new NotSupportedException("OAuth callbacks must bind to a supported loopback host.");
        if (redirect is not null && (!redirect.IsAbsoluteUri || redirect.Scheme != "http"
            || redirect.Host is not ("127.0.0.1" or "localhost") || redirect.UserInfo.Length != 0 || redirect.Fragment.Length != 0))
            throw new NotSupportedException("This host supports HTTP loopback OAuth callbacks on localhost or 127.0.0.1 only.");
        var port = options.Port ?? redirect?.Port ?? 0;
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options));
        var builder = WebApplication.CreateSlimBuilder([]);
        // Authorization codes/state must never reach an HTTP access log.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var listener = new IntegrationCallbackListener(app, redirect?.AbsolutePath ?? options.Path, _clock);
        app.Run(listener.ReceiveAsync);
        try
        {
            await app.StartAsync(ct);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The OAuth callback listener did not report a bound address.");
            var bound = new Uri(addresses.Addresses.Single());
            if (redirect is not null && redirect.Port != bound.Port)
                throw new ArgumentException("The callback listener's bound port does not match redirect_uri.");
            listener.SetRedirect(redirect ?? new UriBuilder("http", options.Host, bound.Port, options.Path).Uri);
            return listener;
        }
        catch (Exception error)
        {
            await app.DisposeAsync();
            if (AddressInUse(error)) throw new IntegrationCallbackAddressInUseException();
            throw;
        }
    }

    private static bool AddressInUse(Exception error) => error is AddressInUseException
        or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
        || error.InnerException is { } inner && AddressInUse(inner);
}

internal sealed class IntegrationCallbackListener(WebApplication app, string path, TimeProvider clock) : IIntegrationCallbackListener
{
    private readonly TaskCompletionSource<IntegrationOAuthCallback> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private byte[]? _state;
    private Uri? _redirect;
    private Task? _close;
    public Uri RedirectUri => _redirect ?? throw new InvalidOperationException("The callback listener is not bound.");
    internal void SetRedirect(Uri uri) => _redirect = uri;

    public void ExpectAuthorization(Uri authorizationUri)
    {
        var query = QueryHelpers.ParseQuery(authorizationUri.Query);
        if (!query.TryGetValue("state", out var state) || state.Count != 1 || string.IsNullOrEmpty(state[0]))
            throw new IntegrationAuthorizationException("The authorization URL did not contain one state value.");
        lock (_gate)
        {
            if (_state is not null) throw new InvalidOperationException("The callback listener has already been assigned an authorization.");
            _state = Encoding.UTF8.GetBytes(state[0]!);
        }
    }

    public Task<IntegrationOAuthCallback> WaitAsync(CancellationToken ct) => _callback.Task.WaitAsync(ct);

    internal async Task ReceiveAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!HttpMethods.IsGet(context.Request.Method) || context.Request.Path != path)
        { context.Response.StatusCode = 404; return; }
        byte[]? expected;
        lock (_gate) expected = _state;
        if (expected is null) { context.Response.StatusCode = 400; return; }
        if (_callback.Task.IsCompleted) { context.Response.StatusCode = 409; return; }
        var query = context.Request.Query;
        var validState = query.TryGetValue("state", out var state) && state.Count == 1 && state[0] is { } actual
            && CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(actual));
        var validCode = query.TryGetValue("code", out var code) && code.Count == 1 && !string.IsNullOrEmpty(code[0]);
        var issuer = query["iss"];
        if (!validState || !validCode || issuer.Count > 1 || query.ContainsKey("error") || query.ContainsKey("error_description"))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Authorization callback was rejected.", context.RequestAborted);
            _callback.TrySetException(new IntegrationAuthorizationException("Authorization callback was rejected."));
            return;
        }
        await context.Response.WriteAsync("Authorization callback received. You may close this window.", context.RequestAborted);
        _callback.TrySetResult(new(code[0]!, state[0]!, issuer.Count == 1 ? issuer[0] : null));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_close ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        _callback.TrySetCanceled();
        // Observe a rejected callback even when setup failed before the completion worker started.
        if (_callback.Task.IsFaulted) _ = _callback.Task.Exception;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        try { await app.StopAsync(deadline.Token); }
        finally { await app.DisposeAsync(); }
    }
}
