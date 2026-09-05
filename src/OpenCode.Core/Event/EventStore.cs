namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Database;
using OpenCode.Core.Event.Log;
using OpenCode.Schema;

/// <summary>
/// The retained-event transaction path of core/bus.ts. DurableEventLog composes log/replay/claim over this boundary.
/// Schema/bootstrap is owned by IDatabase's host, not by this store.
/// </summary>
internal sealed class EventStore(IDatabase database)
{
    internal Task<T> TransactAsync<T>(string aggregateId,
        Func<EventTransaction, CancellationToken, Task<T>> action, CancellationToken ct, bool uninterruptible = false)
    {
        return SessionEvents.SerializeAsync(aggregateId, async () =>
        {
            ct.ThrowIfCancellationRequested();
            var commitToken = uninterruptible ? CancellationToken.None : ct;
            var connection = database.CreateConnection();
            await using var connectionLifetime = connection.ConfigureAwait(true);
            // Publication lock precedes the SQLite writer lock and spans notification.
            using var transaction = connection.BeginTransaction(deferred: false);
            var persistence = new PersistenceContext(connection, transaction);
            await using var persistenceLifetime = persistence.ConfigureAwait(true);
            var context = new EventTransaction(aggregateId, database.Clock, persistence);
            var result = await action(context, commitToken).ConfigureAwait(true);
            commitToken.ThrowIfCancellationRequested();
            // Cancellation is checked above, not after durable commit begins.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(true);
            if (context.Committed.Count > 0) DurableLogSignals.Notify(aggregateId);
            foreach (var committed in context.Live) SessionEvents.Notify(committed, true);
            return result;
        }, ct);
    }
}

internal sealed record DurableEventDefinition<T>(
    string Type,
    int Version,
    string AggregateField,
    JsonTypeInfo<T> DataType,
    Func<EventTransaction, OpenCodeEvent, CancellationToken, Task> Project);

/// <summary>
/// SQL commands and projectors share this connection and transaction. No observer
/// or execution wake-up may run here; callers may notify only after TransactAsync.
/// </summary>
internal sealed class EventTransaction(string aggregateId, TimeProvider clock, PersistenceContext persistence)
{
    internal PersistenceContext Db => persistence;
    internal TimeProvider Clock => clock;
    internal readonly List<OpenCodeEvent> Committed = [];
    internal readonly List<OpenCodeEvent> Live = [];
    internal bool Replaying { get; set; }

    internal async Task<long> LatestSequenceAsync(string aggregateId, CancellationToken ct)
    {
        return await Db.Sequences.Where(row => row.aggregate_id == aggregateId).Select(row => (long?)row.seq).FirstOrDefaultAsync(ct).ConfigureAwait(true) ?? -1;
    }

    // Bus.reserveSequence: used by inherited history, never by event replay ownership.
    internal async Task ReserveSequenceAsync(string aggregateId, long seq, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seq);
        await SqliteIntrinsics.ReserveSequenceAsync(Db, aggregateId, seq, ct).ConfigureAwait(true);
    }

    internal async Task<OpenCodeEvent> AppendAsync<T>(
        DurableEventDefinition<T> definition, T data, CancellationToken ct, EventId? id = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null, LocationRef? location = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Version, 1);
        var encoded = JsonSerializer.SerializeToElement(data, definition.DataType);
        if (!encoded.TryGetProperty(definition.AggregateField, out var aggregate) ||
            aggregate.ValueKind != JsonValueKind.String)
            throw new JsonException("Durable event data must contain its string aggregate field.");
        if (aggregate.GetString() != aggregateId) throw new JsonException("Event aggregate does not match its transaction.");
        var seq = checked(await LatestSequenceAsync(aggregateId, ct).ConfigureAwait(true) + 1);
        var eventId = id ?? EventId.Create();
        if (await Db.Events.AnyAsync(row => row.id == eventId.Value, ct).ConfigureAwait(true))
            throw new InvalidOperationException("The durable event ID already exists. Append is not replay.");

        var committed = new OpenCodeEvent(eventId, definition.Type, Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            encoded, Location: location, Metadata: metadata, Durable: new DurableEnvelope(aggregateId, seq, definition.Version));
        await definition.Project(this, committed, ct).ConfigureAwait(true);
        await ReserveSequenceAsync(aggregateId, seq, ct).ConfigureAwait(true);
        await Db.InsertAsync(new EventRow { id = eventId.Value, aggregate_id = aggregateId, seq = seq,
            created = committed.Created, type = $"{definition.Type}.{definition.Version}", data = encoded.GetRawText() }, ct).ConfigureAwait(true);
        Committed.Add(committed);
        Live.Add(committed);
        return committed;
    }
}
