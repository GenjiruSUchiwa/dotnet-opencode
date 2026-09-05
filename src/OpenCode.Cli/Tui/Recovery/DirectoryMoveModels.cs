namespace OpenCode.Cli.Tui.Recovery;

using OpenCode.Schema;

public enum SessionMovePhase { Submitting, Admitted, Unconfirmed, Rejected, LocationChanged }

/// <summary>Client observation of an ID-less move admission. This is not an inbox identity or delivery receipt.</summary>
public sealed record SessionMoveSnapshot(SessionId SessionId, LocationRef Origin, LocationRef Requested,
    InboxDeliveryMode Delivery, SessionMovePhase Phase, string? Error = null, LocationRef? Observed = null)
{
    public bool Pending => Phase is SessionMovePhase.Submitting or SessionMovePhase.Admitted or SessionMovePhase.Unconfirmed;
    public string Message => Phase switch
    {
        SessionMovePhase.Submitting => "Submitting move…",
        SessionMovePhase.Admitted => "Move admitted; waiting for an authoritative location change.",
        SessionMovePhase.Unconfirmed => "Move admission is unconfirmed. It will not be retried automatically; refresh the Session to inspect its location.",
        SessionMovePhase.LocationChanged => $"Session location changed to {Observed?.Directory}. This is an observed location, not a correlated control-delivery receipt.",
        _ => Error ?? "The move was rejected."
    };
}

public sealed record RecoveryDirectoryOption(string Directory, string Category, bool Worktree = false);
public sealed record RecoveryDirectoryPage(IReadOnlyList<RecoveryDirectoryOption> Directories, LocationRef? Parent = null);
