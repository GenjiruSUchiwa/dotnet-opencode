namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Schema;

internal sealed record RevertStagedData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId, [property: JsonPropertyName("revert"), JsonRequired] SessionRevert Revert);
internal sealed record RevertClearedData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId);
internal sealed record RevertCommittedData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId, [property: JsonPropertyName("to"), JsonRequired] MessageId To);

[JsonSerializable(typeof(RevertStagedData))]
[JsonSerializable(typeof(RevertClearedData))]
[JsonSerializable(typeof(RevertCommittedData))]
internal partial class RevertEventJsonContext : JsonSerializerContext;

internal sealed class SessionRevertPersistence(IDatabase database)
{
    internal static readonly DurableEventDefinition<RevertStagedData> Staged = new("session.revert.staged", 1, "sessionID", RevertEventJsonContext.Default.RevertStagedData, ProjectAsync);
    internal static readonly DurableEventDefinition<RevertClearedData> Cleared = new("session.revert.cleared", 1, "sessionID", RevertEventJsonContext.Default.RevertClearedData, ProjectAsync);
    internal static readonly DurableEventDefinition<RevertCommittedData> Committed = new("session.revert.committed", 1, "sessionID", RevertEventJsonContext.Default.RevertCommittedData, ProjectAsync);

    internal Task<IReadOnlyDictionary<string, SnapshotId>> PlanAsync(SessionId sessionId, MessageId messageId, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            await RequireProjectedAsync(transaction, sessionId, token).ConfigureAwait(true);
            if (await transaction.Db.Messages.Where(row => row.session_id == sessionId.Value && row.id == messageId.Value)
                .Select(row => (long?)row.seq).FirstOrDefaultAsync(token).ConfigureAwait(true) is not { } sequence) throw new SessionRevertMessageNotFoundException(sessionId, messageId);
            var rows = transaction.Db.Messages.Where(row => row.session_id == sessionId.Value && row.type == "assistant" && row.seq > sequence)
                .OrderBy(row => row.seq).Select(row => new { row.id, row.data });
            var result = new Dictionary<string, SnapshotId>(StringComparer.Ordinal);
            await foreach (var row in rows.ReadAsync(-1, token).ConfigureAwait(true))
            {
                var message = (AssistantMessage)SessionQueries.Decode(sessionId, MessageId.FromExisting(row.id), "assistant", row.data);
                if (message.Snapshot?.Start is not { } start) continue;
                foreach (var file in message.Snapshot.Files ?? []) result.TryAdd(file, start);
            }
            return (IReadOnlyDictionary<string, SnapshotId>)result;
        }, ct);

    internal Task StageAsync(SessionId id, SessionRevert revert, CancellationToken ct) => PublishAsync(id, Staged, new RevertStagedData(id, revert), ct);
    internal Task ClearAsync(SessionId id, CancellationToken ct) => PublishAsync(id, Cleared, new RevertClearedData(id), ct);
    internal Task CommitAsync(SessionId id, MessageId to, CancellationToken ct) => PublishAsync(id, Committed, new RevertCommittedData(id, to), ct);

    private Task PublishAsync<T>(SessionId id, DurableEventDefinition<T> definition, T data, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == id.Value, token).ConfigureAwait(true)) throw new SessionMutationNotFoundException(id);
            await RequireProjectedAsync(transaction, id, token).ConfigureAwait(true);
            return await transaction.AppendAsync(definition, data, token).ConfigureAwait(true);
        }, ct);

    private static async Task RequireProjectedAsync(EventTransaction transaction, SessionId id, CancellationToken ct)
    {
        if (await transaction.Db.HighestProjectionAsync(id.Value, ct).ConfigureAwait(true) > await transaction.LatestSequenceAsync(id.Value, ct).ConfigureAwait(true))
            throw new NotSupportedException("Unsequenced projections require canonical migration before revert.");
    }

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var id = committed.Data.GetProperty("sessionID").GetString()!;
        if (committed.Type == "session.revert.committed")
        {
            var to = committed.Data.GetProperty("to").GetString()!;
            if (await transaction.Db.Messages.Where(row => row.session_id == id && row.id == to).Select(row => (long?)row.seq).FirstOrDefaultAsync(ct).ConfigureAwait(true) is not { } sequence)
                throw new InvalidOperationException("Revert boundary message not found: " + to);
            await transaction.Db.Messages.Where(row => row.session_id == id && row.seq >= sequence).ExecuteDeleteAsync(ct).ConfigureAwait(true);
            await transaction.Db.Inbox.Where(row => row.session_id == id && row.enqueued_seq >= sequence).ExecuteDeleteAsync(ct).ConfigureAwait(true);
            await transaction.Db.Set<InstructionStateRow>().Where(row => row.session_id == id).ExecuteDeleteAsync(ct).ConfigureAwait(true);
        }
        var revert = committed.Type == "session.revert.staged" ? committed.Data.GetProperty("revert").GetRawText() : null;
        await transaction.Db.Sessions.Where(row => row.id == id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.revert, revert), ct).ConfigureAwait(true);
        await SqliteIntrinsics.TouchSessionAsync(transaction.Db, id, committed.Created, ct).ConfigureAwait(true);
    }
}
