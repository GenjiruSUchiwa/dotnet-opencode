namespace OpenCode.Cli.Tui;

using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using OpenCode.Client;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Cli.Tui.Recovery;
using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public enum SessionFeedPhase { Connecting, Live, Reconnecting, Disposed }
public enum SessionSynchronization { Empty, Hydrating, Live, Stale, Failed, Deleted }
public sealed record SessionFeedSnapshot(SessionFeedPhase Phase, long Epoch, string? Error = null)
{
    public int Attempt { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
}

/// <summary>One Session's live read model. No prompt or command response is synthesized to observe it.</summary>
public sealed record SessionObservationSnapshot(SessionId SessionId, SessionInfo? Session,
    IReadOnlyList<SessionMessage> Messages, IReadOnlyList<SessionInboxItem> Inbox,
    IReadOnlyList<PermissionRequest> Permissions, bool Running, string? Error, long Revision, bool Deleted = false)
{
    public bool Busy => Running || Inbox.Count > 0;
    /// <summary>Last admission/execution failure, distinct from an observation transport error.
    /// A previous execution failure must not prevent a new command or prompt.</summary>
    public string? ExecutionError { get; init; }
    public SessionSynchronization Synchronization { get; init; }
    public IReadOnlyList<SessionAdmissionSnapshot> Admissions { get; init; } = [];
    public LocationRecoveryEvidence? LocationUnavailable { get; init; }
    public SessionMoveSnapshot? Move { get; init; }
}

public sealed partial class SessionClientAdapter
{
    private sealed class Observation(SessionId id)
    {
        internal readonly SessionId Id = id;
        internal readonly SemaphoreSlim Admission = new(1, 1);
        internal SessionInfo? Session;
        internal ImmutableArray<SessionMessage> Messages = [];
        internal ImmutableArray<SessionInboxItem> Inbox = [];
        internal readonly Dictionary<MessageId, long> MessageVersions = [];
        internal readonly Dictionary<MessageId, (long Version, SessionInboxItem? Item)> InboxChanges = [];
        internal readonly HashSet<MessageId> ConsumedInputs = [];
        internal readonly HashSet<Channel<SessionObservationSnapshot>> Subscribers = [];
        internal Task<SessionObservationSnapshot>? Refresh;
        internal bool Invalidated;
        internal bool Loaded;
        internal bool FormsHydrated;
        internal bool Running;
        internal bool Deleted;
        internal long Version;
        internal long RunningVersion;
        internal long Revision;
        internal string? Error;
        internal SessionStructuredError? Failure;
        internal long FailureVersion;
        internal bool NeedsReconciliation;
        internal SessionCreateInput? Creation;
        internal TaskCompletionSource? CreationReady;
        internal readonly Dictionary<MessageId, AdmissionRecord> Admissions = [];
        internal SessionSynchronization Synchronization;
        internal long Epoch;
        internal bool AwaitingBaseline;
        internal bool Replaying;
        internal readonly List<ServerEventEnvelope> Buffered = [];
        internal readonly HashSet<(MessageId Message, bool Text, int Ordinal)> FullContent = [];
        internal readonly HashSet<(MessageId Message, string Tool)> FullToolInput = [];
        internal long PromptPulse;
        internal LocationRecoveryEvidence? LocationUnavailable;
        internal SessionMoveSnapshot? Move;
    }

    private readonly Dictionary<SessionId, Observation> _observed = [];
    private ImmutableDictionary<SessionId, SessionObservationSnapshot> _observationSnapshot = ImmutableDictionary<SessionId, SessionObservationSnapshot>.Empty;
    private ImmutableHashSet<SessionId> _deletedSessions = [];
    private SessionFeedSnapshot _feed = new(SessionFeedPhase.Connecting, 0);
    private TaskCompletionSource _retryConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SessionFeedSnapshot Feed { get { lock (_gate) return _feed; } }
    public IReadOnlyDictionary<SessionId, SessionObservationSnapshot> Observations { get { lock (_gate) return _observationSnapshot; } }
    public IReadOnlySet<SessionId> DeletedSessions { get { lock (_gate) return _deletedSessions; } }
    public SessionObservationSnapshot? ReadObservation(SessionId id) { lock (_gate) return _observationSnapshot.GetValueOrDefault(id); }

    public SessionMoveSnapshot? ReadMove(SessionId id) { lock (_gate) return _observed.GetValueOrDefault(id)?.Move; }

