namespace OpenCode.Core.Event;

using OpenCode.Core.Session.Transfer;
using OpenCode.Schema;
using Microsoft.EntityFrameworkCore;

// Part of the existing admission owner, placed here to keep transfer changes isolated.
// Callers hold InboxSerialization; these methods must not acquire it recursively.
internal sealed partial class SessionAdmission
{
    internal Task<SessionInboxItem> AdmitMoveLockedAsync(SessionId sessionId, MessageId id,
        MoveInboxPayload payload, InboxDeliveryMode delivery, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            if (delivery is not (InboxDeliveryMode.Steer or InboxDeliveryMode.Queue)) throw new ArgumentOutOfRangeException(nameof(delivery));
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            // Source reconcile accepts a matching pending move, ignores its retried payload/mode,
            // and rejects cross-session/type reuse. There is no retained consumed-control ledger.
            var existing = await ReconcileAsync(transaction, sessionId, id, "move", delivery, token).ConfigureAwait(true);
            if (existing is not null) return existing;
            if (payload.Location.WorkspaceId is not null) throw new NotSupportedException("Workspace movement requires Location routing.");
            await RequireMoveSequenceAsync(transaction, sessionId, token).ConfigureAwait(true);
            var committed = await transaction.AppendAsync(Enqueued,
                new SessionInboxEnqueuedEventData(sessionId, id, new InboxItem(delivery, payload)), token).ConfigureAwait(true);
            return new SessionInboxItem(id, sessionId, delivery, payload,
                DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created)));
        }, ct);

    internal Task MoveFromMissingSourceLockedAsync(SessionId sessionId, MoveInboxPayload payload, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            await RequireMoveSequenceAsync(transaction, sessionId, token).ConfigureAwait(true);
            var ids = (await transaction.Db.Inbox.Where(row => row.session_id == sessionId.Value && row.type == "move")
                .OrderBy(row => row.enqueued_seq).Select(row => row.id).ToListAsync(token).ConfigureAwait(true)).Select(MessageId.FromExisting).ToArray();
            foreach (var id in ids) await transaction.AppendAsync(Cancelled, new InboxRefData(sessionId, id), token).ConfigureAwait(true);
            await transaction.AppendAsync(MoveProjector.Moved,
                new SessionMovedData(sessionId, payload.Location, payload.ProjectId, payload.Subpath), token).ConfigureAwait(true);
            return true;
        }, ct);

    internal async Task<SessionMoveDelivery?> DeliverMoveLockedAsync(SessionId sessionId, InboxPromotable scope,
        Func<CancellationToken, Task> closeSourceTransport, CancellationToken ct)
    {
        var next = await NextPromotableAsync(sessionId, scope, ct).ConfigureAwait(true);
        if (next?.Payload is not MoveInboxPayload payload) return null;
        if (payload.Location.WorkspaceId is not null) throw new NotSupportedException("Workspace movement requires Location routing.");
        // Keep pending input intact on transport closure failure or cancellation.
        await closeSourceTransport(ct).ConfigureAwait(true);
        return await new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            await RequireMoveSequenceAsync(transaction, sessionId, token).ConfigureAwait(true);
            await transaction.AppendAsync(Delivered, new InboxRefData(sessionId, next.Id), token).ConfigureAwait(true);
            await transaction.AppendAsync(MoveProjector.Moved,
                new SessionMovedData(sessionId, payload.Location, payload.ProjectId, payload.Subpath), token).ConfigureAwait(true);
            return new SessionMoveDelivery(next);
        }, ct).ConfigureAwait(true);
    }

    private static async Task RequireMoveSequenceAsync(EventTransaction transaction, SessionId sessionId, CancellationToken ct)
    {
        if (await transaction.Db.HighestProjectionAsync(sessionId.Value, ct).ConfigureAwait(true) > await transaction.LatestSequenceAsync(sessionId.Value, ct).ConfigureAwait(true))
            throw new NotSupportedException("Unsequenced history requires canonical migration before movement.");
    }
}
