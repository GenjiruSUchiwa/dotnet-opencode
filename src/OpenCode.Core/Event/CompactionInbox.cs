namespace OpenCode.Core.Event;

using OpenCode.Schema;
using Microsoft.EntityFrameworkCore;

internal sealed partial class SessionAdmission
{
    internal Task<SessionInboxItem> AdmitCompactionAsync(SessionId sessionId, MessageId id, InboxDeliveryMode delivery, CancellationToken ct) =>
        InboxSerialization.RunAsync(sessionId, () => new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            if (delivery is not (InboxDeliveryMode.Queue or InboxDeliveryMode.Steer)) throw new ArgumentOutOfRangeException(nameof(delivery));
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            var exact = await transaction.Db.Inbox.Where(row => row.id == id.Value)
                .Select(row => new { row.session_id, row.type, row.delivery, row.time_created }).FirstOrDefaultAsync(token).ConfigureAwait(true);
            if (exact is not null)
            {
                if (exact.session_id != sessionId.Value || exact.type != "compaction") throw new InboxLifecycleConflictException(id);
                return new SessionInboxItem(id, sessionId, Delivery(exact.delivery), new CompactionInboxPayload(), DateTimeOffset.FromUnixTimeMilliseconds(exact.time_created));
            }
            if (await transaction.Db.Messages.AnyAsync(row => row.id == id.Value, token).ConfigureAwait(true)) throw new InboxLifecycleConflictException(id);
            var pending = await transaction.Db.Inbox.Where(row => row.session_id == sessionId.Value && row.type == "compaction")
                .OrderBy(row => row.enqueued_seq).Select(row => new { row.id, row.delivery, row.time_created }).FirstOrDefaultAsync(token).ConfigureAwait(true);
            if (pending is not null) return new SessionInboxItem(MessageId.FromExisting(pending.id), sessionId, Delivery(pending.delivery),
                new CompactionInboxPayload(), DateTimeOffset.FromUnixTimeMilliseconds(pending.time_created));
            if (await transaction.Db.HighestProjectionAsync(sessionId.Value, token).ConfigureAwait(true) > await transaction.LatestSequenceAsync(sessionId.Value, token).ConfigureAwait(true))
                throw new NotSupportedException("Unsequenced history requires migration before compaction admission.");
            var item = new InboxItem(delivery, new CompactionInboxPayload());
            var committed = await transaction.AppendAsync(Enqueued, new SessionInboxEnqueuedEventData(sessionId, id, item), token).ConfigureAwait(true);
            return new SessionInboxItem(id, sessionId, delivery, item.Payload, DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created)));
        }, ct), ct);

    internal Task<MessageId?> StartCompactionAsync(SessionId sessionId, InboxPromotable scope, CancellationToken ct) =>
        InboxSerialization.RunAsync(sessionId, () => new EventStore(database).TransactAsync<MessageId?>(sessionId.Value, async (transaction, token) =>
        {
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            var next = await transaction.Db.Inbox.Where(row => row.session_id == sessionId.Value && (scope != InboxPromotable.Steer || row.delivery == "steer"))
                .OrderBy(row => row.delivery == "steer" ? 0 : 1).ThenBy(row => row.enqueued_seq).Select(row => new { row.id, row.type }).FirstOrDefaultAsync(token).ConfigureAwait(true);
            if (next is null || next.type != "compaction") return null;
            var id = MessageId.FromExisting(next.id);
            await transaction.AppendAsync(Delivered, new InboxRefData(sessionId, id), token).ConfigureAwait(true);
            await transaction.AppendAsync(CompactionProjector.Started, new SessionCompactionStartedEventData(sessionId, "manual", "", id), token).ConfigureAwait(true);
            return id;
        }, ct), ct);

    private static InboxDeliveryMode Delivery(string value) => value switch
    {
        "queue" => InboxDeliveryMode.Queue, "steer" => InboxDeliveryMode.Steer, _ => throw new System.Text.Json.JsonException("Invalid inbox delivery.")
    };
}
