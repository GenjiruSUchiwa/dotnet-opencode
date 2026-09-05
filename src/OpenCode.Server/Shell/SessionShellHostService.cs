namespace OpenCode.Server.Shell;

using System.Collections.Concurrent;
using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Session;
using OpenCode.Core.Shell;
using OpenCode.Schema;

/// <summary>
/// Owns completion after HTTP disconnect. Durable Session publication/admission stays with the
/// injected Session lifecycle implementation; this is not a second model execution coordinator.
/// </summary>
public sealed class SessionShellHostService(SessionStore sessions, ShellLocationServices locations,
    ILogger<SessionShellHostService> log, ISessionShellLifecycle? lifecycle = null) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private long _sequence;

    public async Task RunAsync(SessionId sessionId, string command, EventId? eventId = null, CancellationToken ct = default)
    {
        var session = await sessions.GetSessionAsync(sessionId, ct) ?? throw new SessionMutationNotFoundException(sessionId);
        if (lifecycle is null) throw new NotSupportedException("Session shell execution requires the canonical Session shell event/admission adapter.");
        _shutdown.Token.ThrowIfCancellationRequested();
        var key = Interlocked.Increment(ref _sequence);
        var running = RunOwnedAsync(session, command, eventId, lifecycle, _shutdown.Token);
        _running[key] = running;
        _ = running.ContinueWith(completed =>
        {
            _running.TryRemove(key, out _);
            if (completed.IsFaulted)
            {
                _ = completed.Exception;
                log.LogWarning("A server-owned session shell operation failed. Its durable publication outcome must be checked before retrying.");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // A disconnected submitting client does not interrupt the operation or its Session facts.
        await running.WaitAsync(ct);
    }

    private async Task RunOwnedAsync(SessionInfo session, string command, EventId? eventId, ISessionShellLifecycle events, CancellationToken ct)
    {
        await using var location = await locations.AcquireAsync(session.Location, ct);
        ShellInfo started;
        try
        {
            started = await location.Shell.CreateAsync(new ShellCreateInput(command, 0, session.Location.Directory,
                new Dictionary<string, JsonElement>
                {
                    ["sessionID"] = JsonSerializer.SerializeToElement(session.Id.Value),
                    ["background"] = JsonSerializer.SerializeToElement(true)
                }), ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            await events.NotifyAsync(session.Id, new ShellNotification($"User shell command failed to start:\n{command}\n\n{error.Message}", command,
                new Dictionary<string, JsonElement>
                {
                    ["source"] = JsonSerializer.SerializeToElement("shell"), ["state"] = JsonSerializer.SerializeToElement("error")
                }), ct);
            throw;
        }
        await events.StartedAsync(session.Id, eventId, started, ct);
        var result = await location.Shell.ResultAsync(started, ct: ct);
        ShellOutput preview;
        try { preview = await location.Shell.OutputAsync(started.Id, new ShellOutputInput(Limit: 1024 * 1024), ct); }
        catch (Exception error) when (error is ShellNotFoundException or ShellOutputUnavailableException) { preview = ShellResult.Unavailable; }
        await events.EndedAsync(session.Id, result.Info, preview, ct);
        try { await events.NotifyAsync(session.Id, result.UserNotification(), ct); }
        catch (SessionMutationNotFoundException) { /* Source ignores a Session removed before synthetic completion admission. */ }
    }

    public Task StartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public async Task StopAsync(CancellationToken ct) => await DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await Task.WhenAll(_running.Values).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
