namespace OpenCode.Core.Session;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Shell;
using OpenCode.Schema;

/// <summary>DI adapter for the real Shell actor. All durable writes stay in Session's event/admission boundaries.</summary>
public sealed class SessionShellLifecycle(SessionStore sessions) : ISessionShellLifecycle
{
    public Task StartedAsync(SessionId sessionId, EventId? eventId, ShellInfo shell, CancellationToken ct) =>
        sessions.PublishShellStartedAsync(sessionId, shell, ct, eventId);

    public Task EndedAsync(SessionId sessionId, ShellInfo shell, ShellOutput output, CancellationToken ct) =>
        sessions.PublishShellEndedAsync(sessionId, shell, output, ct);

    public Task NotifyAsync(SessionId sessionId, ShellNotification notification, CancellationToken ct) =>
        SessionRunCoordinator.AdmitAsync(sessionId, async () =>
        {
            // Look up current Session placement/existence, never route by the process's original cwd.
            _ = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
            var id = notification.Metadata.TryGetValue("shellID", out var value)
                ? CompletionId(sessionId, ShellId.FromExisting(value.GetString() ?? throw new JsonException("Shell notification identity must be a shell ID.")))
                : MessageId.Create(); // A failed spawn has no shell identity; do not guess one from its command.
            var existing = await sessions.ReconcileInboxAsync(sessionId, id, "synthetic", ct: ct).ConfigureAwait(false);
            if (existing is not null) return existing;
            return await sessions.AdmitInboxAsync(sessionId, id,
                new SyntheticInboxPayload(notification.Text, notification.Description,
                    notification.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)), ct: ct).ConfigureAwait(false);
        }, ct);

    // Native correlation policy for an actor callback that supplies Shell ID rather than Message ID.
    // The explicit namespace avoids collisions with generated/event-derived message identifiers.
    private static MessageId CompletionId(SessionId sessionId, ShellId shellId) => MessageId.FromExisting("msg_shell_" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Value + "\0" + shellId.Value))));
}
