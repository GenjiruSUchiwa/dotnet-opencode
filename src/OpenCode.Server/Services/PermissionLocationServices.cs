namespace OpenCode.Server.Services;

using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Core.Projects;
using OpenCode.Schema;

public interface IPermissionLocationServices
{
    ValueTask<IPermissionLocationLease> AcquireAsync(string? directory, string? workspaceId, CancellationToken ct);
    ValueTask<IPermissionLocationLease> AcquireAsync(LocationRef location, CancellationToken ct);
    ValueTask<IPermissionLocationLease?> TryAcquireLoadedAsync(LocationRef location, CancellationToken ct);
}

public interface IPermissionLocationLease : IAsyncDisposable
{
    LocationInfo Location { get; }
    PermissionService Permissions { get; }
    bool PersistentGrants { get; }
}

/// <summary>
/// HTTP adapter to the same authoritative Core Location map used by tools.
/// Register the same instance as a singleton and IHostedService only after a real
/// IPermissionLocationFactory is available. Core owns policies, leases, and pumps.
/// </summary>
public sealed class PermissionLocationServices(PermissionLocationMap locations, IDatabase database) : IPermissionLocationServices, IHostedService
{
    private sealed class Lease(PermissionLocationLease lease) : IPermissionLocationLease
    {
        public LocationInfo Location => lease.Location;
        public PermissionService Permissions => lease.Permissions;
        public bool PersistentGrants => lease.PersistentGrants;
        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }

    public async ValueTask<IPermissionLocationLease> AcquireAsync(string? directory, string? workspaceId, CancellationToken ct)
    {
        var location = await ProjectDiscovery.ResolveAsync(database, directory, workspaceId, ct);
        return new Lease(await locations.AcquireAsync(new LocationRef(location.Directory, location.WorkspaceId), ct));
    }

    // Session routes use their persisted placement, not a fresh cwd/project or realpath alias.
    public async ValueTask<IPermissionLocationLease> AcquireAsync(LocationRef location, CancellationToken ct) =>
        new Lease(await locations.AcquireAsync(location, ct));

    public async ValueTask<IPermissionLocationLease?> TryAcquireLoadedAsync(LocationRef location, CancellationToken ct) =>
        await locations.TryAcquireLoadedAsync(location, ct) is { } lease ? new Lease(lease) : null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => locations.DisposeAsync().AsTask();
}
