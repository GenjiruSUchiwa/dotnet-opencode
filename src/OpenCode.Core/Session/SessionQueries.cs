namespace OpenCode.Core.Session;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

public enum SessionQueryOrder { Ascending, Descending }
public enum SessionPageDirection { Previous, Next }
public sealed record SessionListAnchor(SessionId Id, double Time, SessionPageDirection Direction);
public sealed record SessionMessageAnchor(MessageId Id, SessionPageDirection Direction);

public sealed record SessionListQuery(
    long Limit = 50,
    SessionQueryOrder? Order = null,
    string? Directory = null,
    string? Project = null,
    string? Subpath = null,
    string? Workspace = null,
    string? Search = null,
    bool FilterParent = false,
    SessionId? ParentId = null,
    SessionListAnchor? Anchor = null);

public sealed class SessionMessageReadException(SessionId sessionId, MessageId messageId, Exception cause)
    : Exception("Failed to decode a projected session message.", cause)
{
    public SessionId SessionId { get; } = sessionId;
    public MessageId MessageId { get; } = messageId;
}

/// <summary>Read-only keyset queries from core/session/store.ts. No execution or projection writes.</summary>
public sealed class SessionQueries(IDatabase database)
{
    /// <summary>Session.message: another Session's message is indistinguishable from a missing ID.</summary>
    public async Task<SessionMessage?> MessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct = default)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        var row = await db.Messages.Where(row => row.session_id == sessionId.Value && row.id == messageId.Value)
            .Select(row => new { row.id, row.type, row.data }).FirstOrDefaultAsync(ct);
        return row is null ? null : Decode(sessionId, MessageId.FromExisting(row.id), row.type, row.data);
    }

    /// <summary>SessionHistory.load: inclusive latest completed checkpoint, then all later projected messages.</summary>
    public async Task<IReadOnlyList<SessionMessage>> ContextAsync(SessionId sessionId, CancellationToken ct = default)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        if (!await db.Sessions.AnyAsync(row => row.id == sessionId.Value, ct)) throw new SessionMutationNotFoundException(sessionId);
        var rows = await Context(db, sessionId.Value).OrderBy(row => row.seq)
            .Select(row => new { row.id, row.type, row.data }).ToListAsync(ct);
        return rows.Select(row => Decode(sessionId, MessageId.FromExisting(row.id), row.type, row.data)).ToArray();
    }

    internal static IQueryable<MessageRow> Context(PersistenceContext db, string sessionId) =>
        db.Messages.Where(row => row.session_id == sessionId && row.seq >=
            (db.Messages.Where(checkpoint => checkpoint.session_id == sessionId && checkpoint.type == "compaction"
                && SqliteFunctions.JsonText(checkpoint.data, "$.status") == "completed")
                .Max(checkpoint => (long?)checkpoint.seq) ?? -1));

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(SessionListQuery input, CancellationToken ct)
    {
        var previous = input.Anchor?.Direction == SessionPageDirection.Previous;
        var ascending = (input.Order == SessionQueryOrder.Ascending) != previous;
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        var query = db.Sessions;
        if (input.Directory is not null) query = query.Where(row => row.directory == input.Directory);
        if (!string.IsNullOrEmpty(input.Workspace)) query = query.Where(row => row.workspace_id == input.Workspace);
        if (input.Project is not null)
        {
            query = query.Where(row => row.project_id == input.Project);
            if (input.Subpath is not null) query = query.Where(row => row.path == input.Subpath);
        }
        if (!string.IsNullOrEmpty(input.Search))
        {
            var pattern = "%" + input.Search + "%";
            query = query.Where(row => EF.Functions.Like(row.title!, pattern));
        }
        if (input.FilterParent)
        {
            query = input.ParentId is { } parent ? query.Where(row => row.parent_id == parent.Value) : query.Where(row => row.parent_id == null);
        }
        if (input.Anchor is { } anchor)
        {
            query = ascending
                ? query.Where(row => row.time_updated > anchor.Time || (row.time_updated == anchor.Time && string.Compare(row.id, anchor.Id.Value) > 0))
                : query.Where(row => row.time_updated < anchor.Time || (row.time_updated == anchor.Time && string.Compare(row.id, anchor.Id.Value) < 0));
        }
        var ordered = ascending ? query.OrderBy(row => row.time_updated).ThenBy(row => row.id)
            : query.OrderByDescending(row => row.time_updated).ThenByDescending(row => row.id);
        var rows = (await ordered.LimitAsync(input.Limit, ct)).Select(SessionStore.ReadSession).ToList();
        if (previous) rows.Reverse();
        return rows;
    }

    public async Task<IReadOnlyList<SessionMessage>> MessagesAsync(SessionId sessionId, int limit,
        SessionQueryOrder order, SessionMessageAnchor? anchor, CancellationToken ct)
    {
        var previous = anchor?.Direction == SessionPageDirection.Previous;
        var ascending = (order == SessionQueryOrder.Ascending) != previous;
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        long? sequence = null;
        if (anchor is not null)
        {
            if (await db.Messages.Where(row => row.session_id == sessionId.Value && row.id == anchor.Id.Value)
                .Select(row => (long?)row.seq).FirstOrDefaultAsync(ct) is not { } found) return [];
            sequence = found;
        }
        var query = db.Messages.Where(row => row.session_id == sessionId.Value);
        if (sequence is not null) query = ascending ? query.Where(row => row.seq > sequence) : query.Where(row => row.seq < sequence);
        var ordered = ascending ? query.OrderBy(row => row.seq) : query.OrderByDescending(row => row.seq);
        var rows = (await ordered.Select(row => new { row.id, row.type, row.data }).LimitAsync(limit, ct))
            .Select(row => Decode(sessionId, MessageId.FromExisting(row.id), row.type, row.data)).ToList();
        if (previous) rows.Reverse();
        return rows;
    }

    internal static SessionMessage Decode(SessionId sessionId, MessageId id, string type, string json)
    {
        try
        {
            var data = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Missing message data.");
            data["id"] = id.Value;
            data["type"] = type;
            return data.Deserialize(OpenCodeJsonContext.Default.SessionMessage) ?? throw new JsonException("Missing message.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new SessionMessageReadException(sessionId, id, error);
        }
    }
}
