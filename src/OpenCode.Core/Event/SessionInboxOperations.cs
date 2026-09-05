namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCode.Schema;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;

/// <summary>SessionInbox.Promotable: a safe step boundary or an idle input boundary.</summary>
public enum InboxPromotable { Steer, Input }

/// <summary>SessionInbox.LifecycleConflict, distinct from unavailable features and invalid JSON.</summary>
public sealed class InboxLifecycleConflictException(MessageId id)
    : InvalidOperationException("Inbox lifecycle conflict: the item is absent, has a different session/type, or cannot make this transition.")
{
    public MessageId Id { get; } = id;
}

internal sealed record InboxRefData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("inboxID"), JsonRequired] MessageId InboxId);

internal sealed record InboxDeliveryChangedData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("inboxID"), JsonRequired] MessageId InboxId,
    [property: JsonPropertyName("delivery"), JsonRequired] InboxDeliveryMode Delivery);

internal sealed partial class SessionAdmission
{
    internal static readonly DurableEventDefinition<InboxRefData> Delivered = new(
        "session.inbox.delivered", 1, "sessionID", SessionEventJsonContext.Default.InboxRefData, ProjectDeliveredAsync);
    internal static readonly DurableEventDefinition<InboxRefData> Cancelled = new(
        "session.inbox.cancelled", 1, "sessionID", SessionEventJsonContext.Default.InboxRefData, ProjectCancelledAsync);
    internal static readonly DurableEventDefinition<InboxDeliveryChangedData> DeliveryChanged = new(
        "session.inbox.delivery.changed", 1, "sessionID", SessionEventJsonContext.Default.InboxDeliveryChangedData, ProjectDeliveryChangedAsync);