    /// <summary>Submit one explicit move through the typed API. HTTP 204 acknowledges admission,
    /// not delivery. Uncertain outcomes are retained and never automatically posted again.</summary>
    public async Task<SessionMoveSnapshot> MoveSessionAsync(SessionId id, LocationRef destination, InboxDeliveryMode? delivery = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(destination.Directory);
        if (destination.WorkspaceId is not null) throw new NotSupportedException("Explicit workspace movement is not supported by the local move service.");
        if (delivery is not (null or InboxDeliveryMode.Steer or InboxDeliveryMode.Queue)) throw new ArgumentOutOfRangeException(nameof(delivery));
        await EnsureConnectedAsync(ct);
        var source = (await RequestAsync(token => _client.GetAsync(id, token), "move source lookup", ct)).Data;
        if (source.Id != id) throw new InvalidOperationException("Move source lookup returned a different Session.");
        if (source.Location.WorkspaceId is not null) throw new NotSupportedException("Moving from an explicit workspace requires its source transport lifecycle and is unsupported here.");
        Observation entry;
        lock (_gate) entry = ObservationFor(id);
        await entry.Admission.WaitAsync(ct);
        var attempted = false;
        try
        {
            lock (_gate)
            {
                if (entry.Deleted) throw new InvalidOperationException("The Session was deleted.");
                if (entry.Move is { Pending: true }) throw new InvalidOperationException("A move is already admitted or unconfirmed. Refresh the Session; do not repost an ID-less control.");
                var origin = entry.Session is { } latest && latest.Time.Updated >= source.Time.Updated ? latest : source;
                entry.Session = origin;
                entry.Move = new(id, origin.Location, destination, delivery ?? InboxDeliveryMode.Steer, SessionMovePhase.Submitting);
                PublishObservation(entry);
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                attempted = true;
                await RequestAsync(async token => { await _client.MoveAsync(id, destination, delivery, token); return true; }, "move admission", ct);
                lock (_gate)
                {
                    if (entry.Move is { Phase: SessionMovePhase.Submitting } move) entry.Move = move with { Phase = SessionMovePhase.Admitted };
                    PublishObservation(entry);
                    _ = RefreshLocked(entry);
                    return entry.Move!;
                }
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (entry.Move?.Phase != SessionMovePhase.LocationChanged)
                    {
                        var rejected = !attempted || error is SessionApiException { StatusCode: { } status } && (int)status < 500;
                        entry.Move = entry.Move! with { Phase = rejected ? SessionMovePhase.Rejected : SessionMovePhase.Unconfirmed, Error = Describe(error) };
                    }
                    PublishObservation(entry);
                    if (!_lifetime.IsCancellationRequested) _ = RefreshLocked(entry);
                    if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) throw;
                    return entry.Move!;
                }
            }
        }
        finally { entry.Admission.Release(); }
    }

    /// <summary>Wake the existing receiver's backoff and join its next connection. Cancelling this
    /// wait neither stops automatic reconnection nor interrupts any Session execution.</summary>
    public async Task RetryConnectionAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_feed.Phase == SessionFeedPhase.Live) return;
            _retryConnection.TrySetResult();
        }
        await EnsureConnectedAsync(ct);
    }

    /// <summary>Explicit recovery reload through the same observer and origin-scoped readiness callback.</summary>
    public async Task<SessionObservationSnapshot> ReloadSessionAsync(SessionId id, CancellationToken ct = default)
    {
        await RetryConnectionAsync(ct);
        var snapshot = await RefreshObservationAsync(id, ct);
        if (snapshot.Session is not { } session || snapshot.Error is not null) return snapshot;
        try
        {
            var readiness = await RequestAsync(token => _readiness(session, token), "session recovery readiness", ct);
            if (readiness.SelectionError is not null || readiness.ExecutionError is not null)
                throw new InvalidOperationException(readiness.SelectionError ?? readiness.ExecutionError);
            lock (_gate)
            {
                var entry = ObservationFor(id);
                if (entry.Session?.Location == session.Location) entry.LocationUnavailable = null;
                return PublishObservation(entry);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (_gate)
            {
                var entry = ObservationFor(id);
                entry.Error = Describe(error);
                entry.LocationUnavailable = LocationRecoveryEvidence.From(id, session.Location, error) ?? entry.LocationUnavailable;
                entry.Synchronization = SessionSynchronization.Failed;
                return PublishObservation(entry);
            }
        }
    }

    public async Task<SessionObservationSnapshot> ObserveSessionAsync(SessionId id, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        await WaitForCreationAsync(id, ct);
        Task<SessionObservationSnapshot> refresh;
        lock (_gate)
        {
            var entry = ObservationFor(id);
            if (entry.Loaded && !entry.Invalidated && entry.Refresh is not { IsCompleted: false }) return PublishObservation(entry);
            refresh = RefreshLocked(entry);
        }
        return await refresh.WaitAsync(ct);
    }

    public async Task<SessionObservationSnapshot> RefreshObservationAsync(SessionId id, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        await WaitForCreationAsync(id, ct);
        Task<SessionObservationSnapshot> refresh;
        lock (_gate) refresh = RefreshLocked(ObservationFor(id));
        return await refresh.WaitAsync(ct);
    }

    public async IAsyncEnumerable<SessionObservationSnapshot> WatchSessionAsync(SessionId id,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await ObserveSessionAsync(id, linked.Token);
        var channel = Channel.CreateBounded<SessionObservationSnapshot>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        Observation entry;
        lock (_gate)
        {
            entry = ObservationFor(id);
            entry.Subscribers.Add(channel);
            channel.Writer.TryWrite(_observationSnapshot[id]);
        }
        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(linked.Token)) yield return snapshot;
        }
        finally { lock (_gate) entry.Subscribers.Remove(channel); }
    }

    private Observation ObservationFor(SessionId id)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_observed.TryGetValue(id, out var entry)) _observed[id] = entry = new(id) { Epoch = _feed.Epoch };
        return entry;
    }

    private Task WaitForCreationAsync(SessionId id, CancellationToken ct)
    {
        lock (_gate) return _observed.GetValueOrDefault(id) is { Creation: not null, CreationReady: { } ready }
            ? ready.Task.WaitAsync(ct) : Task.CompletedTask;
    }

    private Task<SessionObservationSnapshot> RefreshLocked(Observation entry)
    {
        entry.Invalidated = true;
        if (!entry.Loaded) { entry.AwaitingBaseline = true; entry.Synchronization = SessionSynchronization.Hydrating; }
        // Serial revalidation per Session. The single global SSE receiver never awaits HTTP.
        return entry.Refresh is { IsCompleted: false } pending ? pending : entry.Refresh = Task.Run(() => RefreshObserved(entry), _lifetime.Token);
    }

    private async Task<SessionObservationSnapshot> RefreshObserved(Observation entry)
    {
        while (true)
        {
            long version;
            long epoch;
            lock (_gate) { entry.Invalidated = false; version = entry.Version; epoch = entry.Epoch; }
            var found = false;
            var location = entry.Session?.Location;
            SessionInfo? loadedInfo = null;
            try
            {
                var info = (await RequestAsync(token => _client.GetAsync(entry.Id, token), "observed session lookup", _lifetime.Token)).Data;
                if (info.Id != entry.Id) throw new InvalidOperationException("Session lookup returned a different Session ID.");
                found = true;
                location = info.Location;
                loadedInfo = info;
                // Inbox before messages avoids losing an item promoted between the two reads.
                var inbox = (await RequestAsync(token => _client.InboxAsync(entry.Id, token), "observed inbox", _lifetime.Token)).Data;
                var messages = await ReadMessagesAsync(entry.Id, _lifetime.Token);
                var running = (await RequestAsync(token => _client.ActiveAsync(token), "observed active sessions", _lifetime.Token)).Data.ContainsKey(entry.Id.Value);
                await RefreshPermissionsAsync(entry.Id, _lifetime.Token);
                if (!entry.FormsHydrated) { await RefreshFormsAsync(info, _lifetime.Token); entry.FormsHydrated = true; }
                lock (_gate)
                {
                    if (epoch != entry.Epoch) continue;
                    if (_feed.Phase != SessionFeedPhase.Live)
                    {
                        entry.Synchronization = SessionSynchronization.Stale; entry.Refresh = null;
                        return PublishObservation(entry);
                    }
                    if (entry.Deleted) { entry.Refresh = null; return PublishObservation(entry); }
                    entry.Session = info;
                    if (entry.Move is { Pending: true } move && info.Location != move.Origin)
                        entry.Move = move with { Phase = SessionMovePhase.LocationChanged, Observed = info.Location, Error = null };
                    entry.Creation = null;
                    _formSessions[info.Id] = (info.ParentId, info.Location);
                    var merged = messages.ToList();
                    foreach (var message in entry.Messages.Where(message => !entry.AwaitingBaseline && entry.MessageVersions.GetValueOrDefault(message.Id) > version))
                    {
                        var index = merged.FindIndex(item => item.Id == message.Id);
                        if (index < 0) merged.Add(message); else merged[index] = message;
                    }
                    entry.Messages = merged.ToImmutableArray();
                    var pending = inbox.ToDictionary(item => item.Id);
                    foreach (var change in entry.InboxChanges.Where(pair => pair.Value.Version > version))
                        if (change.Value.Item is { } item) pending[change.Key] = item; else pending.Remove(change.Key);
                    var visible = entry.Messages.Select(message => message.Id).ToHashSet();
                    entry.Inbox = pending.Values.Where(item => !visible.Contains(item.Id) && !entry.ConsumedInputs.Contains(item.Id)).OrderBy(item => item.TimeCreated).ToImmutableArray();
                    if (entry.RunningVersion <= version) entry.Running = running;
                    var baseline = entry.AwaitingBaseline;
                    if (baseline)
                    {
                        entry.AwaitingBaseline = false;
                        entry.FullContent.Clear(); entry.FullToolInput.Clear();
                        foreach (var message in entry.Messages.OfType<AssistantMessage>())
                        {
                            var text = 0; var reasoning = 0;
                            foreach (var part in message.Content)
                            {
                                if (part is AssistantTextContent value)
                                { if (message.Time.Completed is not null || value.Text.Length > 0) entry.FullContent.Add((message.Id, true, text)); text++; }
                                if (part is AssistantReasoningContent thought)
                                { if (thought.Time?.Completed is not null || message.Time.Completed is not null || thought.Text.Length > 0) entry.FullContent.Add((message.Id, false, reasoning)); reasoning++; }
                                if (part is AssistantToolContent tool && (tool.State is not ToolStateStreaming input || input.Input.Length > 0)) entry.FullToolInput.Add((message.Id, tool.Id));
                            }
                        }
                        var buffered = entry.Buffered.DistinctBy(item => item.Id).ToArray();
                        entry.Buffered.Clear();
                        entry.Replaying = true;
                        try { foreach (var item in buffered) ObserveEvent(item); }
                        finally { entry.Replaying = false; }
                    }
                    entry.Loaded = true;
                    entry.Error = null;
                    ReconcileAdmissions(entry, version);
                    entry.NeedsReconciliation = false;
                    entry.Synchronization = entry.Invalidated ? SessionSynchronization.Hydrating : SessionSynchronization.Live;
                    if (baseline)
                        foreach (var monitor in _pendingRequests.Values.Where(item => item.SessionId == entry.Id && item.Admitted && !item.ObservationEnded))
                        {
                            monitor.ObservationEnded = true; monitor.Status = "Observation resynchronized";
                            monitor.Publish(); monitor.Updates.Writer.TryComplete();
                        }
                    if (entry.FailureVersion <= version && info.Outcome != SessionOutcome.Failed) entry.Failure = null;
                    if (info.Outcome == SessionOutcome.Failed && entry.Failure is null) entry.Failure = entry.Messages.OfType<AssistantMessage>().LastOrDefault()?.Error;
                    foreach (var id in entry.MessageVersions.Where(pair => pair.Value <= version).Select(pair => pair.Key).ToArray()) entry.MessageVersions.Remove(id);
                    foreach (var id in entry.InboxChanges.Where(pair => pair.Value.Version <= version).Select(pair => pair.Key).ToArray()) entry.InboxChanges.Remove(id);
                    if (_sessionId == entry.Id) { CurrentSession = entry.Session; ReconciledMessages = entry.Messages; ReconciledInbox = entry.Inbox; }
                    PublishForms();
                    var snapshot = PublishObservation(entry);
                    if (!entry.Invalidated) { entry.Refresh = null; return snapshot; }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (epoch != entry.Epoch) continue;
                    if (!found && error is SessionApiException { StatusCode: HttpStatusCode.NotFound } && entry.Creation is null) MarkObservedDeleted(entry);
                    entry.Error = Describe(error);
                    if (LocationRecoveryEvidence.From(entry.Id, location, error) is { } unavailable)
                    {
                        entry.LocationUnavailable = unavailable;
                        entry.Session = loadedInfo ?? entry.Session;
                    }
                    entry.NeedsReconciliation = true;
                    entry.Synchronization = entry.Deleted ? SessionSynchronization.Deleted : SessionSynchronization.Failed;
                    entry.Refresh = null;
                    entry.Invalidated = true;
                    return PublishObservation(entry);
                }
            }
        }
    }

    private SessionObservationSnapshot PublishObservation(Observation entry)
    {
        var permissions = _permissions.Values.Where(permission => permission.SessionId == entry.Id).ToImmutableArray();
        var snapshot = new SessionObservationSnapshot(entry.Id, entry.Session, entry.Messages, entry.Inbox, permissions,
            entry.Running, entry.Error, ++entry.Revision, entry.Deleted)
        { ExecutionError = entry.Admissions.Values.FirstOrDefault(item => item.Unconfirmed)?.Error
                ?? entry.Admissions.Values.LastOrDefault()?.Error ?? entry.Failure?.Message, Synchronization = entry.Synchronization,
          Admissions = entry.Admissions.Values.Select(item => item.Snapshot(entry.Id)).ToImmutableArray(),
          LocationUnavailable = entry.LocationUnavailable?.Location == entry.Session?.Location ? entry.LocationUnavailable : null,
          Move = entry.Move };
        _observationSnapshot = _observationSnapshot.SetItem(entry.Id, snapshot);
        var previous = _tabActivity.GetValueOrDefault(entry.Id) ?? new();
        var info = entry.Session;
        var unread = info?.Time.Idle is { } idle && (info.Time.Viewed is null || idle > info.Time.Viewed);
        _tabActivity = _tabActivity.SetItem(entry.Id, previous with { Busy = snapshot.Busy, Title = info?.Title,
            Unread = unread ? info?.Outcome == SessionOutcome.Failed ? SessionTabUnread.Error : SessionTabUnread.Activity : null,
            PromptPulse = entry.PromptPulse });
        UpdateTabAttention();
        foreach (var subscriber in entry.Subscribers) subscriber.Writer.TryWrite(snapshot);
        return snapshot;
    }

    private void MarkObservedDeleted(Observation entry)
    {
        entry.Deleted = true; entry.Running = false; entry.Session = null; entry.Messages = []; entry.Inbox = [];
        entry.Synchronization = SessionSynchronization.Deleted;
        _deletedSessions = _deletedSessions.Add(entry.Id);
        foreach (var id in _permissions.Where(pair => pair.Value.SessionId == entry.Id).Select(pair => pair.Key).ToArray()) _permissions.Remove(id);
        _permissionSnapshot = _permissions.Values.ToArray();
    }

    private void ObserveEvent(ServerEventEnvelope item)
    {
        if (!item.Data.TryGetProperty("sessionID", out var identity) || identity.ValueKind != JsonValueKind.String
            || identity.GetString() is not { } value || !value.StartsWith("ses", StringComparison.Ordinal)) return;
        var id = OpenCode.Schema.SessionId.FromExisting(value);
        if (item.Type == "session.deleted") _deletedSessions = _deletedSessions.Add(id);
        if (!_observed.TryGetValue(id, out var entry))
        {
            if (item.Type is not ("session.created" or "session.step.started")) return;
            entry = ObservationFor(id);
            entry.Epoch = _feed.Epoch;
            entry.AwaitingBaseline = true;
            entry.Synchronization = SessionSynchronization.Hydrating;
            entry.Running = _tabActivity.GetValueOrDefault(id)?.Busy == true;
        }
        if (entry.AwaitingBaseline && !entry.Replaying && item.Type != "session.deleted")
        {
            entry.Buffered.Add(item);
            if (entry.Refresh is not { IsCompleted: false } && (entry.Creation is null || item.Type == "session.created")) _ = RefreshLocked(entry);
            return;
        }
        entry.Version++;
        if (item.Durable is { } durable && durable.AggregateId != id.Value) throw new JsonException("Observed event belongs to a different aggregate.");
        if (item.Type == "session.deleted") { MarkObservedDeleted(entry); PublishObservation(entry); return; }
        if (entry.Deleted) return;
        if (item.Type == "session.execution.started") { entry.Running = true; entry.RunningVersion = entry.Version; entry.Error = null; entry.Failure = null; entry.FailureVersion = entry.Version; }
        if (IsTerminal(item.Type)) { entry.Running = false; entry.RunningVersion = entry.Version; }
        if (item.Type == "session.execution.failed" && item.Data.TryGetProperty("error", out var failure))
        { entry.Failure = failure.Deserialize(OpenCodeJsonContext.Default.SessionStructuredError); entry.FailureVersion = entry.Version; }
        var changed = ApplyObservedMessage(entry, item);
        if (item.Type == "session.inbox.enqueued")
        {
            var data = Decode(item, OpenCodeJsonContext.Default.SessionInboxEnqueuedEventData);
            var inbox = new SessionInboxItem(data.InboxId, id, data.Item.Delivery, data.Item.Payload, EventTime(item));
            if (entry.ConsumedInputs.Contains(inbox.Id)) return;
            if (entry.Replaying && (entry.Messages.Any(message => message.Id == inbox.Id) || entry.Inbox.Any(previous => previous.Id == inbox.Id))) return;
            entry.Inbox = entry.Inbox.Where(previous => previous.Id != inbox.Id).Append(inbox).ToImmutableArray();
            entry.InboxChanges[inbox.Id] = (entry.Version, inbox);
            ObserveAdmissionEvent(entry, inbox.Id, SessionAdmissionPhase.Pending, inbox.Delivery);
            if (inbox.Payload is UserInboxPayload && _sessionId != id) entry.PromptPulse++;
        }
        if (item.Type is "session.inbox.delivered" or "session.inbox.cancelled")
        {
            var inboxID = MessageId.FromExisting(item.Data.GetProperty("inboxID").GetString()!);
            var inbox = entry.Inbox.FirstOrDefault(inbox => inbox.Id == inboxID);
            entry.Inbox = entry.Inbox.Where(inbox => inbox.Id != inboxID).ToImmutableArray();
            entry.InboxChanges[inboxID] = (entry.Version, null);
            entry.ConsumedInputs.Add(inboxID);
            ObserveAdmissionEvent(entry, inboxID, item.Type == "session.inbox.cancelled" ? SessionAdmissionPhase.Cancelled : SessionAdmissionPhase.Delivered);
            if (item.Type == "session.inbox.delivered" && inbox is not null)
            {
                SessionMessage? message = inbox.Payload switch
                {
                    UserInboxPayload user => new UserMessage { Id = inbox.Id, Time = new(EventTime(item)), Text = user.Text,
                        Files = user.Files, Agents = user.Agents, Skills = user.Skills, Metadata = user.Metadata },
                    SyntheticInboxPayload synthetic => new SyntheticMessage { Id = inbox.Id, Time = new(EventTime(item)), Text = synthetic.Text,
                        Description = synthetic.Description, Metadata = synthetic.Metadata },
                    _ => null
                };
                if (message is not null) PutObservedMessage(entry, message);
            }
        }
        if (item.Type == "session.inbox.delivery.changed")
        {
            var inboxID = MessageId.FromExisting(item.Data.GetProperty("inboxID").GetString()!);
            var inbox = entry.Inbox.FirstOrDefault(inbox => inbox.Id == inboxID);
            if (inbox is not null)
            {
                var delivery = item.Data.GetProperty("delivery").GetString() switch { "steer" => InboxDeliveryMode.Steer, "queue" => InboxDeliveryMode.Queue, _ => throw new JsonException("Unknown inbox delivery mode.") };
                inbox = inbox with { Delivery = delivery };
                entry.Inbox = entry.Inbox.Select(previous => previous.Id == inboxID ? inbox : previous).ToImmutableArray();
                entry.InboxChanges[inboxID] = (entry.Version, inbox);
                ObserveAdmissionEvent(entry, inboxID, delivery: delivery);
            }
        }
        if (item.Type == "session.renamed" && entry.Session is { } session)
            entry.Session = session with { Title = item.Data.GetProperty("title").GetString() };
        if (!entry.Replaying) PublishObservation(entry);
        // Stream content is projected immediately. Other domains (shell, compaction, move, revert,
        // instructions, commands) refresh their actual typed projected messages, not user prompts.
        if (!entry.Replaying && (!changed || entry.Session is null && entry.Creation is null)
            && !item.Type.EndsWith(".delta", StringComparison.Ordinal) && !item.Type.StartsWith("permission.", StringComparison.Ordinal))
            _ = RefreshLocked(entry);
        if (entry.Replaying && !changed && !item.Type.StartsWith("session.inbox.", StringComparison.Ordinal)
            && !item.Type.EndsWith(".delta", StringComparison.Ordinal)) entry.Invalidated = true;
    }

    private static DateTimeOffset EventTime(ServerEventEnvelope item) => Timestamp(item) ?? throw new JsonException("Session event is missing its creation time.");

    private static void PutObservedMessage(Observation entry, SessionMessage message)
    {
        var index = entry.Messages.ToList().FindIndex(item => item.Id == message.Id);
        entry.Messages = index < 0 ? entry.Messages.Add(message) : entry.Messages.SetItem(index, message);
        entry.MessageVersions[message.Id] = entry.Version;
    }

    private bool ApplyObservedMessage(Observation entry, ServerEventEnvelope item)
    {
        if (!item.Data.TryGetProperty("assistantMessageID", out var idValue) || idValue.ValueKind != JsonValueKind.String) return false;
        var id = MessageId.FromExisting(idValue.GetString()!);
        var assistant = entry.Messages.OfType<AssistantMessage>().FirstOrDefault(message => message.Id == id);
        if (item.Type == "session.step.started")
        {
            var data = Decode(item, OpenCodeJsonContext.Default.SessionStepStartedEventData);
            if (entry.Replaying && assistant is not null && (assistant.Time.Completed is null || assistant.Time.Completed >= EventTime(item))) return true;
            assistant = assistant is null ? new AssistantMessage { Id = id, Time = new(EventTime(item)), Agent = data.Agent, Model = data.Model, Content = [] }
                : assistant with { Agent = data.Agent, Model = data.Model, Retry = null, Error = null, Finish = null, RawFinish = null,
                    ProviderState = null, Time = new(assistant.Time.Created) };
            if (data.Snapshot is { } snapshot) assistant = assistant with { Snapshot = new(snapshot) };
            PutObservedMessage(entry, assistant);
            return true;
        }
        if (assistant is null) { if (entry.Loaded) _ = RefreshLocked(entry); return false; }
        var content = assistant.Content.ToList();
        switch (item.Type)
        {
            case "session.step.streamed": assistant = assistant with { Time = assistant.Time with { Streamed = EventTime(item) } }; break;
            case "session.step.ended":
                var ended = Decode(item, OpenCodeJsonContext.Default.SessionStepEndedEventData);
                assistant = assistant with { Time = assistant.Time with { Completed = EventTime(item) }, Finish = ended.Finish,
                    RawFinish = ended.RawFinish, ProviderState = ended.ProviderState, Cost = ended.Cost, Tokens = ended.Tokens,
                    Snapshot = new(assistant.Snapshot?.Start, ended.Snapshot, ended.Files) };
                break;
            case "session.step.failed":
                var failed = Decode(item, OpenCodeJsonContext.Default.SessionStepFailedEventData);
                assistant = assistant with { Time = assistant.Time with { Completed = EventTime(item) }, Error = failed.Error, Retry = null,
                    Finish = failed.Finish == "content-filter" ? LlmFinishReason.ContentFilter : assistant.Finish,
                    RawFinish = failed.RawFinish, ProviderState = failed.ProviderState, Cost = failed.Cost ?? assistant.Cost, Tokens = failed.Tokens ?? assistant.Tokens };
                break;
            case "session.retry.scheduled":
                var retry = Decode(item, OpenCodeJsonContext.Default.SessionRetryScheduledEventData);
                assistant = assistant with { Retry = new(retry.Attempt, DateTimeOffset.FromUnixTimeMilliseconds((long)retry.At), retry.Error) }; break;
            case "session.text.started":
                var textStart = Decode(item, OpenCodeJsonContext.Default.SessionTextStartedEventData);
                if (ContentPosition(content, true, textStart.Ordinal) < 0) content.Add(new AssistantTextContent("")); break;
            case "session.reasoning.started":
                var reasoning = Decode(item, OpenCodeJsonContext.Default.SessionReasoningStartedEventData);
                if (ContentPosition(content, false, reasoning.Ordinal) < 0) content.Add(new AssistantReasoningContent("", reasoning.State, new(EventTime(item)))); break;
            case "session.text.delta": case "session.reasoning.delta":
                var delta = Decode(item, OpenCodeJsonContext.Default.SessionContentDeltaEventData);
                if (assistant.Time.Completed is not null || entry.FullContent.Contains((id, item.Type.StartsWith("session.text", StringComparison.Ordinal), checked((int)delta.Ordinal)))) return true;
                var position = ContentPosition(content, item.Type.StartsWith("session.text", StringComparison.Ordinal), delta.Ordinal);
                if (position >= 0) content[position] = content[position] switch
                { AssistantTextContent text => text with { Text = text.Text + delta.Delta }, AssistantReasoningContent thought => thought with { Text = thought.Text + delta.Delta }, _ => content[position] };
                break;
            case "session.text.ended": case "session.reasoning.ended":
                var full = Decode(item, OpenCodeJsonContext.Default.SessionContentEndedEventData);
                entry.FullContent.Add((id, item.Type.StartsWith("session.text", StringComparison.Ordinal), checked((int)full.Ordinal)));
                var at = ContentPosition(content, item.Type.StartsWith("session.text", StringComparison.Ordinal), full.Ordinal);
                if (at >= 0) content[at] = content[at] switch
                { AssistantTextContent text => text with { Text = full.Text, State = full.State },
                  AssistantReasoningContent thought => thought with { Text = full.Text, State = full.State ?? thought.State, Time = new(thought.Time?.Created ?? EventTime(item), EventTime(item)) }, _ => content[at] };
                break;
            case "session.tool.input.started":
                var start = Decode(item, OpenCodeJsonContext.Default.SessionToolInputStartedEventData);
                if (!content.OfType<AssistantToolContent>().Any(tool => tool.Id == start.Id))
                    content.Add(new AssistantToolContent(start.Id, start.Name, new ToolStateStreaming(""), new(EventTime(item))));
                break;
            default:
                if (!item.Type.StartsWith("session.tool.", StringComparison.Ordinal)) return false;
                var toolId = item.Data.GetProperty("id").GetString();
                var toolIndex = content.FindIndex(part => part is AssistantToolContent tool && tool.Id == toolId);
                if (toolIndex < 0) return false;
                var tool = (AssistantToolContent)content[toolIndex];
                switch (item.Type)
                {
                    case "session.tool.input.delta" when tool.State is ToolStateStreaming streaming && !entry.FullToolInput.Contains((id, tool.Id)):
                        tool = tool with { State = new ToolStateStreaming(streaming.Input + Decode(item, OpenCodeJsonContext.Default.SessionToolInputDeltaEventData).Delta) }; break;
                    case "session.tool.input.ended" when tool.State is ToolStateStreaming:
                        entry.FullToolInput.Add((id, tool.Id));
                        tool = tool with { State = new ToolStateStreaming(Decode(item, OpenCodeJsonContext.Default.SessionToolInputEndedEventData).Text) }; break;
                    case "session.tool.called" when tool.Time.Completed is null:
                        var call = Decode(item, OpenCodeJsonContext.Default.SessionToolCalledEventData);
                        tool = tool with { State = new ToolStateRunning(call.Input, ImmutableDictionary<string, JsonElement>.Empty), Executed = call.Executed,
                            ProviderState = call.State, Time = tool.Time with { Ran = EventTime(item) } }; break;
                    case "session.tool.progress" when tool.State is ToolStateRunning running:
                        tool = tool with { State = running with { Metadata = Decode(item, OpenCodeJsonContext.Default.SessionToolProgressEventData).Metadata } }; break;
                    case "session.tool.success" when tool.State is ToolStateRunning running:
                        var success = Decode(item, OpenCodeJsonContext.Default.SessionToolSuccessEventData);
                        tool = tool with { State = new ToolStateCompleted(running.Input, success.Content, success.Metadata), Executed = success.Executed || tool.Executed == true,
                            ProviderResultState = success.ResultState, Time = tool.Time with { Completed = EventTime(item) } }; break;
                    case "session.tool.failed":
                        var failure = Decode(item, OpenCodeJsonContext.Default.SessionToolFailedEventData);
                        tool = tool with { State = new ToolStateError(tool.State is ToolStateRunning state ? state.Input : ImmutableDictionary<string, JsonElement>.Empty,
                            failure.Error, failure.Content, failure.Metadata), Executed = failure.Executed || tool.Executed == true,
                            ProviderResultState = failure.ResultState, Time = tool.Time with { Completed = EventTime(item) } }; break;
                }
                content[toolIndex] = tool;
                break;
        }
        PutObservedMessage(entry, assistant with { Content = content.ToImmutableArray() });
        return true;
    }

    private static int ContentPosition(IReadOnlyList<AssistantContent> content, bool text, double ordinal) =>
        content.Select((part, index) => (part, index)).Where(item => text ? item.part is AssistantTextContent : item.part is AssistantReasoningContent)
            .Skip(checked((int)ordinal)).Select(item => item.index).DefaultIfEmpty(-1).First();
}
