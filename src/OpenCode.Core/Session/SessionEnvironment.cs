namespace OpenCode.Core.Session;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using OpenCode.Schema;

/// <summary>
/// Process-local session environment snapshots from core/session/environment.ts.
/// Register once per host. Values are neither persisted nor published as events.
/// The Shell domain can consume this replacement for implicit-local sessions;
/// this service does not mutate the process environment or launch commands.
/// </summary>
public sealed class SessionEnvironment
{
    private readonly ConcurrentDictionary<SessionId, ImmutableDictionary<string, string>> _variables = new();

    public IReadOnlyDictionary<string, string>? Get(SessionId sessionId) => _variables.GetValueOrDefault(sessionId);

    public void Set(SessionId sessionId, IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (variables.Any(pair => pair.Value is null))
            throw new ArgumentException("Environment values must be strings.", nameof(variables));
        // Replacement, not merging: an empty record is a meaningful empty override.
        _variables[sessionId] = variables.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public void Clear(SessionId sessionId) => _variables.TryRemove(sessionId, out _);
}
