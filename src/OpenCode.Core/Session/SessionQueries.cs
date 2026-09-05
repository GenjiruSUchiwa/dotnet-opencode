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
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var session = sessionId.Value;
        var message = messageId.Value;
        var row = await db.Messages.Where(row => row.session_id == session && row.id == message)
            .Select(row => new { row.id, row.type, row.data }).FirstOrDefaultAsync(ct).ConfigureAwait(true);
        return row is null ? null : Decode(sessionId, MessageId.FromExisting(row.id), row.type, row.data);
    }

    /// <summary>SessionHistory.load: inclusive latest completed checkpoint, then all later projected messages.</summary>
    public async Task<IReadOnlyList<SessionMessage>> ContextAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var session = sessionId.Value;
        if (!await db.Sessions.AnyAsync(row => row.id == session, ct).ConfigureAwait(true)) throw new SessionMutationNotFoundException(sessionId);
        var rows = new List<SessionMessage>();
        await foreach (var row in Context(db, session).OrderBy(row => row.seq)
            .Select(row => new { row.id, row.type, row.data }).ReadAsync(-1, ct).ConfigureAwait(true))
            rows.Add(Decode(sessionId, MessageId.FromExisting(row.id), row.type, row.data));
        return rows;
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
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var query = db.SessionDetails;
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
            var parent = input.ParentId?.Value;
            query = parent is not null ? query.Where(row => row.parent_id == parent) : query.Where(row => row.parent_id == null);
        }
        if (input.Anchor is { } anchor)
        {
            var id = anchor.Id.Value;
            query = ascending
                ? query.Where(row => SqliteFunctions.After(row.time_updated, anchor.Time) || (SqliteFunctions.At(row.time_updated, anchor.Time) && string.Compare(row.id, id) > 0))
                : query.Where(row => SqliteFunctions.Before(row.time_updated, anchor.Time) || (SqliteFunctions.At(row.time_updated, anchor.Time) && string.Compare(row.id, id) < 0));
        }
        var ordered = ascending ? query.OrderBy(row => row.time_updated).ThenBy(row => row.id)
            : query.OrderByDescending(row => row.time_updated).ThenByDescending(row => row.id);
        var rows = new List<SessionInfo>();
        await foreach (var row in ordered.ReadAsync(input.Limit, ct).ConfigureAwait(true)) rows.Add(SessionStore.ReadSession(row));
        if (previous) rows.Reverse();
        return rows;
    }

    public async Task<IReadOnlyList<SessionMessage>> MessagesAsync(SessionId sessionId, int limit,
        SessionQueryOrder order, SessionMessageAnchor? anchor, CancellationToken ct)
    {
        var previous = anchor?.Direction == SessionPageDirection.Previous;
        var ascending = (order == SessionQueryOrder.Ascending) != previous;
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var session = sessionId.Value;
        long? sequence = null;
        if (anchor is not null)
        {
            var id = anchor.Id.Value;
            if (await db.Messages.Where(row => row.session_id == session && row.id == id)
                .Select(row => (long?)row.seq).FirstOrDefaultAsync(ct).ConfigureAwait(true) is not { } found) return [];
            sequence = found;
        }
        var query = db.Messages.Where(row => row.session_id == session);
        if (sequence is not null) query = ascending ? query.Where(row => row.seq > sequence) : query.Where(row => row.seq < sequence);
        var ordered = ascending ? query.OrderBy(row => row.seq) : query.OrderByDescending(row => row.seq);
        var rows = new List<SessionMessage>();
        await foreach (var row in ordered.Select(row => new { row.id, row.type, row.data }).ReadAsync(limit, ct).ConfigureAwait(true))
            rows.Add(Decode(sessionId, MessageId.FromExisting(row.id), row.type, row.data));
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
