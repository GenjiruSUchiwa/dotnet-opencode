namespace OpenCode.Core.Session;

using OpenCode.Core.Event;
using OpenCode.Schema;

/// <summary>Startup scheduling observations, not claims that model execution succeeded.</summary>
public sealed record SessionRecoveryReport(
    IReadOnlyList<SessionId> Scheduled,
    IReadOnlyList<SessionId> Exhausted,
    IReadOnlyList<SessionId> Skipped,
    IReadOnlyDictionary<SessionId, string> Blocked);

internal sealed class SessionAlreadyOwnedException : InvalidOperationException;
internal sealed class RecoveryNotScheduledException(RestartPreparation preparation) : Exception
{
    internal RestartPreparation Preparation { get; } = preparation;
}
