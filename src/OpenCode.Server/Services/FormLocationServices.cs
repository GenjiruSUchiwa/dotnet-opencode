namespace OpenCode.Server.Services;

using OpenCode.Core.Database;
using OpenCode.Core.Forms;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Schema;

public sealed class FormLocationLease(IPermissionLocationLease lease, FormService forms) : IAsyncDisposable
{
    public LocationInfo Location => lease.Location;
    public FormService Forms { get; } = forms;
    public ValueTask DisposeAsync() => lease.DisposeAsync();
}

/// <summary>Forms borrow the authoritative Location lifetime; IDs never index a process-global form catalog.</summary>
public sealed class FormLocationServices(IPermissionLocationServices locations, SessionStore sessions, IEventFeedService feed)
    : IHostedService, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocationRef, FormService> _forms = [];
    private bool _closed;

    public async ValueTask<FormLocationLease?> AcquireAsync(LocationRef reference, bool loadedOnly = false, CancellationToken ct = default)
    {
        var lease = loadedOnly ? await locations.TryAcquireLoadedAsync(reference, ct) : await locations.AcquireAsync(reference, ct);
        if (lease is null) return null;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                return new FormLocationLease(lease, ForLocation(lease.Location));
            }
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    /// <summary>Factory callback before MCP construction/observation; does not acquire or reenter the Location map.</summary>
    public FormService ForLocation(LocationInfo location)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var key = PermissionLocationMap.Canonical(new(location.Directory, location.WorkspaceId));
            if (!_forms.TryGetValue(key, out var forms)) _forms[key] = forms = new FormService(key, feed.Publish, sessions.Clock);
            return forms;
        }
    }

    /// <summary>MCP adapters pass form fields or an external URL field explicitly. No implicit approval or browser launch.</summary>
    public async Task<FormState> AskAsync(LocationRef reference, string owner, FormCreatePayload input, CancellationToken ct = default)
    {
        if (owner != "global")
        {
            var session = await sessions.GetSessionAsync(SessionId.FromExisting(owner), ct) ?? throw new SessionMutationNotFoundException(SessionId.FromExisting(owner));
            if (PermissionLocationMap.Canonical(session.Location) != PermissionLocationMap.Canonical(reference))
                throw new ArgumentException("The form owner belongs to a different Location.", nameof(owner));
        }
        await using var lease = await AcquireAsync(reference, ct: ct) ?? throw new InvalidOperationException("The form Location is unavailable.");
        return await lease.Forms.AskAsync(owner, input, ct);
    }

    public void Invalidate(LocationRef reference)
    {
        lock (_gate)
            if (_forms.Remove(PermissionLocationMap.Canonical(reference), out var forms)) forms.Dispose();
    }

    public Task StartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            foreach (var forms in _forms.Values) forms.Dispose();
            _forms.Clear();
        }
    }
}
