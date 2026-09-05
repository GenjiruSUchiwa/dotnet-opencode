namespace OpenCode.Core.Session.Archive;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Schema;

public sealed class SessionArchiveConflictException(SessionId sessionId)
    : InvalidOperationException($"Session already exists: {sessionId.Value}")
{
    public SessionId SessionId { get; } = sessionId;
}

/// <summary>Owner handoff: implementations MUST commit through the canonical Session
/// Created event/projector transaction. No adapter is supplied or silently substituted.</summary>
public interface ISessionArchivePersistence
{
    Task<SessionInfo> ImportAsync(SessionArchiveImport input, CancellationToken ct);
}

/// <summary>Validated, detached projected transcript. Message order is ascending;
/// persistence assigns projection seq 1..N and reserves the aggregate watermark.
/// Existing IDs conflict, including when retrying an identical archive.</summary>
public sealed record SessionArchiveImport(SessionId Id, SessionId? ParentId, string? Title, string? Agent,
    ModelRef? Model, IReadOnlyDictionary<string, JsonElement>? Metadata, Money Cost, TokenUsageInfo Tokens,
    SessionTime Time, SessionOutcome? Outcome, IReadOnlyList<SessionMessage> Messages, LocationRef Location);

public sealed class SessionArchiveService(SessionStore sessions, SessionQueries queries)
{
    public async Task<SessionTransferData> ExportAsync(SessionId sessionId, bool sanitize = false, CancellationToken ct = default)
    {
        var info = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new SessionMutationNotFoundException(sessionId);
        // Upstream exports all projections, not only current context after compaction.
        var messages = await queries.MessagesAsync(sessionId, int.MaxValue, SessionQueryOrder.Ascending, null, ct).ConfigureAwait(false);
        var data = new SessionTransferData(info, messages.Where(IsSettled).ToArray());
        return sanitize ? SessionArchiveSanitizer.Sanitize(data) : data;
    }

    public async Task<SessionInfo> ImportAsync(SessionTransferData data, LocationRef location,
        ISessionArchivePersistence persistence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Info);
        ArgumentNullException.ThrowIfNull(data.Messages);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrEmpty(location.Directory);
        ArgumentNullException.ThrowIfNull(persistence);
        // Reuse current canonical converters/validators; detach caller-owned lists
        // and JSON metadata before handing them to asynchronous persistence.
        var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(data,
            OpenCodeJsonContext.Default.SessionTransferData), OpenCodeJsonContext.Default.SessionTransferData)
            ?? throw new JsonException("Missing session archive.");
        if (copy.Messages.Any(message => message is null)) throw new JsonException("Archive contains a null message.");
        var settled = copy.Messages.Where(IsSettled).ToArray();
        if (settled.Select(message => message.Id).Distinct().Count() != settled.Length)
            throw new JsonException("Archive contains duplicate settled message IDs.");
        // This is advisory validation only. The owner's transaction MUST repeat
        // conflict and parent checks, including global message-ID uniqueness.
        if (await sessions.GetSessionAsync(copy.Info.Id, ct).ConfigureAwait(false) is not null)
            throw new SessionArchiveConflictException(copy.Info.Id);
        if (copy.Info.ParentId is { } parent && await sessions.GetSessionAsync(parent, ct).ConfigureAwait(false) is null)
            throw new SessionMutationNotFoundException(parent);
        var time = copy.Info.Time;
        // Deliberately do not pass imported projectID/location/subpath/fork/revert
        // as persistence fields: source rebuilds placement and does not restore them.
        return await persistence.ImportAsync(new(copy.Info.Id, copy.Info.ParentId, copy.Info.Title,
            copy.Info.Agent, copy.Info.Model, copy.Info.Metadata, copy.Info.Cost, copy.Info.Tokens,
            time with { Viewed = time.Idle is { } idle && time.Viewed is { } viewed ? (viewed < idle ? viewed : idle) : null },
            time.Idle is null ? null : copy.Info.Outcome, settled, location), ct).ConfigureAwait(false);
    }

    internal static bool IsSettled(SessionMessage message) => message switch
    {
        AssistantMessage assistant => assistant.Time.Completed is not null,
        ShellMessage shell => shell.Status != "running",
        CompactionMessage compaction => compaction.Status != "running",
        _ => true
    };
}
