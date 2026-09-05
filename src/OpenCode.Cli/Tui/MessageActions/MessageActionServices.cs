namespace OpenCode.Cli.Tui.MessageActions;

using OpenCode.Client;
using OpenCode.Schema;

public readonly record struct MessageTarget(SessionId SessionId, MessageId MessageId);

/// <summary>Only supplied, functional operations appear in Message Actions.</summary>
public sealed record MessageActionServices(Action<Exception> ReportError)
{
    public Func<MessageTarget, Task>? Jump { get; init; }
    public Action<UserMessage>? RestorePrompt { get; init; }
    public Func<MessageTarget, CancellationToken, Task<SessionRevert>>? StageRevert { get; init; }
    public Func<MessageTarget, CancellationToken, Task>? Reconcile { get; init; }
    public Func<MessageTarget, CancellationToken, Task<SessionInfo>>? ForkBefore { get; init; }
    public Func<SessionInfo, UserMessage, Task>? OpenFork { get; init; }
    public Func<MessageTarget, bool>? CanMutate { get; init; }

    public static MessageActionServices FromClient(SessionHttpClient client, Action<Exception> reportError,
        Func<MessageTarget, CancellationToken, Task> reconcile) => new(reportError)
        {
            StageRevert = async (target, cancellationToken) =>
                (await client.StageRevertAsync(target.SessionId, target.MessageId, ct: cancellationToken)).Data,
            Reconcile = reconcile
        };
}
