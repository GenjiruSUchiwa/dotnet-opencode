namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InboxRefData))]
[JsonSerializable(typeof(InboxDeliveryChangedData))]
internal partial class SessionEventJsonContext : JsonSerializerContext;

internal sealed partial class SessionAdmission(IDatabase database)
{
    internal static readonly DurableEventDefinition<SessionInboxEnqueuedEventData> Enqueued = new(
        "session.inbox.enqueued", 1, "sessionID", OpenCodeJsonContext.Default.SessionInboxEnqueuedEventData, ProjectEnqueuedAsync);

    /// <summary>
    /// Admits already prepared user or synthetic input. This is not
    /// Session.prompt: it does not prepare attachments, commit reverts, or wake execution.
    /// </summary>
    internal Task<SessionInboxItem> AdmitAsync(SessionId sessionId, MessageId id, InboxPayload payload,
        InboxDeliveryMode delivery, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            var type = payload switch
            {
                UserInboxPayload => "user",
                SyntheticInboxPayload => "synthetic",
                _ => throw new NotSupportedException("Control admission requires the upstream compaction/move lifecycle and canonical payload contracts.")
            };
            if (delivery is not (InboxDeliveryMode.Steer or InboxDeliveryMode.Queue))
                throw new ArgumentOutOfRangeException(nameof(delivery));
            await RequireSessionAsync(transaction, sessionId, token);

            // First admission wins before serializing or validating a retried payload.
            // Delivered identities are reconciled from messages, not retained events.
            var existing = await ReconcileAsync(transaction, sessionId, id, type, delivery, token);
            if (existing is not null) return existing;

            var messageSeq = await transaction.Db.Messages.Where(row => row.session_id == sessionId.Value).MaxAsync(row => (long?)row.seq, token);
            if (messageSeq is { } seq && seq > await transaction.LatestSequenceAsync(sessionId.Value, token))
                throw new NotSupportedException("Unsequenced direct-SQL history requires a canonical migration before durable admission.");
            if (payload is UserInboxPayload)
            {
                if (await transaction.Db.Sessions.Where(row => row.id == sessionId.Value).Select(row => row.revert).FirstOrDefaultAsync(token) is not null)
                    throw new NotSupportedException("Commit the staged revert through its durable domain operation before admitting new input.");
            }

            RequireSupportedPayload(payload);
            var data = new SessionInboxEnqueuedEventData(sessionId, id, new InboxItem(delivery, payload));
            var committed = await transaction.AppendAsync(Enqueued, data, token);
            return new SessionInboxItem(id, sessionId, delivery,
                committed.Data.Deserialize(OpenCodeJsonContext.Default.SessionInboxEnqueuedEventData)!.Item.Payload,
                DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created)));
        }, ct);

    private static async Task<SessionInboxItem?> ReconcileAsync(EventTransaction transaction, SessionId sessionId,
        MessageId id, string type, InboxDeliveryMode delivery, CancellationToken ct)
    {
        var pending = await transaction.Db.Inbox.FirstOrDefaultAsync(row => row.id == id.Value, ct);
        if (pending is not null)
        {
                if (pending.session_id != sessionId.Value || pending.type != type)
                    throw new InboxLifecycleConflictException(id);
                using var document = JsonDocument.Parse(pending.payload);
                return new SessionInboxItem(id, sessionId, pending.delivery switch
                {
                    "steer" => InboxDeliveryMode.Steer,
                    "queue" => InboxDeliveryMode.Queue,
                    _ => throw new JsonException("Invalid persisted inbox delivery.")
                }, DecodePayload(type, document.RootElement), DateTimeOffset.FromUnixTimeMilliseconds(pending.time_created));
        }

        var delivered = await transaction.Db.Messages.Where(row => row.id == id.Value).Select(row => new { row.session_id, row.type, row.data }).FirstOrDefaultAsync(ct);
        if (delivered is null) return null;
        if (delivered.session_id != sessionId.Value || delivered.type != type)
            throw new InboxLifecycleConflictException(id);
        using var stored = JsonDocument.Parse(delivered.data);
        return new SessionInboxItem(id, sessionId, delivery, DecodePayload(type, stored.RootElement),
            DateTimeOffset.FromUnixTimeMilliseconds(stored.RootElement.GetProperty("time").GetProperty("created").GetInt64()));
    }

    private static InboxPayload DecodePayload(string type, JsonElement data)
    {
        var payload = type switch
        {
            "user" => (InboxPayload?)data.Deserialize(OpenCodeJsonContext.Default.UserInboxPayload),
            "synthetic" => data.Deserialize(OpenCodeJsonContext.Default.SyntheticInboxPayload),
            "compaction" => data.Deserialize(OpenCodeJsonContext.Default.CompactionInboxPayload),
            "move" => data.Deserialize(OpenCodeJsonContext.Default.MoveInboxPayload),
            _ => throw new JsonException("Unknown persisted inbox type.")
        } ?? throw new JsonException("Missing inbox payload.");
        RequireSupportedPayload(payload);
        return payload;
    }

    private static void RequireSupportedPayload(InboxPayload payload)
    {
        if (payload is UserInboxPayload user)
        {
            if (user.Text is null) throw new JsonException("User inbox payload requires text.");
        }
        if (payload is SyntheticInboxPayload { Text: null })
            throw new JsonException("Synthetic inbox payload requires text.");
    }

    private static async Task ProjectEnqueuedAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(OpenCodeJsonContext.Default.SessionInboxEnqueuedEventData)!;
        if (await transaction.Db.Messages.AnyAsync(row => row.id == data.InboxId.Value, ct)) throw new InboxLifecycleConflictException(data.InboxId);
        if (await SqliteIntrinsics.InsertInboxAsync(transaction.Db, data.InboxId.Value, data.SessionId.Value, data.Item.Type,
            JsonSerializer.Serialize(data.Item.Payload, OpenCodeJsonContext.Default.InboxPayload),
            data.Item.Delivery == InboxDeliveryMode.Steer ? "steer" : "queue", checked((long)committed.Durable!.Seq), committed.Created, ct) != 1)
            throw new InboxLifecycleConflictException(data.InboxId);
        await SqliteIntrinsics.TouchSessionAsync(transaction.Db, data.SessionId.Value, committed.Created, ct);
    }
}
