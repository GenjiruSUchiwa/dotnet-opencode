namespace OpenCode.Core.Pty;

using OpenCode.Schema;

public sealed record PtyLocationScope(LocationInfo Location, PtyService Pty);

/// <summary>Host-owned PTYs keyed by resolved Location identity, independent of request/Session lifetimes.</summary>
public sealed class PtyLocationMap(
    Func<LocationInfo, string> resolveShell,
    Func<LocationInfo, PtyService, IAsyncDisposable> attachEvents) : IAsyncDisposable
{
    private sealed record Entry(PtyLocationScope Scope, IAsyncDisposable Events)
    {
        internal Task? Closing;
    }

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<LocationRef, Entry> entries = [];
    private Task? shutdown;

    public async ValueTask<PtyLocationScope> GetAsync(LocationInfo location, CancellationToken ct = default)
    {
        var key = Key(new(location.Directory, location.WorkspaceId));
        if (key.WorkspaceId is not null)
            throw new NotSupportedException("Explicit workspace PTY placement is not implemented by the local runtime.");
        await gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(shutdown is not null, this);
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Closing is not null) throw new ObjectDisposedException("PTY Location");
                return entry.Scope;
            }
            var runtime = new PtyService(key.Directory, () => resolveShell(location));
            try
            {
                var scope = new PtyLocationScope(location, runtime);
                entries.Add(key, new(scope, attachEvents(location, runtime)));
                return scope;
            }
            catch { await runtime.DisposeAsync().ConfigureAwait(true); throw; }
        }
        finally { gate.Release(); }
    }

    public async ValueTask InvalidateAsync(LocationRef location, CancellationToken ct = default)
    {
        var key = Key(location);
        Task? closing = null;
        await gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (entries.TryGetValue(key, out var entry)) closing = entry.Closing ??= CloseAsync(key, entry);
        }
        finally { gate.Release(); }
        if (closing is not null) await closing.ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        Task closing;
        await gate.WaitAsync().ConfigureAwait(true);
        try
        {
            shutdown ??= Task.WhenAll(entries.Select(pair => pair.Value.Closing ??= CloseAsync(pair.Key, pair.Value)).ToArray());
            closing = shutdown;
        }
        finally { gate.Release(); }
        await closing.ConfigureAwait(true);
    }

    private async Task CloseAsync(LocationRef key, Entry entry)
    {
        try { await entry.Scope.Pty.DisposeAsync().ConfigureAwait(true); }
        finally
        {
            try { await entry.Events.DisposeAsync().ConfigureAwait(true); }
            finally
            {
                await gate.WaitAsync().ConfigureAwait(true);
                try { entries.Remove(key); }
                finally { gate.Release(); }
            }
        }
    }

    private static LocationRef Key(LocationRef location)
    {
        if (!Path.IsPathFullyQualified(location.Directory)) throw new ArgumentException("A resolved absolute Location directory is required.", nameof(location));
        return new(OperatingSystem.IsWindows() ? Path.GetFullPath(location.Directory) : location.Directory, location.WorkspaceId);
    }
}
