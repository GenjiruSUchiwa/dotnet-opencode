namespace OpenCode.Core.Locations;

using OpenCode.Core.Permissions;
using OpenCode.Schema;

/// <summary>The factory owns authoritative project identity and current Location-scoped dependencies.
/// Do not implement it with process-global default permissions. Failed construction must clean up its resources.</summary>
public interface IPermissionLocationFactory
{
    ValueTask<PermissionLocationScope> CreateAsync(LocationRef location, CancellationToken ct);
}

/// <summary>OwnedResources excludes shared stores. The map owns Permissions and the notification pump.</summary>
public sealed record PermissionLocationScope(LocationInfo Location, PermissionService Permissions, IAsyncDisposable? OwnedResources = null);

public sealed class PermissionLocationLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _released;
    public LocationInfo Location { get; }
    public PermissionService Permissions { get; }
    public bool PersistentGrants => Permissions.PersistentGrants;

    internal PermissionLocationLease(PermissionLocationScope scope, Func<ValueTask> release)
    {
        Location = scope.Location;
        Permissions = scope.Permissions;
        _release = release;
    }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? _release() : ValueTask.CompletedTask;
}

/// <summary>One host-owned authoritative map shared by endpoints and tools. Releasing a request lease does not
/// discard pending approvals. Explicit invalidation/shutdown closes a Location and settles its notification pump.</summary>
public sealed class PermissionLocationMap : IAsyncDisposable
{
    private sealed class Entry(PermissionLocationScope scope, Task pump)
    {
        public readonly PermissionLocationScope Scope = scope;
        public readonly Task Pump = pump;
        public readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Leases;
        public Task? Closing;
    }

    private readonly IPermissionLocationFactory _factory;
    private readonly Func<LocationInfo, PermissionService, Task> _dispatch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<LocationRef, Entry> _entries = [];
    private Task? _shutdown;

    /// <param name="dispatch">Start one notification consumer per service. It must drain until channel completion,
    /// not until an HTTP request ends. Server can supply PermissionEventBridge.RunAsync(CancellationToken.None).</param>
    public PermissionLocationMap(IPermissionLocationFactory factory, Func<LocationInfo, PermissionService, Task> dispatch)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public async ValueTask<PermissionLocationLease> AcquireAsync(LocationRef location, CancellationToken ct = default)
    {
        var key = Canonical(location);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (_entries.TryGetValue(key, out var existing)) return Lease(existing);
            var scope = await _factory.CreateAsync(key, ct);
            Task? pump = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (Canonical(new LocationRef(scope.Location.Directory, scope.Location.WorkspaceId)) != key)
                    throw new InvalidOperationException("Permission factory returned a different Location.");
                if (scope.Location.Project.Id.Value != scope.Permissions.ProjectId)
                    throw new InvalidOperationException("Permission factory returned a different project permission service.");
                if (scope.Permissions.IsDisposed || _entries.Values.Any(entry => ReferenceEquals(entry.Scope.Permissions, scope.Permissions)))
                    throw new InvalidOperationException("Each loaded Location must own a fresh permission service.");
                pump = DispatchAsync(scope);
                var entry = new Entry(scope, pump);
                var lease = Lease(entry);
                _entries.Add(key, entry);
                return lease;
            }
            catch
            {
                // A faulty factory must not dispose another Location's authoritative service.
                if (!_entries.Values.Any(entry => ReferenceEquals(entry.Scope.Permissions, scope.Permissions)))
                {
                    await Task.WhenAll(scope.Permissions.DisposeAsync().AsTask(), pump ?? Task.CompletedTask)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    if (scope.OwnedResources is not null) await scope.OwnedResources.DisposeAsync();
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Authoritative non-creating lookup. Closing Locations are unavailable, not falsely reported unloaded.</summary>
    public async ValueTask<PermissionLocationLease?> TryAcquireLoadedAsync(LocationRef location, CancellationToken ct = default)
    {
        var key = Canonical(location);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            return _entries.TryGetValue(key, out var entry) ? Lease(entry) : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call from the owning Location lifecycle, not while holding one of its leases.</summary>
    public async Task InvalidateAsync(LocationRef location, CancellationToken ct = default)
    {
        var key = Canonical(location);
        Task? closing = null;
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            if (_entries.TryGetValue(key, out var entry)) closing = BeginClose(key, entry);
        }
        finally { _gate.Release(); }
        if (closing is not null) await closing;
    }

    public async ValueTask DisposeAsync()
    {
        Task shutdown;
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _shutdown ??= Task.WhenAll(_entries.Select(pair => BeginClose(pair.Key, pair.Value)).ToArray());
            shutdown = _shutdown;
        }
        finally { _gate.Release(); }
        await shutdown;
    }

    public static LocationRef Canonical(LocationRef location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!Path.IsPathFullyQualified(location.Directory)) throw new ArgumentException("An authoritative absolute Location directory is required.");
        // Match the source's lexical Windows separator normalization; do not introduce a realpath alias here.
        return new(OperatingSystem.IsWindows() ? Path.GetFullPath(location.Directory) : location.Directory, location.WorkspaceId);
    }

    private PermissionLocationLease Lease(Entry entry)
    {
        if (entry.Closing is not null || entry.Scope.Permissions.IsDisposed || entry.Pump.IsCompleted)
            throw new NotSupportedException("The permission Location is closing or its event dispatcher has stopped.");
        entry.Leases++;
        return new(entry.Scope, async () =>
        {
            Task? closing = null;
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (--entry.Leases == 0)
                {
                    if (entry.Closing is not null) entry.Drained.TrySetResult();
                    else if (entry.Scope.Location.WorkspaceId is null && !Directory.Exists(entry.Scope.Location.Directory))
                        closing = BeginClose(Canonical(new(entry.Scope.Location.Directory)), entry);
                }
            }
            finally { _gate.Release(); }
            // Match LocationServiceMap's zero idle TTL for a missing local path.
            // Never dispose a still-borrowed Location or stat an explicit workspace.
            if (closing is not null) await closing;
        });
    }

    private Task BeginClose(LocationRef key, Entry entry)
    {
        if (entry.Closing is not null) return entry.Closing;
        if (entry.Leases == 0) entry.Drained.TrySetResult();
        return entry.Closing = CloseAsync(key, entry);
    }

    private async Task CloseAsync(LocationRef key, Entry entry)
    {
        try
        {
            await Task.WhenAll(entry.Scope.Permissions.DisposeAsync().AsTask(), entry.Pump);
        }
        finally
        {
            await entry.Drained.Task;
            try
            {
                if (entry.Scope.OwnedResources is not null) await entry.Scope.OwnedResources.DisposeAsync();
            }
            finally
            {
                await _gate.WaitAsync(CancellationToken.None);
                try { _entries.Remove(key); }
                finally { _gate.Release(); }
            }
        }
    }

    private async Task DispatchAsync(PermissionLocationScope scope)
    {
        try
        {
            await _dispatch(scope.Location, scope.Permissions);
            if (!scope.Permissions.IsDisposed) throw new InvalidOperationException("Permission dispatcher stopped before Location shutdown.");
        }
        finally
        {
            // A broken event bridge must not strand model calls awaiting a request that no user can see.
            await scope.Permissions.DisposeAsync();
        }
    }
}
