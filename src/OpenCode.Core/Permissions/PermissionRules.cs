namespace OpenCode.Core.Permissions;

using System.Text.RegularExpressions;
using OpenCode.Schema;

public static class PermissionRules
{
    public static bool Match(string input, string pattern)
    {
        var escaped = Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*", ".*").Replace("\\?", ".");
        // "git *" also matches the bare command "git" in the source wildcard algebra.
        if (escaped.EndsWith("\\ .*", StringComparison.Ordinal)) escaped = escaped[..^4] + "( .*)?";
        return Regex.IsMatch(input.Replace('\\', '/'), "^" + escaped + "$",
            RegexOptions.Singleline | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant |
            (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1));
    }

    public static PermissionRule Evaluate(string action, string resource, params IReadOnlyList<PermissionRule>[] rulesets) =>
        rulesets.SelectMany(rules => rules).LastOrDefault(rule => Match(action, rule.Action) && Match(resource, rule.Resource))
        ?? new PermissionRule(action, "*", PermissionEffect.Ask);
}

public interface IPermissionRuleSource
{
    /// <summary>Reject unknown Sessions; missing agents return deny-all. Must reflect current Session/Agent configuration.</summary>
    ValueTask<IReadOnlyList<PermissionRule>> GetAsync(SessionId session, AgentId? agent, CancellationToken ct);
}

/// <summary>Explicit host-managed Location configuration. Not a substitute for wiring the Session/Agent store.</summary>
public sealed class LocalPermissionRules : IPermissionRuleSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, AgentId?> _sessions = [];
    private readonly Dictionary<AgentId, PermissionRule[]> _agents = [];

    public void AddSession(SessionId session, AgentId? agent = null) { lock (_gate) _sessions[session] = agent; }
    public void RemoveSession(SessionId session) { lock (_gate) _sessions.Remove(session); }
    public void SetAgent(AgentId agent, IEnumerable<PermissionRule> rules)
    {
        var snapshot = rules.ToArray();
        if (snapshot.Any(rule => rule is null || rule.Action is null || rule.Resource is null || !Enum.IsDefined(rule.Effect)))
            throw new ArgumentException("Invalid permission rule.", nameof(rules));
        lock (_gate) _agents[agent] = snapshot;
    }
    public void RemoveAgent(AgentId agent) { lock (_gate) _agents.Remove(agent); }

    public ValueTask<IReadOnlyList<PermissionRule>> GetAsync(SessionId session, AgentId? agent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var selected)) throw new PermissionSessionNotFoundException(session);
            return ValueTask.FromResult<IReadOnlyList<PermissionRule>>((agent ?? selected) is { } id && _agents.TryGetValue(id, out var rules)
                ? Array.AsReadOnly(rules) : new[] { new PermissionRule("*", "*", PermissionEffect.Deny) });
        }
    }
}

public interface IPermissionGrantStore
{
    /// <summary>True only if AddAsync commits grants to storage that survives process restart.</summary>
    bool Persistent { get; }
    ValueTask<IReadOnlyList<PermissionRule>> ListAsync(string projectId, CancellationToken ct);
    ValueTask AddAsync(string projectId, string action, IReadOnlyList<string> resources, CancellationToken ct);
}

/// <summary>Explicitly process-local grants. Hosts needing durable "always" replies must inject a persistent store.</summary>
public sealed class MemoryPermissionGrantStore : IPermissionGrantStore
{
    public bool Persistent => false;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<PermissionRule>> _projects = new(StringComparer.Ordinal);
    public ValueTask<IReadOnlyList<PermissionRule>> ListAsync(string projectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult<IReadOnlyList<PermissionRule>>(_projects.TryGetValue(projectId, out var rules) ? rules.ToArray() : []);
    }
    public ValueTask AddAsync(string projectId, string action, IReadOnlyList<string> resources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_projects.TryGetValue(projectId, out var rules)) _projects[projectId] = rules = [];
            foreach (var resource in resources)
            {
                var rule = new PermissionRule(action, resource, PermissionEffect.Allow);
                if (!rules.Contains(rule)) rules.Add(rule);
            }
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class PermissionSessionNotFoundException(SessionId session) : Exception($"Session not found: {session}")
{
    public SessionId SessionId { get; } = session;
}
/// <summary>A policy denial, not a user's rejection of a pending request. Tool leaves may report it to the model.</summary>
public sealed class PermissionBlockedException(string action, IReadOnlyList<string> resources, IReadOnlyList<PermissionRule> rules, string? reason = null)
    : Exception(reason ?? $"Permission denied: {action}")
{
    public string Action { get; } = action;
    public IReadOnlyList<string> Resources { get; } = resources;
    public IReadOnlyList<PermissionRule> Rules { get; } = rules;
    public string Detail => $"{Message}\nAction: {Action}\nResources: {string.Join(", ", Resources)}" +
        (Rules.Count == 0 ? "" : "\nApplicable policy: " + string.Join("; ", Rules.Select(rule =>
            $"{rule.Action} {rule.Resource}: {rule.Effect.ToString().ToLowerInvariant()}")));
}
/// <summary>Control-flow rejection. Must never be converted to model-visible tool output.</summary>
public sealed class PermissionDeclinedException() : OperationCanceledException("Permission declined.");
public sealed class PermissionCorrectedException(string feedback) : Exception(feedback)
{
    public string Feedback { get; } = feedback;
}
