namespace OpenCode.Core.Locations;

using OpenCode.Core.Database;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

/// <summary>Explicit composition from authoritative Location identity, Session storage, and a live Agent resolver.
/// The resolver must apply current Location configuration on each call, not synthesize default allow rules.</summary>
public sealed class StorePermissionLocationFactory(
    SessionStore sessions,
    Func<LocationRef, CancellationToken, ValueTask<LocationInfo>> resolveLocation,
    Func<LocationInfo, AgentId?, CancellationToken, ValueTask<AgentInfo?>> resolveAgent,
    IPermissionGrantStore grants,
    Func<LocationInfo, IPermissionEvaluationHook?>? hooks = null) : IPermissionLocationFactory
{
    public async ValueTask<PermissionLocationScope> CreateAsync(LocationRef location, CancellationToken ct)
    {
        var info = await resolveLocation(location, ct);
        ct.ThrowIfCancellationRequested();
        var rules = new StorePermissionRules(sessions, location, (agent, cancellation) => resolveAgent(info, agent, cancellation));
        return new(info, new PermissionService(info.Project.Id.Value, rules, grants, hooks?.Invoke(info)));
    }
}
