namespace OpenCode.Core.Event;

using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

/// <summary>instruction-entry.ts producer storage, serialized with Session publication.
/// Entry mutation is not an event in the source. InstructionPersistence.PrepareAsync
/// later publishes session.instructions.updated.2 and owns all epoch/message projection.</summary>
internal sealed class InstructionEntryPersistence(IDatabase database)
{
    internal Task PutAsync(SessionId sessionId, string key, string? json, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            return await SqliteIntrinsics.PutInstructionAsync(transaction.Db, sessionId.Value, key, json,
                database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), token);
        }, ct);

    internal Task RemoveAsync(SessionId sessionId, string key, CancellationToken ct) =>
        new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            var now = database.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            return await transaction.Db.Set<InstructionEntryRow>().Where(row => row.session_id == sessionId.Value && row.key == key && !row.removed)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.value, (string?)null).SetProperty(row => row.removed, true)
                    .SetProperty(row => row.time_updated, now), token);
        }, ct);
}
