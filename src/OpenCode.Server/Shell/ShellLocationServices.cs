namespace OpenCode.Server.Shell;

using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Threading.Channels;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Core.Shell;
using OpenCode.Core.Tools;
using OpenCode.Schema;
using OpenCode.Server.Services;

/// <summary>Host supplies real configured shell selection, plugin readiness, and channel output placement.</summary>
public sealed record ShellHostOptions(
    Func<LocationInfo, CancellationToken, Task<string>> ResolveShell,
    Func<LocationInfo, CancellationToken, Task> FlushPlugins,
    Func<LocationInfo, string> OutputDirectory,
    bool CleanupOwnedOutput = false);

public sealed class ShellLocationLease(ToolLocationLease tools, ShellRuntime shell) : IAsyncDisposable
{
    public LocationInfo Location => tools.Location;
    public ShellRuntime Shell { get; } = shell;
    public ValueTask DisposeAsync() => tools.DisposeAsync();
}

/// <summary>Borrow the existing authoritative tool/permission Location. No independent permissions or model runner.</summary>
public sealed class ShellLocationServices(ShellHostOptions options, ToolLocationFactory factory, PermissionLocationMap locations,
    SessionEnvironment environments, IEventFeedService feed) : IHostedService, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocationRef, ShellRuntime> _shells = [];
    private readonly HashSet<string> _outputDirectories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _maintenance = new(1);
    private readonly CancellationTokenSource _cleanupStop = new();
    private readonly Channel<bool> _cleanupRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private Task? _cleanup;
    private Task? _schedule;
    private bool _closed;

    public async ValueTask<ShellLocationLease> AcquireAsync(LocationRef location, CancellationToken ct = default)
    {
        var tools = await factory.AcquireAsync(locations, location, ct);
        try { return new ShellLocationLease(tools, ForLocation(tools.Location)); }
        catch { await tools.DisposeAsync(); throw; }
    }

    /// <summary>Can be used by a Location tool composition to share this exact process registry.</summary>
    public ShellRuntime ForLocation(LocationInfo location)
    {
        var key = PermissionLocationMap.Canonical(new(location.Directory, location.WorkspaceId));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_shells.TryGetValue(key, out var existing)) return existing;
            var shell = new ShellRuntime(key, options.OutputDirectory(location),
                async (input, ct) =>
                {
                    var executable = await options.ResolveShell(location, ct);
                    if (!Path.IsPathFullyQualified(executable)) throw new NotSupportedException("The shell selector must return an absolute executable path.");
                    var cwd = Path.GetFullPath(string.IsNullOrEmpty(input.Cwd) ? location.Directory : input.Cwd, location.Directory);
                    var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
                    // User commands use source config-priority selection, not an invented ToolContext.
                    // Tool-originated commands enter ShellRuntime.CreateToolAsync with the scanned policy.
                    return new PreparedToolShell(executable, name == "cmd" ? ["/c", input.Command]
                        : name is "pwsh" or "powershell" ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", input.Command]
                        : ["-c", input.Command], cwd);
                }, ct => options.FlushPlugins(location, ct), environments.Get, feed.Publish, feed.Clock);
            _shells.Add(key, shell);
            _outputDirectories.Add(shell.OutputDirectory);
            if (options.CleanupOwnedOutput) _cleanupRequests.Writer.TryWrite(true);
            return shell;
        }
    }

    /// <summary>Await during Location close before the permission map waits for request/session-shell leases to drain.</summary>
    public async ValueTask InvalidateAsync(LocationRef location)
    {
        // Do not let retention lose the active-file protection between map removal and process stop.
        await _maintenance.WaitAsync();
        try
        {
            ShellRuntime? shell;
            lock (_gate) _shells.Remove(PermissionLocationMap.Canonical(location), out shell);
            if (shell is not null) await shell.DisposeAsync();
        }
        finally { _maintenance.Release(); }
    }

    /// <summary>Only enable for this host's private output tree; the default opencode data root is shared with other installations.</summary>
    public async Task CleanupOutputAsync(CancellationToken ct = default)
    {
        if (!options.CleanupOwnedOutput) throw new NotSupportedException("The host has not declared exclusive ownership of its shell output directories.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cleanupStop.Token);
        await _maintenance.WaitAsync(lifetime.Token);
        try
        {
            ShellRuntime[] shells;
            string[] directories;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                shells = _shells.Values.ToArray();
                directories = _outputDirectories.ToArray();
            }
            var protectedFiles = new List<string>();
            foreach (var shell in shells) protectedFiles.AddRange(await shell.OwnedCapturesAsync(lifetime.Token));
            await ShellOutputRetention.CleanupAsync(directories, protectedFiles, lifetime.Token, feed.Clock);
        }
        finally { _maintenance.Release(); }
    }

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (options.CleanupOwnedOutput)
        {
            _cleanup ??= CleanupLoopAsync();
            _schedule ??= ScheduleCleanupAsync();
            _cleanupRequests.Writer.TryWrite(true);
        }
        return Task.CompletedTask;
    }

    private async Task ScheduleCleanupAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), feed.Clock);
        try { while (await timer.WaitForNextTickAsync(_cleanupStop.Token)) _cleanupRequests.Writer.TryWrite(true); }
        catch (OperationCanceledException) when (_cleanupStop.IsCancellationRequested) { }
    }

    private async Task CleanupLoopAsync()
    {
        try
        {
            while (await _cleanupRequests.Reader.WaitToReadAsync(_cleanupStop.Token))
            {
                while (_cleanupRequests.Reader.TryRead(out _)) { }
                try { await CleanupOutputAsync(_cleanupStop.Token); }
                catch (Exception error) when (!_cleanupStop.IsCancellationRequested)
                { System.Diagnostics.Trace.TraceWarning("Shell output retention skipped ({0}).", error.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (_cleanupStop.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken ct) => await DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        await _cleanupStop.CancelAsync();
        _cleanupRequests.Writer.TryComplete();
        await Task.WhenAll(_cleanup ?? Task.CompletedTask, _schedule ?? Task.CompletedTask);
        await _maintenance.WaitAsync();
        try
        {
            ShellRuntime[] shells;
            lock (_gate) { if (_closed) return; _closed = true; shells = _shells.Values.ToArray(); _shells.Clear(); }
            await Task.WhenAll(shells.Select(shell => shell.DisposeAsync().AsTask()));
        }
        finally { _maintenance.Release(); }
    }
}

public static class ShellHostComposition
{
    /// <summary>Call after the shared tool Location graph; later hosted services stop before shell ownership closes.</summary>
    public static IServiceCollection AddShellServices(this IServiceCollection services, ShellHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.ResolveShell);
        ArgumentNullException.ThrowIfNull(options.FlushPlugins);
        ArgumentNullException.ThrowIfNull(options.OutputDirectory);
        services.AddSingleton(options);
        services.TryAddSingleton<ShellLocationServices>();
        services.AddHostedService<ShellLocationServices>(provider => provider.GetRequiredService<ShellLocationServices>());
        services.TryAddSingleton<SessionShellHostService>();
        services.AddHostedService<SessionShellHostService>(provider => provider.GetRequiredService<SessionShellHostService>());
        return services;
    }
}
