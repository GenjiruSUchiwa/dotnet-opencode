namespace OpenCode.Server.Hosting;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenCode.Core.Database;

/// <summary>Private listener lifetime: no managed files, election, incumbent inspection, monitoring, or restart recovery.</summary>
internal sealed class StandaloneLifetime(string password, StartupDiagnostics diagnostics) : IHostedLifecycleService, IServerIdentity
{
    private readonly ServerCredentials _credentials = new(password);
    private string _state = "starting";
    internal WebApplication Application { private get; set; } = null!;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? Url { get; private set; }
    public string State => Volatile.Read(ref _state);
    public bool Authorized(HttpRequest request) => _credentials.Authorized(request);

    public Task StartingAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StartedAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            diagnostics.Phase("storage");
            using var connection = Application.Services.GetRequiredService<IDatabase>().CreateConnection();
            Url = Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                ?? throw new InvalidOperationException("The standalone listener has no unique bound address.");
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port <= 0)
                throw new InvalidOperationException("The standalone listener must bind an actual IPv4 loopback port.");
            Volatile.Write(ref _state, "ready");
            diagnostics.Complete("ready");
            return Task.CompletedTask;
        }
        catch (Exception error) { Volatile.Write(ref _state, "failed"); diagnostics.Fail(error); throw; }
    }
    public Task StoppingAsync(CancellationToken ct) { Volatile.Write(ref _state, "stopping"); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}

// The embedding CLI owns cancellation; do not install ConsoleLifetime signal handlers.
internal sealed class StandaloneHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
