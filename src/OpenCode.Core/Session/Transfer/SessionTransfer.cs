namespace OpenCode.Core.Session.Transfer;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Event;
using OpenCode.Schema;

public sealed record SessionForkRequest(SessionId SessionId, ForkRequestBoundary Boundary);

public sealed record SessionMoveRequest(SessionId SessionId, string Directory,
    WorkspaceId? WorkspaceId = null, InboxDeliveryMode Delivery = InboxDeliveryMode.Steer);

public sealed class SessionForkEmptyException(SessionId sessionId) : Exception("Cannot fork an empty session.")
{
    public SessionId SessionId { get; } = sessionId;
}

public sealed class SessionForkMessageNotFoundException(SessionId sessionId, MessageId messageId)
    : Exception("Fork boundary message not found in the source session.")
{
    public SessionId SessionId { get; } = sessionId;
    public MessageId MessageId { get; } = messageId;
}

/// <summary>Session.fork admission. All writes pass through the retained event transaction.</summary>
public sealed class SessionTransfer(IDatabase database)
{
    public Task<SessionInfo> ForkAsync(SessionForkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Boundary);
        if (request.Boundary is not (ForkRequestBoundaryBefore or ForkRequestBoundaryThrough))
            throw new ArgumentException("Unknown fork boundary.", nameof(request));

        var id = SessionId.Create();
        return new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == request.SessionId.Value, token)) throw new SessionMutationNotFoundException(request.SessionId);

            var boundary = transaction.Db.Messages.Where(row => row.session_id == request.SessionId.Value);
            if (request.Boundary is ForkRequestBoundaryBefore before) boundary = boundary.Where(row => row.id == before.MessageId.Value);
            var found = await boundary.OrderByDescending(row => row.seq).Select(row => row.id).FirstOrDefaultAsync(token);
            if (found is null && request.Boundary is ForkRequestBoundaryBefore missing) throw new SessionForkMessageNotFoundException(request.SessionId, missing.MessageId);
            if (found is null) throw new SessionForkEmptyException(request.SessionId);
            var messageId = MessageId.FromExisting(found);

            // Snapshot current values, not the parent's epoch baseline or boundary-era values.
            IReadOnlyDictionary<string, string>? instructions = null;
            if (await transaction.Db.Set<InstructionStateRow>().Where(row => row.session_id == request.SessionId.Value).Select(row => row.current_values)
                .FirstOrDefaultAsync(token) is { } json)
                instructions = JsonSerializer.Deserialize(json, TransferJsonContext.Default.IReadOnlyDictionaryStringString)
                    ?? throw new JsonException("Instruction state must be an object.");
            var entries = new List<InstructionEntrySnapshot>();
            var rows = await transaction.Db.Set<InstructionEntryRow>().Where(row => row.session_id == request.SessionId.Value).OrderBy(row => row.key)
                .Select(row => new { row.key, row.value, row.removed }).ToListAsync(token);
            foreach (var row in rows)
            {
                using var value = JsonDocument.Parse(row.value ?? "null");
                entries.Add(new InstructionEntrySnapshot(row.key, value.RootElement.Clone(), row.removed));
            }
            await transaction.AppendAsync(ForkProjector.Forked, new SessionForkedData(id, request.SessionId,
                request.Boundary is ForkRequestBoundaryBefore ? new ForkBoundaryBefore(messageId) : new ForkBoundaryThrough(messageId),
                instructions, entries), token);

            var projected = await transaction.Db.Sessions.FirstOrDefaultAsync(row => row.id == id.Value, token)
                ?? throw new InvalidOperationException("Fork projection did not create a session.");
            return SessionStore.ReadSession(projected);
        }, ct);
    }

}
