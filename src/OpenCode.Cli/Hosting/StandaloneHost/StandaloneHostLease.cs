namespace OpenCode.Cli.Hosting;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Client;
using OpenCode.Server;

/// <summary>Owns one private, ready HTTP host. Its endpoint is a credential-bearing
/// capability: do not log it or publish it in managed service registration.</summary>
public sealed class StandaloneHostLease : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly Lock _gate = new();
    private Task? _shutdown;

    private StandaloneHostLease(WebApplication application, ServiceEndpoint endpoint)
    {
        _application = application;
        Endpoint = endpoint;
    }

    public ServiceEndpoint Endpoint { get; }
    public CancellationToken Stopping => _application.Lifetime.ApplicationStopping;

    /// <summary>The token cancels startup. After return, the caller owns an await-using
    /// scope and cancels its clients before disposing this lease. Cancellation must
    /// not close the listener before a command can send its Session interrupt.</summary>
    public static async Task<StandaloneHostLease> StartAsync(CancellationToken ct = default, TimeProvider? clock = null)
    {
        ct.ThrowIfCancellationRequested();
        // Match source's 32-byte base64url credential; no environment/argv/config write.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var application = ServerHost.CreateStandaloneApp(password, clock);
        try
        {
            // The public factory's hosted lifecycle validates storage and marks the
            // real API ready before StartAsync returns. Do not invent readiness.
            await application.StartAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count != 1
                || !Uri.TryCreate(addresses.Single(), UriKind.Absolute, out var address)
                || address.Scheme != "http" || address.Host != "127.0.0.1" || address.Port <= 0
                || address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
                throw new InvalidOperationException("The private server did not expose one bound IPv4 loopback endpoint.");
            return new(application, new ServiceEndpoint(address.GetLeftPart(UriPartial.Authority), password));
        }
        catch (Exception startupError)
        {
            try { await ShutdownAsync(application).ConfigureAwait(false); }
            catch (Exception shutdownError)
            {
                throw new AggregateException("Private server startup and cleanup failed.", startupError, shutdownError);
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        // Repeated/concurrent disposal joins the same cleanup, including its error.
        lock (_gate) return new(_shutdown ??= ShutdownAsync(_application));
    }

    private static async Task ShutdownAsync(WebApplication application)
    {
        // Never pass the already-cancelled command token to owned shutdown. The
        // Server composition owns settling its jobs; no global interrupt or PID kill.
        try { await application.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { await application.DisposeAsync().ConfigureAwait(false); }
    }
}
