namespace OpenCode.Core.Database;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Projects;
using OpenCode.Core.Event;
using OpenCode.Core.Instructions;
using OpenCode.Core.Locations;
using OpenCode.Core.Session;
using OpenCode.Schema;

/// <summary>
/// Maps the upstream session projections. Database readiness belongs to the daemon.
/// Inbox operations use durable append plus transactional projection.
/// Other mutations remain direct projection writes and reject durable aggregates.
/// This is not a complete Session domain or an execution coordinator.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly IDatabase _database;
    public TimeProvider Clock => _database.Clock;
    private readonly ReadInstructionLoader _readInstructions;
    private readonly Lock _instructionScopesLock = new();
    private readonly Dictionary<string, InstructionLocationState> _instructionScopes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool _disposed;

    public SessionStore(IDatabase database)
    {
        _database = database;
        _readInstructions = new ReadInstructionLoader(database);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(int limit = 50, CancellationToken ct = default)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        var db = new PersistenceContext(conn);
        await using var dbLifetime = db.ConfigureAwait(true);
        var sessions = new List<SessionInfo>();
        await foreach (var row in db.SessionDetails.OrderByDescending(row => row.time_updated).ThenByDescending(row => row.id).ReadAsync(limit, ct).ConfigureAwait(true))
            sessions.Add(ReadSession(row));
        return sessions;
    }

    public async Task<SessionInfo?> GetSessionAsync(SessionId id, CancellationToken ct = default)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        var db = new PersistenceContext(conn);
        await using var dbLifetime = db.ConfigureAwait(true);
        var key = id.Value;
        var row = await db.SessionDetails.FirstOrDefaultAsync(row => row.id == key, ct).ConfigureAwait(true);
        return row is null ? null : ReadSession(row);
    }

    internal static SessionInfo ReadSession(SessionRow row)
    {
        static DateTimeOffset? Time(long? value) => value is { } time ? DateTimeOffset.FromUnixTimeMilliseconds(time) : null;
        var directory = ProjectPaths.DirectoryPlatform(row.directory);
        var model = row.model is { } modelJson ? JsonSerializer.Deserialize(modelJson, OpenCodeJsonContext.Default.ModelRef) : null;
        return new SessionInfo(
            Id: SessionId.FromExisting(row.id), ProjectId: ProjectId.FromExisting(row.project_id), Slug: row.slug,
            Directory: directory, Version: row.version,
            ParentId: row.parent_id is { Length: > 0 } parent ? SessionId.FromExisting(parent) : null,
            Fork: row.fork_session_id is { Length: > 0 } fork && row.fork_boundary is { } boundary
                ? new SessionForkInfo(SessionId.FromExisting(fork), JsonSerializer.Deserialize(boundary, OpenCodeJsonContext.Default.ForkBoundary)!) : null,
            Time: new SessionTime(DateTimeOffset.FromUnixTimeMilliseconds(row.time_created), DateTimeOffset.FromUnixTimeMilliseconds(row.time_updated),
                Time(row.time_idle), Time(row.time_viewed), Time(row.time_archived)),
            Tokens: new TokenUsageInfo(row.tokens_input, row.tokens_output, row.tokens_reasoning, new TokenCacheUsage(row.tokens_cache_read, row.tokens_cache_write)),
            Cost: Money.FromExisting(row.cost), Title: row.title, Agent: row.agent is { Length: > 0 } agent ? agent : null,
            Model: model is null ? null : model with { Variant = model.Variant ?? "default" },
            Outcome: row.idle_outcome switch { null => null, "succeeded" => SessionOutcome.Succeeded, "failed" => SessionOutcome.Failed,
                "interrupted" => SessionOutcome.Interrupted, _ => throw new JsonException("Invalid persisted session outcome.") },
            Location: new LocationRef(directory, row.workspace_id is { Length: > 0 } workspace ? WorkspaceId.FromExisting(workspace) : null),
            Subpath: row.path is { Length: > 0 } subpath ? ProjectPaths.Relative(subpath) : null,
            Metadata: row.metadata is { } metadata ? JsonSerializer.Deserialize<IReadOnlyDictionary<string, JsonElement>>(metadata, OpenCodeJsonContext.Default.Options) : null,
            Revert: row.revert is { } revert ? ReadRevert(revert) : null);
    }

    private static SessionRevert? ReadRevert(string json)
    {
        var data = JsonNode.Parse(json);
        // Upstream PersistedRevert also accepts V1 file diffs stored with `path`.
        if (data?["files"] is JsonArray files)
        {
            foreach (var file in files.OfType<JsonObject>())
            {
                if (!file.ContainsKey("file") && file["path"] is { } path)
                {
                    file["file"] = path.DeepClone();
                    file.Remove("path");
                }
            }
        }
        return data.Deserialize(OpenCodeJsonContext.Default.SessionRevert);
    }

    public Task<IReadOnlyList<JsonElement>> ListMessagesAsync(SessionId sessionId, int limit = 100, CancellationToken ct = default) =>
        ReadMessagesAsync(sessionId, limit, false, ct);

    private async Task<IReadOnlyList<JsonElement>> ReadMessagesAsync(SessionId sessionId, int limit, bool context, CancellationToken ct)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        var db = new PersistenceContext(conn);
        await using var dbLifetime = db.ConfigureAwait(true);
        var session = sessionId.Value;
        var query = context ? SessionQueries.Context(db, session) : db.Messages.Where(row => row.session_id == session);
        var list = new List<JsonElement>();
        await foreach (var row in query.OrderBy(row => row.seq).Select(row => new { row.id, row.type, row.data }).ReadAsync(limit, ct).ConfigureAwait(true))
        {
            var data = new JsonObject
            {
                ["type"] = row.type,
                ["id"] = row.id
            };
            foreach (var property in JsonNode.Parse(row.data)!.AsObject())
            {
                if (property.Key is not ("id" or "type"))
                    data[property.Key] = property.Value?.DeepClone();
            }
            // The payload owns message time; row update time does not mean completion.
            using var document = JsonDocument.Parse(data.ToJsonString());
            list.Add(document.RootElement.Clone());
        }

        return list;
    }

    public async Task<ProjectId> EnsureProjectAsync(string directory, CancellationToken ct = default)
    {
        var location = await CatalogLocation.ResolveAsync(_database, directory, ct: ct).ConfigureAwait(true);
        return location.Project.Id;
    }

    public async Task DeleteSessionAsync(SessionId id, CancellationToken ct = default)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        using var transaction = conn.BeginTransaction(deferred: false);
        var db = new PersistenceContext(conn, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        await RequireDirectMutationAsync(db, id, ct).ConfigureAwait(true);
        await db.Sessions.Where(row => row.id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task UpdateTitleAsync(SessionId id, string title, CancellationToken ct = default)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        using var transaction = conn.BeginTransaction(deferred: false);
        var db = new PersistenceContext(conn, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        await RequireDirectMutationAsync(db, id, ct).ConfigureAwait(true);
        var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.Sessions.Where(row => row.id == id.Value).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.title, title).SetProperty(row => row.time_updated, now), ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task UpdateAgentAsync(SessionId id, string agent, CancellationToken ct = default)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        using var transaction = conn.BeginTransaction(deferred: false);
        var db = new PersistenceContext(conn, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        await RequireDirectMutationAsync(db, id, ct).ConfigureAwait(true);
        var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.Sessions.Where(row => row.id == id.Value).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.agent, agent).SetProperty(row => row.time_updated, now), ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task UpdateModelAsync(SessionId id, ModelRef model, CancellationToken ct = default)
    {
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        using var transaction = conn.BeginTransaction(deferred: false);
        var db = new PersistenceContext(conn, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        await RequireDirectMutationAsync(db, id, ct).ConfigureAwait(true);
        var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(model, OpenCodeJsonContext.Default.ModelRef);
        await db.Sessions.Where(row => row.id == id.Value).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.model, json).SetProperty(row => row.time_updated, now), ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task<SessionInfo> CreateSessionAsync(
        string directory,
        string? title = null,
        ProjectId? projectId = null,
        SessionId? sessionId = null,
        CancellationToken ct = default,
        string? agent = null,
        ModelRef? model = null,
        LocationRef? location = null,
        IReadOnlyDictionary<string, JsonElement>? metadata = null,
        SessionId? parentId = null,
        string? subpath = null,
        SessionForkInfo? fork = null)
    {
        directory = location?.Directory ?? directory;
        var id = sessionId ?? SessionId.Create();
        if (sessionId is not null && await GetSessionAsync(id, ct).ConfigureAwait(true) is { } existing) return existing;
        if (fork is not null) throw new NotSupportedException("Fork creation requires the canonical fork event and history-copying projector.");
        var resolved = projectId is null
            ? await CatalogLocation.ResolveAsync(_database, directory, location?.WorkspaceId?.Value, ct).ConfigureAwait(true)
            : null;
        var prjId = projectId ?? resolved!.Project.Id;
        if (resolved is not null)
        {
            directory = resolved.Directory;
            location = new LocationRef(directory, resolved.WorkspaceId);
            var relative = Path.GetRelativePath(resolved.Project.Directory, directory).Replace('\\', '/');
            subpath ??= relative == "." ? "" : relative;
        }
        return await new SessionCreation(_database).CreateAsync(new SessionCreatedEventData(id, prjId,
            Guid.NewGuid().ToString("N")[..8], "2", location ?? new LocationRef(directory), title, agent, model, parentId, metadata, subpath), ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Admits prepared text-only user or synthetic input without running a model.
    /// Returns only after event and projection commit. Execution wake-up belongs to
    /// the caller; resume=false must stop here. Controls and attachments are not supported yet.
    /// </summary>
    public async Task<SessionInboxItem> AdmitInboxAsync(SessionId sessionId, MessageId id, InboxPayload payload,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default)
    {
        var type = payload switch
        {
            UserInboxPayload => "user", SyntheticInboxPayload => "synthetic",
            _ => throw new NotSupportedException("Control admission requires its dedicated lifecycle.")
        };
        var admission = new SessionAdmission(_database);
        var existing = await admission.ReconcileAsync(sessionId, id, type, delivery, ct).ConfigureAwait(true);
        if (existing is not null) return existing;
        return await SessionRunCoordinator.AdmitAsync(sessionId, () => admission.AdmitAsync(sessionId, id, payload, delivery, ct), ct).ConfigureAwait(true);
    }

    /// <summary>Operation-specific compaction admission; one unconsumed compaction per Session.</summary>
    public Task<SessionInboxItem> AdmitCompactionAsync(SessionId sessionId, MessageId? id = null,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default) =>
        SessionRunCoordinator.AdmitAsync(sessionId,
            () => new SessionAdmission(_database).AdmitCompactionAsync(sessionId, id ?? MessageId.Create(), delivery, ct), ct);

    internal Task<MessageId?> StartCompactionAsync(SessionId sessionId, InboxPromotable scope, CancellationToken ct) =>
        new SessionAdmission(_database).StartCompactionAsync(sessionId, scope, ct);

    internal Task<OpenCodeEvent> PublishCompactionAsync<T>(SessionId id, OpenCode.Core.Event.DurableEventDefinition<T> definition, T data, CancellationToken ct) =>
        new EventStore(_database).TransactAsync(id.Value, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == id.Value, token).ConfigureAwait(true)) throw new InvalidOperationException("Session not found.");
            return await transaction.AppendAsync(definition, data, token).ConfigureAwait(true);
        }, ct);

    /// <summary>Reconcile identity before preparing a retried user/synthetic payload.</summary>
    public Task<SessionInboxItem?> ReconcileInboxAsync(SessionId sessionId, MessageId id, string type,
        InboxDeliveryMode delivery = InboxDeliveryMode.Steer, CancellationToken ct = default) =>
        new SessionAdmission(_database).ReconcileAsync(sessionId, id, type, delivery, ct);

    public Task<IReadOnlyList<SessionInboxItem>> ListInboxAsync(SessionId sessionId, CancellationToken ct = default) =>
        new SessionAdmission(_database).ListAsync(sessionId, ct);

    /// <summary>Read-only preview, including controls; not a claim or reservation.</summary>
    public Task<SessionInboxItem?> NextPromotableInboxAsync(SessionId sessionId, InboxPromotable scope, CancellationToken ct = default) =>
        new SessionAdmission(_database).NextPromotableAsync(sessionId, scope, ct);

    public Task CancelInboxAsync(SessionId sessionId, MessageId id, CancellationToken ct = default) =>
        new SessionAdmission(_database).CancelAsync(sessionId, id, ct);

    /// <summary>Changes queue/steer mode only; callers own advisory execution wake-up.</summary>
    public Task ChangeInboxDeliveryAsync(SessionId sessionId, MessageId id, InboxDeliveryMode delivery, CancellationToken ct = default) =>
        new SessionAdmission(_database).ChangeDeliveryAsync(sessionId, id, delivery, ct);

    /// <summary>
    /// Delivers the ordered steer prefix at a safe step boundary, or one queued
    /// input followed by newly arrived steers at an idle boundary. Does not execute
    /// a model. Controls remain pending for their dedicated domain operation.
    /// </summary>
    public Task<int> PromoteInboxAsync(SessionId sessionId, InboxPromotable scope, CancellationToken ct = default) =>
        new SessionAdmission(_database).PromoteAsync(sessionId, scope, ct);

    /// <summary>
    /// Appends a supported canonical assistant event data object (not an envelope)
    /// and projects it atomically. Type is unversioned; the event definition owns
    /// its version. This does not execute tools, stream deltas, or replay events.
    /// Reusing an event ID fails; a fresh terminal event records usage again.
    /// </summary>
    public Task<OpenCodeEvent> AppendAssistantEventAsync(string type, JsonElement data,
        CancellationToken ct = default, EventId? eventId = null) =>
        new AssistantProjector(_database).AppendAsync(type, data, ct, eventId);

    /// <summary>Experimental durable Session log, not the volatile /api/event feed.</summary>
    public async IAsyncEnumerable<OpenCode.Core.Event.Log.DurableLogItem> LogAsync(SessionId sessionId, double? after = null, bool follow = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (await GetSessionAsync(sessionId, ct).ConfigureAwait(true) is null) throw new SessionMutationNotFoundException(sessionId);
        await foreach (var item in new OpenCode.Core.Event.Log.DurableEventLog(_database).LogAsync(sessionId.Value, after ?? -1, follow, ct).ConfigureAwait(true))
            yield return item;
    }

    /// <summary>Publishes session.shell.started.1 and its event-derived message atomically; does not start a process.</summary>
    public Task<OpenCodeEvent> PublishShellStartedAsync(SessionId sessionId, ShellInfo shell, CancellationToken ct = default,
        EventId? eventId = null, IReadOnlyDictionary<string, JsonElement>? metadata = null) =>
        new SessionShellPersistence(_database).StartedAsync(sessionId, shell, ct, eventId, metadata);

    /// <summary>Publishes session.shell.ended.1 and updates the latest matching shell message; does not admit completion input.</summary>
    public Task<OpenCodeEvent> PublishShellEndedAsync(SessionId sessionId, ShellInfo shell, ShellOutput output, CancellationToken ct = default,
        EventId? eventId = null, IReadOnlyDictionary<string, JsonElement>? metadata = null) =>
        new SessionShellPersistence(_database).EndedAsync(sessionId, shell, output, ct, eventId, metadata);

    internal Task<OpenCodeEvent> AppendExecutionEventAsync(SessionId id, string type, CancellationToken ct,
        SessionStructuredError? error = null, string? reason = null) =>
        new ExecutionProjector(_database).AppendAsync(id, type, error, reason, ct);

    /// <summary>Unpaginated projected history from the latest completed compaction boundary.</summary>
    internal Task<IReadOnlyList<JsonElement>> LoadExecutionHistoryAsync(SessionId id, CancellationToken ct) =>
        ReadMessagesAsync(id, int.MaxValue, true, ct);

    internal async Task<LocalInstructionSelection> SelectInstructionsAsync(SessionInfo session, string agent,
        JsonObject configuration, bool preview, CancellationToken ct, AgentInfo? selection = null,
        IReadOnlyList<string>? toolNames = null, string? projectDirectory = null, InstructionSource? mcp = null, InstructionSource? codeMode = null)
    {
        var instructions = await ObserveInstructionsAsync(session, agent, configuration, ct, selection, toolNames, projectDirectory, mcp, codeMode).ConfigureAwait(true);
        await new InstructionPersistence(_database).PrepareAsync(session.Id, instructions.Sources, preview, ct).ConfigureAwait(true);
        return instructions;
    }

    internal async Task<LocalInstructionSelection> ObserveInstructionsAsync(SessionInfo session, string agent,
        JsonObject configuration, CancellationToken ct, AgentInfo? selection = null,
        IReadOnlyList<string>? toolNames = null, string? projectDirectory = null, InstructionSource? mcp = null, InstructionSource? codeMode = null)
    {
        InstructionLocationState observations;
        lock (_instructionScopesLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var directory = Path.GetFullPath(session.Location.Directory);
            if (!_instructionScopes.TryGetValue(directory, out observations!))
                _instructionScopes.Add(directory, observations = new InstructionLocationState(directory));
        }
        var persistence = new InstructionPersistence(_database);
        return await LocalInstructions.ReadAsync(session, agent, configuration, await persistence.EntriesAsync(session.Id, ct).ConfigureAwait(true), observations, Clock, ct, selection, toolNames, projectDirectory, mcp, codeMode).ConfigureAwait(true);
    }

    /// <summary>Invalidates only this host's Location observation scope; durable instruction epochs are unchanged.</summary>
    public void InvalidateInstructionLocation(string directory)
    {
        lock (_instructionScopesLock)
            if (_instructionScopes.Remove(Path.GetFullPath(directory), out var scope)) scope.Dispose();
    }

    public void Dispose()
    {
        lock (_instructionScopesLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var scope in _instructionScopes.Values) scope.Dispose();
            _instructionScopes.Clear();
        }
    }

    internal Task<(string Initial, IReadOnlyList<JsonElement> Messages)> LoadExecutionContextAsync(SessionId id,
        LocalInstructionSelection selection, CancellationToken ct) =>
        new InstructionPersistence(_database).LoadAsync(id, selection.Sources, ct);

    internal Task<(string Initial, string Update, IReadOnlyList<JsonElement> Messages)> PreviewExecutionContextAsync(SessionId id,
        LocalInstructionSelection selection, CancellationToken ct) =>
        new InstructionPersistence(_database).PreviewAsync(id, selection.Sources, ct);

    internal Task LoadReadInstructionsAsync(SessionId id, IReadOnlyList<string> paths, string projectRoot, CancellationToken ct) =>
        _readInstructions.LoadAsync(id, paths, projectRoot, ct);

    /// <summary>Reads durable execution claims. This snapshot does not establish recovery ownership or start execution.</summary>
    public Task<IReadOnlyList<SessionId>> ListSuspendedAsync(CancellationToken ct) => new RestartPersistence(_database).ListAsync(ct);

    internal Task<RestartPreparation> PrepareRestartAsync(SessionId id, int maxAttempts, CancellationToken ct, RestartScope? scope = null,
        bool child = false, bool localMoves = false) =>
        new RestartPersistence(_database).PrepareAsync(id, maxAttempts, ct, scope, child, localMoves);

    internal async Task RequireExecutionReadyAsync(SessionId id, CancellationToken ct, bool recovering = false)
    {
        var connection = _database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        if (await SqliteIntrinsics.HasUnsequencedProjectionAsync(db, id.Value, ct).ConfigureAwait(true))
            throw new NotSupportedException("Unsequenced projections require canonical migration before execution.");
        if (recovering) return;
        if (await db.Sessions.AnyAsync(row => row.id == id.Value && row.time_suspended != null, ct).ConfigureAwait(true))
            throw new NotSupportedException("A surviving execution claim requires startup recovery; this runner will not overwrite or release it.");
    }

    public async Task AddMessageAsync(SessionId sessionId, SessionMessage message, CancellationToken ct = default)
    {
        var data = JsonSerializer.SerializeToNode(message, OpenCodeJsonContext.Default.SessionMessage)!.AsObject();
        var type = data["type"]!.GetValue<string>();
        data.Remove("id");
        data.Remove("type");
        var conn = _database.CreateConnection();
        await using var connLifetime = conn.ConfigureAwait(true);
        using var transaction = conn.BeginTransaction(deferred: false);
        var db = new PersistenceContext(conn, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        await RequireDirectMutationAsync(db, sessionId, ct).ConfigureAwait(true);
        // The old Convert.ToInt64(SQL max + 1) rejected overflow instead of
        // wrapping to a negative sequence or clamping through GetInt64.
        var nextSeq = checked((await db.Messages.Where(row => row.session_id == sessionId.Value)
            .MaxAsync(row => (long?)row.seq, ct).ConfigureAwait(true) ?? -1) + 1);
        var nowMs = message.Time.Created.ToUnixTimeMilliseconds();
        await db.InsertAsync(new MessageRow { id = message.Id.Value, session_id = sessionId.Value, type = type,
            seq = nextSeq, time_created = nowMs, time_updated = nowMs, data = data.ToJsonString() }, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private static async Task RequireDirectMutationAsync(PersistenceContext db,
        SessionId id, CancellationToken ct)
    {
        var aggregate = id.Value;
        if (await db.Sequences.AnyAsync(row => row.aggregate_id == aggregate, ct).ConfigureAwait(true))
            throw new NotSupportedException("Durable aggregates require event-driven domain operations, not direct session mutation.");
    }
}
