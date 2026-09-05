namespace OpenCode.Core.Event;

using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Schema;

internal sealed class SessionTitlePersistence(IDatabase database)
{
    internal async Task<UserMessage?> FirstUserAsync(SessionId id, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        var row = await db.Messages.Where(row => row.session_id == id.Value && row.type == "user").OrderBy(row => row.seq)
            .Select(row => new { row.id, row.data }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        try { return SessionQueries.Decode(id, MessageId.FromExisting(row.id), "user", row.data) as UserMessage; }
        catch (SessionMessageReadException) { return null; }
    }

    internal async Task<long> ExpectedSequenceAsync(SessionId id, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        return (await db.Sequences.Where(row => row.aggregate_id == id.Value).Select(row => (long?)row.seq).FirstOrDefaultAsync(ct) ?? -1) + 1;
    }

    internal Task UsageAsync(SessionId id, Money cost, TokenUsageInfo tokens, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == id.Value, token)) return false;
            await transaction.AppendAsync(CompactionProjector.Usage, new SessionUsageRecordedEventData(id, "title", cost, tokens), token);
            return true;
        }, ct);

    internal Task RenameAsync(SessionId id, string? original, string title, long expectedSequence, CancellationToken ct) =>
        new SessionMutationProjector(database).PublishAsync(id, SessionMutationProjector.Renamed,
            current => current.Title != original || current.Title == title ? null : new SessionRenameData(id, title), ct, expectedSequence);
}
