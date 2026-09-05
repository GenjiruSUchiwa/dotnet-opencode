namespace OpenCode.Core.Event.Log;

using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

/// <summary>Retained Bus log/replay/claim semantics. Replay ownership is not Session execution ownership.</summary>
public sealed class DurableEventLog(IDatabase database, int pageSize = 512)
{
    public async IAsyncEnumerable<DurableLogItem> LogAsync(string aggregateId, double after = -1, bool follow = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregateId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (!double.IsFinite(after)) throw new ArgumentOutOfRangeException(nameof(after));
        using var subscription = follow ? DurableLogSignals.Subscribe(aggregateId) : null;
        var target = await LatestAsync(aggregateId, ct);
        var cursor = after;
        await foreach (var item in ReadThroughAsync(target, ct)) yield return new DurableLogItem.Entry(item);
        yield return new DurableLogItem.Synced(aggregateId, target >= 0 ? target : null);
        if (subscription is null) yield break;
        await foreach (var _ in subscription.Wake.Reader.ReadAllAsync(ct))
        {
            var through = await LatestAsync(aggregateId, ct);
            if (through <= cursor) continue;
            await foreach (var item in ReadThroughAsync(through, ct)) yield return new DurableLogItem.Entry(item);
        }

        async IAsyncEnumerable<OpenCodeEvent> ReadThroughAsync(long through, [EnumeratorCancellation] CancellationToken token)
        {
            while (true)
            {
                var page = await ReadPageAsync(aggregateId, cursor, through, token);
                if (page.Count == 0) yield break;
                // A reserved inherited prefix is not a missing event to fabricate. Only actual rows advance this cursor.
                cursor = page[^1].Seq;
                foreach (var row in page) yield return DurableReplayDefinitions.Decode(row, requireProjector: false).Event;
                if (cursor >= through) yield break;
            }
        }
    }

    public Task ClaimAsync(string aggregateId, string ownerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregateId);
        ArgumentNullException.ThrowIfNull(ownerId);
        return new EventStore(database).TransactAsync(aggregateId, async (transaction, token) =>
        {
            return await transaction.Db.Sequences.Where(row => row.aggregate_id == aggregateId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.owner_id, ownerId), token); // Unknown aggregate: update-only no-op.
        }, ct);
    }

    public Task ReplayAsync(SerializedDurableEvent input, DurableReplayOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var decoded = DurableReplayDefinitions.Decode(input, requireProjector: true);
        var replay = options ?? new DurableReplayOptions();
        return new EventStore(database).TransactAsync(input.AggregateId, async (transaction, token) =>
        {
            var sequence = await transaction.Db.Sequences.FirstOrDefaultAsync(row => row.aggregate_id == input.AggregateId, token);
            var latest = sequence?.seq ?? -1;
            var owner = sequence?.owner_id;
            if (replay.StrictOwner && !string.IsNullOrEmpty(owner) && owner != replay.OwnerId)
                throw Invalid($"Replay owner mismatch for aggregate {input.AggregateId}: expected {owner}, got {replay.OwnerId ?? "none"}");
            if (input.Seq <= latest)
            {
                var row = await transaction.Db.Events.Where(row => row.aggregate_id == input.AggregateId && row.seq == input.Seq)
                    .Select(row => new { row.id, row.type, row.created, row.data }).FirstOrDefaultAsync(token);
                var identical = false;
                if (row is not null)
                {
                    using var stored = JsonDocument.Parse(row.data);
                    identical = row.id == input.Id.Value && row.type == input.Type &&
                        row.created == (input.Created ?? 0) && Equal(stored.RootElement, decoded.Event.Data);
                }
                if (!identical) throw Invalid($"Replay diverged at aggregate {input.AggregateId} sequence {input.Seq}");
                if (!string.IsNullOrEmpty(replay.OwnerId) && owner is null)
                {
                    await transaction.Db.Sequences.Where(row => row.aggregate_id == input.AggregateId && row.owner_id == null)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.owner_id, replay.OwnerId), token);
                }
                return false; // No projection, notification or durable wake for a duplicate.
            }
            if (!string.IsNullOrEmpty(owner) && owner != replay.OwnerId) return false;
            if (input.Seq != latest + 1) throw Invalid($"Sequence mismatch for aggregate {input.AggregateId}: expected {latest + 1}, got {input.Seq}");
            var duplicate = await transaction.Db.Events.Where(row => row.id == input.Id.Value)
                .Select(row => new { row.aggregate_id, row.seq }).FirstOrDefaultAsync(token);
            if (duplicate is not null) throw Invalid($"Event {input.Id} already exists at aggregate {duplicate.aggregate_id} sequence {duplicate.seq}");

            transaction.Replaying = true;
            try { await decoded.Definition!.Project(transaction, decoded.Event, token); }
            finally { transaction.Replaying = false; }
            await SqliteIntrinsics.ReserveReplayAsync(transaction.Db, input.AggregateId, checked((long)input.Seq), replay.OwnerId,
                !string.IsNullOrEmpty(replay.OwnerId) && owner is null, token);
            await transaction.Db.InsertAsync(new EventRow { id = input.Id.Value, aggregate_id = input.AggregateId,
                seq = checked((long)input.Seq), created = decoded.Event.Created, type = input.Type, data = decoded.Event.Data.GetRawText() }, token);
            transaction.Committed.Add(decoded.Event);
            if (replay.Publish) transaction.Live.Add(decoded.Event);
            return true;
        }, ct, uninterruptible: true);

        InvalidDurableEventException Invalid(string message) => new(input.Type, message);
    }

    private async Task<long> LatestAsync(string aggregateId, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        return await db.Sequences.Where(row => row.aggregate_id == aggregateId).Select(row => (long?)row.seq).FirstOrDefaultAsync(ct) ?? -1;
    }

    private async Task<IReadOnlyList<SerializedDurableEvent>> ReadPageAsync(string aggregateId, double after, long through, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        var rows = await db.Events.Where(row => row.aggregate_id == aggregateId && row.seq > after && row.seq <= through)
            .OrderBy(row => row.seq).Take(pageSize).Select(row => new { row.id, row.type, row.seq, row.created, row.data }).ToListAsync(ct);
        var result = new List<SerializedDurableEvent>();
        foreach (var row in rows)
        {
            using var data = JsonDocument.Parse(row.data);
            result.Add(new(EventId.FromExisting(row.id), row.type, row.seq, aggregateId, data.RootElement.Clone(), row.created));
        }
        return result;
    }

    // JSON object order is irrelevant, array order is not. Source isDeepStrictEqual distinguishes signed zero.
    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEqual(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equal(pair.First, pair.Second)),
            JsonValueKind.Number => BitConverter.DoubleToInt64Bits(left.GetDouble()) == BitConverter.DoubleToInt64Bits(right.GetDouble()),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false
        };
    }
    private static bool ObjectsEqual(JsonElement left, JsonElement right)
    {
        var first = left.EnumerateObject().GroupBy(item => item.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var second = right.EnumerateObject().GroupBy(item => item.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        return first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && Equal(pair.Value, value));
    }
}
