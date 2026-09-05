namespace OpenCode.Cli.Tui.Forms;

using OpenCode.Schema;

/// <summary>Typed transport boundary while the client owns canonical HTTP paths and serialization.</summary>
public sealed record SessionFormTransport(
    Func<string, LocationRef, CancellationToken, Task<IReadOnlyList<FormInfo>>> List,
    Func<FormReplyRequest, CancellationToken, Task> Reply,
    Func<FormCancelRequest, CancellationToken, Task> Cancel);

public sealed record SessionFormSnapshot(
    SessionId? Session, LocationRef Location, IReadOnlyList<PendingForm> Pending,
    IReadOnlyList<SessionId> Descendants, bool Loading, string? Error);
