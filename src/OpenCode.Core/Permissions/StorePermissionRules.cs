namespace OpenCode.Core.Permissions;

using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Schema;

/// <summary>Reads authoritative Session state for every evaluation. The injected Location-scoped Agent resolver
/// must read current configuration, including default-Agent selection when both request and Session omit it.</summary>
public sealed class StorePermissionRules(
    SessionStore sessions,
    LocationRef location,
    Func<AgentId?, CancellationToken, ValueTask<AgentInfo?>> resolveAgent) : IPermissionRuleSource
{
    public async ValueTask<IReadOnlyList<PermissionRule>> GetAsync(SessionId session, AgentId? agent, CancellationToken ct)
    {
        var current = await sessions.GetSessionAsync(session, ct) ?? throw new PermissionSessionNotFoundException(session);
        if (PermissionLocationMap.Canonical(current.Location) != PermissionLocationMap.Canonical(location))
            throw new PermissionLocationMismatchException(session);
        var resolved = await resolveAgent(agent ?? (current.Agent is { } selected ? AgentId.FromExisting(selected) : null), ct);
        if (resolved is null) return [new PermissionRule("*", "*", PermissionEffect.Deny)];
        var rules = resolved.Permissions.ToArray();
        if (rules.Any(rule => rule is null || rule.Action is null || rule.Resource is null || !Enum.IsDefined(rule.Effect)))
            throw new InvalidOperationException("Agent resolution returned invalid permission rules.");
        return rules;
    }
}

public sealed class PermissionLocationMismatchException(SessionId session)
    : InvalidOperationException($"Session {session} no longer belongs to this permission Location; resolve its current Location again.")
{
    public SessionId SessionId { get; } = session;
}