    internal Task<SessionInboxItem?> ReconcileAsync(SessionId sessionId, MessageId id, string type,
        InboxDeliveryMode delivery, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            if (type is not ("user" or "synthetic"))
                throw new NotSupportedException("Only user and synthetic admission reconciliation is implemented.");
            if (delivery is not (InboxDeliveryMode.Steer or InboxDeliveryMode.Queue))
                throw new ArgumentOutOfRangeException(nameof(delivery));
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            return await ReconcileAsync(transaction, sessionId, id, type, delivery, token).ConfigureAwait(true);
        }, ct);

    internal async Task<IReadOnlyList<SessionInboxItem>> ListAsync(SessionId sessionId, CancellationToken ct)
    {
        var rows = await ReadPendingAsync(sessionId, null, false, ct).ConfigureAwait(true);
        return rows.Select(row => new SessionInboxItem(row.Id, sessionId, row.Delivery,
            DecodePayload(row.Type, row.Payload),
            DateTimeOffset.FromUnixTimeMilliseconds(row.Created))).ToArray();
    }

    internal async Task<SessionInboxItem?> NextPromotableAsync(SessionId sessionId, InboxPromotable scope, CancellationToken ct)
    {
        if (scope is not (InboxPromotable.Steer or InboxPromotable.Input))
            throw new ArgumentOutOfRangeException(nameof(scope));
        var steers = await ReadPendingAsync(sessionId, InboxDeliveryMode.Steer, true, ct).ConfigureAwait(true);
        var row = steers.FirstOrDefault() ?? (scope == InboxPromotable.Input
            ? (await ReadPendingAsync(sessionId, InboxDeliveryMode.Queue, true, ct).ConfigureAwait(true)).FirstOrDefault()
            : null);
        return row is null ? null : new SessionInboxItem(row.Id, sessionId, row.Delivery,
            DecodePayload(row.Type, row.Payload), DateTimeOffset.FromUnixTimeMilliseconds(row.Created));
    }

    internal Task CancelAsync(SessionId sessionId, MessageId id, CancellationToken ct) =>
        InboxSerialization.RunAsync(sessionId, () => new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            return await transaction.AppendAsync(Cancelled, new InboxRefData(sessionId, id), token).ConfigureAwait(true);
        }, ct), ct);

    internal Task ChangeDeliveryAsync(SessionId sessionId, MessageId id, InboxDeliveryMode delivery, CancellationToken ct)
    {
        if (delivery is not (InboxDeliveryMode.Steer or InboxDeliveryMode.Queue))
            throw new ArgumentOutOfRangeException(nameof(delivery));
        return InboxSerialization.RunAsync(sessionId, () => new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
            return await transaction.AppendAsync(DeliveryChanged, new InboxDeliveryChangedData(sessionId, id, delivery), token).ConfigureAwait(true);
        }, ct), ct);
    }

    internal Task<int> PromoteAsync(SessionId sessionId, InboxPromotable scope, CancellationToken ct)
    {
        if (scope is not (InboxPromotable.Steer or InboxPromotable.Input))
            throw new ArgumentOutOfRangeException(nameof(scope));
        return InboxSerialization.RunAsync(sessionId, async () =>
        {
            var steers = await ReadPendingAsync(sessionId, InboxDeliveryMode.Steer, false, ct).ConfigureAwait(true);
            if (steers.Count > 0 || scope == InboxPromotable.Steer)
                return await PublishAsync(sessionId, steers.TakeWhile(row => row.Type is not ("compaction" or "move")), ct).ConfigureAwait(true);

            var queued = await ReadPendingAsync(sessionId, InboxDeliveryMode.Queue, true, ct).ConfigureAwait(true);
            if (queued.Count == 0) return 0;
            // Control completion belongs to its domain operation, not generic input promotion.
            // In particular, consuming a move here would lose the requested move.
            if (queued[0].Type is "compaction" or "move")
                throw new NotSupportedException("The queued control requires its compaction or move domain operation before input can advance.");
            var promoted = await PublishAsync(sessionId, queued, ct).ConfigureAwait(true);
            var arrivedSteers = await ReadPendingAsync(sessionId, InboxDeliveryMode.Steer, false, ct).ConfigureAwait(true);
            return promoted + await PublishAsync(sessionId,
                arrivedSteers.TakeWhile(row => row.Type is not ("compaction" or "move")), ct).ConfigureAwait(true);
        }, ct);
    }

    private async Task<int> PublishAsync(SessionId sessionId, IEnumerable<PendingRow> rows, CancellationToken ct)
    {
        var count = 0;
        foreach (var row in rows)
        {
            await new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
            {
                await RequireSessionAsync(transaction, sessionId, token).ConfigureAwait(true);
                // A delivery retry reconciles from the projected message, without
                // publishing a second delivery event or retaining a consumed row.
                var pending = await transaction.Db.Inbox.Where(item => item.id == row.Id.Value)
                    .Select(item => new { item.session_id, item.type }).FirstOrDefaultAsync(token).ConfigureAwait(true);
                if (pending is not null)
                {
                    if (pending.session_id != sessionId.Value || pending.type != row.Type) throw new InboxLifecycleConflictException(row.Id);
                }
                else
                {
                    if (await ReconcileAsync(transaction, sessionId, row.Id, row.Type, row.Delivery, token).ConfigureAwait(true) is null)
                        throw new InboxLifecycleConflictException(row.Id);
                    return false;
                }
                await transaction.AppendAsync(Delivered, new InboxRefData(sessionId, row.Id), token).ConfigureAwait(true);
                return true;
            }, ct).ConfigureAwait(true);
            count++;
        }
        return count;
    }

    private sealed record PendingRow(MessageId Id, string Type, InboxDeliveryMode Delivery, JsonElement Payload, long Created);

    private async Task<IReadOnlyList<PendingRow>> ReadPendingAsync(SessionId sessionId,
        InboxDeliveryMode? delivery, bool first, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        if (!await db.Sessions.AnyAsync(row => row.id == sessionId.Value, ct).ConfigureAwait(true)) throw new InvalidOperationException("Inbox operations require an existing session.");
        var query = db.Inbox.Where(row => row.session_id == sessionId.Value);
        if (delivery is not null)
        {
            var mode = delivery == InboxDeliveryMode.Steer ? "steer" : "queue";
            query = query.Where(row => row.delivery == mode);
        }
        var rows = new List<PendingRow>();
        await foreach (var row in query.OrderBy(row => row.enqueued_seq)
            .Select(row => new { row.id, row.type, row.delivery, row.payload, row.time_created }).ReadAsync(first ? 1 : -1, ct).ConfigureAwait(true))
        {
            using var payload = JsonDocument.Parse(row.payload);
            rows.Add(new PendingRow(MessageId.FromExisting(row.id), row.type, row.delivery switch
            {
                "steer" => InboxDeliveryMode.Steer,
                "queue" => InboxDeliveryMode.Queue,
                _ => throw new JsonException("Invalid persisted inbox delivery.")
            }, payload.RootElement.Clone(), row.time_created));
        }
        return rows;
    }

    private static async Task RequireSessionAsync(EventTransaction transaction, SessionId sessionId, CancellationToken ct)
    {
        if (!await transaction.Db.Sessions.AnyAsync(row => row.id == sessionId.Value, ct).ConfigureAwait(true))
            throw new InvalidOperationException("Inbox operations require an existing session.");
    }

    private static async Task ProjectDeliveredAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(SessionEventJsonContext.Default.InboxRefData)!;
        var consumed = await SqliteIntrinsics.ConsumeInboxAsync(transaction.Db, data.SessionId.Value, data.InboxId.Value, ct).ConfigureAwait(true)
            ?? throw new InboxLifecycleConflictException(data.InboxId);
        var type = consumed.type;
        if (type is "compaction" or "move")
        {
            using var control = JsonDocument.Parse(consumed.payload);
            DecodePayload(type, control.RootElement);
            return;
        }
        if (type is not ("user" or "synthetic")) throw new NotSupportedException("Control delivery requires its compaction or move domain operation.");
        using var document = JsonDocument.Parse(consumed.payload);
        var decoded = DecodePayload(type, document.RootElement);
        var payload = JsonSerializer.SerializeToNode(decoded, OpenCodeJsonContext.Default.InboxPayload)!.AsObject();
        payload.Remove("type");
        payload["time"] = JsonSerializer.SerializeToNode(new MessageTime(DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created))),
            OpenCodeJsonContext.Default.MessageTime);
        // Drizzle's Timestamps supplies projection write time, not completion time.
        await SqliteIntrinsics.InsertMessageAsync(transaction.Db, data.InboxId.Value, data.SessionId.Value, type,
            checked((long)committed.Durable!.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), payload.ToJsonString(), ct).ConfigureAwait(true);
    }

    private static async Task ProjectCancelledAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(SessionEventJsonContext.Default.InboxRefData)!;
        if (await transaction.Db.Inbox.Where(row => row.id == data.InboxId.Value && row.session_id == data.SessionId.Value
            && (row.delivery == "queue" || row.delivery == "steer")).ExecuteDeleteAsync(ct).ConfigureAwait(true) != 1) throw new InboxLifecycleConflictException(data.InboxId);
    }

    private static async Task ProjectDeliveryChangedAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(SessionEventJsonContext.Default.InboxDeliveryChangedData)!;
        var to = data.Delivery == InboxDeliveryMode.Steer ? "steer" : "queue";
        var from = data.Delivery == InboxDeliveryMode.Steer ? "queue" : "steer";
        if (await transaction.Db.Inbox.Where(row => row.id == data.InboxId.Value && row.session_id == data.SessionId.Value && row.delivery == from)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.delivery, to), ct).ConfigureAwait(true) != 1) throw new InboxLifecycleConflictException(data.InboxId);
    }
}
