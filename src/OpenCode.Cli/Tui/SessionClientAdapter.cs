namespace OpenCode.Cli.Tui;

using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Protocol;
using OpenCode.Cli.Tui.Components;
using OpenCode.Cli.Tui.Forms;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Schema;

public sealed record SessionContentSnapshot(MessageId AssistantId, string Kind, double Ordinal, string Text, bool Ended,
    IReadOnlyDictionary<string, JsonElement>? State, int Order, DateTimeOffset? Created, DateTimeOffset? Completed);
public sealed record SessionStepSnapshot(MessageId Id, SessionStepStartedEventData? Started,
    SessionStepEndedEventData? Ended, SessionStepFailedEventData? Failed, bool Streamed,
    DateTimeOffset? Created, DateTimeOffset? Completed, DateTimeOffset? StreamedAt, AssistantMessage? ReadModel);
public sealed record SessionToolSnapshot(MessageId AssistantId, string Id, string Name, string Status, int Order,
    string Input, IReadOnlyDictionary<string, JsonElement>? Arguments, IReadOnlyDictionary<string, JsonElement>? Metadata,
    IReadOnlyList<ToolContent>? Output, SessionStructuredError? Error, DateTimeOffset? Created, DateTimeOffset? Completed,
    DateTimeOffset? Ran, bool? Executed, IReadOnlyDictionary<string, JsonElement>? ProviderState,
    IReadOnlyDictionary<string, JsonElement>? ProviderResultState);
public sealed record SessionResponseSnapshot(SessionId SessionId, MessageId PromptId,
    ImmutableArray<SessionContentSnapshot> Content, ImmutableArray<SessionStepSnapshot> Steps,
    string Status, bool Admitted, bool Delivered, SessionOutcome? Outcome, string? InterruptionReason,
    SessionStructuredError? Error, ServerEventEnvelope? UnsupportedEvent, ImmutableArray<SessionToolSnapshot> Tools,
    DateTimeOffset? DeliveredAt, string? SessionTitle = null);

public sealed record SessionClientTimeouts(TimeSpan Handshake, TimeSpan Request, TimeSpan EventIdle)
{
    public static SessionClientTimeouts Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
}

/// <summary>
/// Owns a volatile session subscription, not model execution. Readiness must come
/// from the selected server's actual contract; lack of readiness prevents admission.
/// </summary>
public sealed partial class SessionClientAdapter : IAsyncDisposable
{
    private readonly SessionHttpClient _client;
    private readonly Func<SessionInfo?, CancellationToken, Task<PromptConfiguration>> _readiness;
    private readonly Func<SessionCreateInput> _create;
    private readonly SessionClientTimeouts _timeouts;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _httpOperations = [];
    private Task _pump = Task.CompletedTask;
    private CancellationTokenSource? _subscription;
    private TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SessionId? _sessionId;
    private bool _needsReconciliation
    {
        get => _sessionId is { } id && _observed.GetValueOrDefault(id)?.NeedsReconciliation == true;
        set { if (_sessionId is { } id) ObservationFor(id).NeedsReconciliation = value; }
    }
    private Exception? _connectionFailure;
    // Compatibility boundary for the separately owned Forms partial: its visible request is
    // selected from the Session-bound monitors, never ownership of all background work.
    private Pending? _pending => _pendingRequests.Values.LastOrDefault(pending => pending.SessionId == _sessionId);
    private int _disposed;
    private int _submitting => _submissions.Values.Count(submission => submission.SessionId == _sessionId);
    private long _viewVersion;
    private SessionCreateInput? _unconfirmedCreation
    {
        get => _sessionId is { } id ? _observed.GetValueOrDefault(id)?.Creation : null;
        set { if (_sessionId is { } id) ObservationFor(id).Creation = value; }
    }
    private OpeningSession? _opening;
    private string? _confirmedMutation;

    private sealed class OpeningSession(SessionId id)
    {
        internal readonly SessionId Id = id;
        internal long Version;
        internal readonly Dictionary<PermissionId, (long Version, PermissionRequest? Request)> Permissions = [];
    }
    private readonly Dictionary<PermissionId, PermissionRequest> _permissions = [];
    private readonly Dictionary<PermissionId, long> _permissionVersions = [];
    private long _permissionVersion;
    private IReadOnlyList<PermissionRequest> _permissionSnapshot = [];
    private ImmutableDictionary<SessionId, SessionTabActivity> _tabActivity = ImmutableDictionary<SessionId, SessionTabActivity>.Empty;
    public IReadOnlyDictionary<SessionId, SessionTabActivity> TabActivity { get { lock (_gate) return _tabActivity; } }
    public IReadOnlyList<PermissionRequest> Permissions { get { lock (_gate) return _permissionSnapshot; } }
    private bool _persistentPermissionGrants;
    public bool PersistentPermissionGrants { get { lock (_gate) return _persistentPermissionGrants; } }

    private sealed class Fragment(int order)
    {
        internal readonly int Order = order;
        internal readonly StringBuilder Text = new();
        internal bool Ended;
        internal IReadOnlyDictionary<string, JsonElement>? State;
        internal DateTimeOffset? Created;
        internal DateTimeOffset? Completed;
    }

    private sealed class Step
    {
        internal SessionStepStartedEventData? Started;
        internal SessionStepEndedEventData? Ended;
        internal SessionStepFailedEventData? Failed;
        internal bool Streamed;
        internal DateTimeOffset? Created;
        internal DateTimeOffset? Completed;
        internal DateTimeOffset? StreamedAt;
        internal AssistantMessage? ReadModel;
    }

    private sealed class Tool(string name, int order)
    {
        internal readonly string Name = name;
        internal readonly int Order = order;
        internal string Status = "streaming";
        internal bool InputEnded;
        internal readonly StringBuilder Input = new();
        internal IReadOnlyDictionary<string, JsonElement>? Arguments;
        internal IReadOnlyDictionary<string, JsonElement>? Metadata;
        internal IReadOnlyList<ToolContent>? Output;
        internal SessionStructuredError? Error;
        internal DateTimeOffset? Created;
        internal DateTimeOffset? Completed;
        internal DateTimeOffset? Ran;
        internal bool? Executed;
        internal IReadOnlyDictionary<string, JsonElement>? ProviderState;
        internal IReadOnlyDictionary<string, JsonElement>? ProviderResultState;
    }

    private sealed class PendingUpdates
    {
        private readonly HashSet<Channel<SessionResponseSnapshot>> _readers = [];
        private bool _completed;
        private Exception? _error;
        internal bool Completed => _completed;
        internal PendingUpdates Writer => this;
        internal Channel<SessionResponseSnapshot> Subscribe()
        {
            var reader = Channel.CreateBounded<SessionResponseSnapshot>(new BoundedChannelOptions(1)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
            _readers.Add(reader);
            if (_completed) reader.Writer.TryComplete(_error);
            return reader;
        }
        internal void Remove(Channel<SessionResponseSnapshot> reader) => _readers.Remove(reader);
        internal bool TryWrite(SessionResponseSnapshot value)
        {
            if (_completed) return false;
            foreach (var reader in _readers) reader.Writer.TryWrite(value);
            return true;
        }
        internal bool TryComplete(Exception? error = null)
        {
            if (_completed) return false;
            _completed = true; _error = error;
            foreach (var reader in _readers) reader.Writer.TryComplete(error);
            return true;
        }
    }

    private sealed class Pending(SessionClientAdapter owner, SessionId sessionId, MessageId promptId)
    {
        internal readonly SessionId SessionId = sessionId;
        internal readonly MessageId PromptId = promptId;
        internal readonly PendingUpdates Updates = new();
        internal int Readers;
        internal readonly HashSet<EventId> Seen = [];
        internal readonly Dictionary<(MessageId Assistant, string Kind, double Ordinal), Fragment> Fragments = [];
        internal readonly Dictionary<MessageId, Step> Steps = [];
        internal readonly Dictionary<MessageId, int> StepOrder = [];
        internal readonly Dictionary<(MessageId Assistant, string Id), Tool> Tools = [];
        internal int PartOrder;
        internal bool Admitted;
        internal bool Delivered;
        internal bool DeliveryObserved;
        internal readonly HashSet<MessageId> ReconciledAssistants = [];
        internal string Status = "Admitting";
        internal SessionStructuredError? Error;
        internal ServerEventEnvelope? Unsupported;
        internal SessionOutcome? Outcome;
        internal string? InterruptionReason;
        internal readonly List<ServerEventEnvelope> Deferred = [];
        internal readonly HashSet<EventId> DeferredApplied = [];
        internal double? EnqueuedSequence;
        internal double? BusyStartSequence;
        internal bool ReconcileRequested;
        internal bool ObservationEnded;
        internal DateTimeOffset? DeliveredAt;

        internal SessionResponseSnapshot Snapshot() => new(SessionId, PromptId,
            Fragments.OrderBy(pair => StepOrder.GetValueOrDefault(pair.Key.Assistant)).ThenBy(pair => pair.Value.Order)
                .Select(pair => new SessionContentSnapshot(pair.Key.Assistant, pair.Key.Kind,
                    pair.Key.Ordinal, pair.Value.Text.ToString(), pair.Value.Ended, pair.Value.State, pair.Value.Order, pair.Value.Created, pair.Value.Completed)).ToImmutableArray(),
            Steps.OrderBy(pair => StepOrder[pair.Key]).Select(pair => new SessionStepSnapshot(pair.Key, pair.Value.Started,
                pair.Value.Ended, pair.Value.Failed, pair.Value.Streamed, pair.Value.Created, pair.Value.Completed, pair.Value.StreamedAt, pair.Value.ReadModel)).ToImmutableArray(),
            owner.PendingStatus(this), Admitted, Delivered, Outcome, InterruptionReason, Error, Unsupported,
            Tools.Select(pair => new SessionToolSnapshot(pair.Key.Assistant, pair.Key.Id, pair.Value.Name, pair.Value.Status,
                pair.Value.Order, pair.Value.Input.ToString(), pair.Value.Arguments, pair.Value.Metadata, pair.Value.Output,
                pair.Value.Error, pair.Value.Created, pair.Value.Completed, pair.Value.Ran, pair.Value.Executed,
                pair.Value.ProviderState, pair.Value.ProviderResultState)).ToImmutableArray(), DeliveredAt);

        internal void Publish() => Updates.Writer.TryWrite(Snapshot());
    }

    public SessionClientAdapter(ServiceEndpoint endpoint,
        Func<SessionInfo?, CancellationToken, Task<PromptConfiguration>> readiness, Func<SessionCreateInput> create, SessionId? sessionId = null,
        SessionClientTimeouts? timeouts = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(create);
        _timeouts = timeouts ?? SessionClientTimeouts.Default;
        foreach (var timeout in new[] { _timeouts.Handshake, _timeouts.Request, _timeouts.EventIdle })
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(timeouts), "Timeouts must be positive and within the supported timer range.");
        _client = new SessionHttpClient(endpoint);
        ConfigureForms(new(
            async (owner, location, token) => (await _client.ListFormsAsync(owner, location.Directory, location.WorkspaceId?.Value, token)).Data,
            (request, token) => _client.ReplyFormAsync(request.SessionId, request.FormId, request.Reply.Answer,
                request.Location.Directory, request.Location.WorkspaceId?.Value, token),
            (request, token) => _client.CancelFormAsync(request.SessionId, request.FormId,
                request.Location.Directory, request.Location.WorkspaceId?.Value, token)));
        _readiness = readiness;
        _create = create;
        _sessionId = sessionId;
        _needsReconciliation = sessionId is not null;
    }

    public MessageId? LastPromptId { get; private set; }
    public SessionId? SessionId { get { lock (_gate) return _sessionId; } }
    public SessionInfo? CurrentSession { get; private set; }
    public IReadOnlyList<SessionMessage>? ReconciledMessages { get; private set; }
    public IReadOnlyList<SessionInboxItem> ReconciledInbox { get; private set; } = [];

    public async Task<PromptConfiguration> PrepareAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await PrepareCoreAsync(cancellationToken);
            lock (_gate) _confirmedMutation = null;
            return result;
        }
        catch (Exception exception)
        {
            string? mutation;
            lock (_gate) { mutation = _confirmedMutation; if (mutation is not null) _needsReconciliation = true; }
            if (mutation is not null) throw MutationRefreshFailure(mutation, exception);
            throw;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _connectionGate.WaitAsync(cancellationToken);
        Task connected;
        try
        {
            if (_pump.IsCompleted)
            {
                lock (_gate) _connectionFailure = null;
                _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _subscription?.Dispose();
                _subscription = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _pump = PumpAsync(_subscription.Token);
            }
            lock (_gate) connected = _connected.Task;
        }
        finally { _connectionGate.Release(); }
        try { await connected.WaitAsync(_timeouts.Handshake, _clock, cancellationToken); }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Server did not send server.connected within {_timeouts.Handshake.TotalSeconds:g} seconds. The shared receiver is reconnecting; no prompt was admitted by this wait.", exception);
        }
    }

    private async Task<PromptConfiguration> PrepareCoreAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        SessionId? session;
        lock (_gate) session = _sessionId;
        SessionObservationSnapshot? observed = null;
        if (session is SessionId id)
        {
            observed = await RefreshObservationAsync(id, cancellationToken);
            if (observed.Session is null && _observed.GetValueOrDefault(id)?.Creation is null)
                throw new InvalidOperationException(observed.Error ?? "Session not found.");
        }
        var info = observed?.Session;
        var readiness = await RequestAsync(token => _readiness(info, token), "execution readiness", cancellationToken);
        await RefreshPermissionCapabilitiesAsync(cancellationToken);
        await RefreshFormsAsync(info, cancellationToken);
        lock (_gate)
        {
            if (_connectionFailure is not null)
            {
                if (session is { } failed) ObservationFor(failed).NeedsReconciliation = true;
                throw new IOException("The event stream disconnected during preparation. Reload to reconcile before submitting.", _connectionFailure);
            }
        }
        return readiness with { Messages = observed?.Messages, SessionId = info?.Id, SessionTitle = info?.Title,
            Directory = info?.Location.Directory ?? readiness.Directory };
    }

    public IAsyncEnumerable<SessionResponseSnapshot> PromptAsync(string text, CancellationToken cancellationToken)
    {
        lock (_gate) return PromptForOrigin(_sessionId, new SessionPromptInput(text), _sessionId is null ? _create() : null, _viewVersion, cancellationToken);
    }

    public IAsyncEnumerable<SessionResponseSnapshot> PromptAsync(SessionId sessionId, string text, CancellationToken cancellationToken = default)
    {
        return PromptAsync(sessionId, new SessionPromptInput(text), cancellationToken);
    }

    public IAsyncEnumerable<SessionResponseSnapshot> PromptAsync(SessionId? sessionId, PromptInput input, CancellationToken cancellationToken = default) =>
        PromptAsync(sessionId, new SessionPromptInput(input.Text, Files: input.Files, Agents: input.Agents, Skills: input.Skills), cancellationToken);

    public IAsyncEnumerable<SessionResponseSnapshot> PromptAsync(SessionId? sessionId, SessionPromptInput input, CancellationToken cancellationToken = default)
    {
        var captured = SnapshotPrompt(input);
        lock (_gate) return PromptForOrigin(sessionId, captured, sessionId is null ? _create() : null,
            _sessionId == sessionId ? _viewVersion : -1, cancellationToken);
    }

    public IAsyncEnumerable<SessionResponseSnapshot> PromptAsync(SessionId? sessionId, SessionPromptInput input,
        PromptSelection selection, CancellationToken cancellationToken)
    {
        var captured = SnapshotPrompt(input);
        lock (_gate) return PromptForOrigin(sessionId, captured, sessionId is null ? _create() with
            { Location = selection.Location, Agent = selection.Agent?.Value, Model = selection.Model } : null,
            _sessionId == sessionId ? _viewVersion : -1, cancellationToken, selection);
    }

    public void NewConversation()
    {
        lock (_gate)
        {
            if (_opening is not null) throw new InvalidOperationException("Another view is still opening.");
            _viewVersion++;
            _sessionId = null;
            _confirmedMutation = null;
            _unconfirmedCreation = null;
            CurrentSession = null;
            LastPromptId = null;
            ReconciledMessages = null;
            ReconciledInbox = [];
            PublishForms();
        }
    }

    public void ForgetDeletedSession(SessionId id)
    {
        lock (_gate)
        {
            if (_observed.TryGetValue(id, out var entry)) { MarkObservedDeleted(entry); PublishObservation(entry); }
            _deletedSessions = _deletedSessions.Add(id);
            foreach (var pending in _pendingRequests.Values.Where(pending => pending.SessionId == id))
            { pending.ObservationEnded = true; pending.Updates.Writer.TryComplete(); }
            if (_sessionId == id) { _sessionId = null; CurrentSession = null; _viewVersion++; ReconciledMessages = null; ReconciledInbox = []; PublishForms(); }
        }
    }

    public async Task<PromptConfiguration> OpenSessionAsync(SessionId id, CancellationToken cancellationToken)
    {
        OpeningSession opening;
        SessionId? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_opening is not null) throw new InvalidOperationException("Another view is still opening.");
            previous = _sessionId;
            _opening = opening = new(id);
        }
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var observed = await ObserveSessionAsync(id, cancellationToken);
            var session = observed.Session ?? throw new InvalidOperationException(observed.Error ?? "Session not found.");
            var messages = observed.Messages;
            var inbox = observed.Inbox;
            long version;
            lock (_gate) version = opening.Version;
            var permissions = (await RequestAsync(token => _client.ListSessionPermissionsAsync(id, token), "target-session permission hydration", cancellationToken)).Data;
            var configuration = await RequestAsync(token => _readiness(session, token), "target-session readiness", cancellationToken);
            await RefreshPermissionCapabilitiesAsync(cancellationToken);
            await RefreshFormsAsync(session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!ReferenceEquals(_opening, opening) || _sessionId != previous)
                    throw new InvalidOperationException("Session ownership changed while the target session was loading.");
                if (_connectionFailure is not null) throw new IOException("The event stream disconnected while opening the session. The previous session remains selected.", _connectionFailure);
                _sessionId = id;
                _viewVersion++;
                _confirmedMutation = null;
                CurrentSession = session;
                ReconciledMessages = messages;
                ReconciledInbox = inbox;
                if (previous != id) LastPromptId = null;
                var familyPermissions = _permissions.Values.Where(permission => permission.SessionId != id
                    && FormDescendants(id).Contains(permission.SessionId)).ToArray();
                foreach (var permission in familyPermissions) _permissions[permission.Id] = permission;
                foreach (var permission in permissions) _permissions[permission.Id] = permission;
                foreach (var change in opening.Permissions.Where(pair => pair.Value.Version > version))
                {
                    if (change.Value.Request is { } request) _permissions[change.Key] = request;
                    else _permissions.Remove(change.Key);
                }
                _permissionSnapshot = _permissions.Values.ToArray();
                PublishForms();
            }
            return configuration with { Messages = messages, SessionId = session.Id, SessionTitle = session.Title, Directory = session.Location.Directory };
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_opening, opening)) _opening = null;
        }
    }

    public async Task CreateSessionAsync(CancellationToken cancellationToken, ModelRef? model = null, AgentId? agent = null)
    {
        lock (_gate)
            if (_opening is not null) throw new InvalidOperationException("Another view is still opening.");
        await PrepareAsync(cancellationToken);
        var input = _create() with { Id = Schema.SessionId.Create(), Model = model, Agent = agent?.Value };
        var session = (await RequestAsync(token => _client.CreateAsync(input, token), "session creation", cancellationToken)).Data;
        lock (_gate)
        {
            _sessionId = session.Id;
            _viewVersion++;
            _unconfirmedCreation = null;
            CurrentSession = session;
            _confirmedMutation = $"Session creation ({session.Id.Value})";
            ReconciledMessages = [];
            ReconciledInbox = [];
            LastPromptId = null;
            _needsReconciliation = true;
            PublishForms();
        }
        await ObserveSessionAsync(session.Id, cancellationToken);
    }

    public async Task SwitchModelAsync(ModelRef model, CancellationToken cancellationToken)
    {
        lock (_gate) if (_submitting != 0 || _opening is not null) throw new InvalidOperationException("Wait for the current session operation before changing models.");
        if (SessionId is not { } id) { await CreateSessionAsync(cancellationToken, model: model); return; }
        await RequestAsync(async token => { await _client.SwitchModelAsync(id, model, token); return true; }, "model selection", cancellationToken);
        lock (_gate)
        {
            _confirmedMutation = "Model selection";
            _needsReconciliation = true;
            if (CurrentSession?.Id == id) CurrentSession = CurrentSession with { Model = model };
        }
        try { CurrentSession = (await RequestAsync(token => _client.GetAsync(id, token), "selected model lookup", cancellationToken)).Data; }
        catch (Exception exception) { throw MutationRefreshFailure("Model selection", exception); }
    }

    public async Task SwitchAgentAsync(AgentId agent, CancellationToken cancellationToken)
    {
        lock (_gate) if (_submitting != 0 || _opening is not null) throw new InvalidOperationException("Wait for the current session operation before changing agents.");
        if (SessionId is not { } id) { await CreateSessionAsync(cancellationToken, agent: agent); return; }
        await RequestAsync(async token => { await _client.SwitchAgentAsync(id, agent, token); return true; }, "agent selection", cancellationToken);
        lock (_gate)
        {
            _confirmedMutation = "Agent selection";
            _needsReconciliation = true;
            if (CurrentSession?.Id == id) CurrentSession = CurrentSession with { Agent = agent.Value };
        }
        try { CurrentSession = (await RequestAsync(token => _client.GetAsync(id, token), "selected agent lookup", cancellationToken)).Data; }
        catch (Exception exception) { throw MutationRefreshFailure("Agent selection", exception); }
    }

    private static InvalidOperationException MutationRefreshFailure(string operation, Exception exception) =>
        new($"{operation} succeeded on the server, but refreshing the session failed: {Describe(exception)} Reload to reconcile; the server change was not rolled back.", exception);

    public async Task ReplyPermissionAsync(SessionId session, PermissionId request, PermissionReply reply, string? feedback, CancellationToken cancellationToken)
    {
        lock (_gate)
            if (!IsPermissionInCurrentFamily(session)) throw new InvalidOperationException("The permission belongs to another session family.");
        if (reply == PermissionReply.Always)
        {
            lock (_gate)
            {
                if (!_persistentPermissionGrants) throw new NotSupportedException("Durable permission persistence has not been confirmed by the server.");
                if (!_permissions.TryGetValue(request, out var pending) || pending.Save is not { Count: > 0 })
                    throw new InvalidOperationException("The pending permission has no save patterns.");
            }
        }
        try
        {
            await RequestAsync(async token =>
            {
                await _client.ReplyPermissionAsync(session, request, reply, message: feedback, ct: token);
                return true;
            }, "permission reply", cancellationToken);
            lock (_gate)
            {
                _permissionVersions[request] = ++_permissionVersion;
                _permissions.Remove(request);
                _tabPermissionOwners.Remove(request);
                _permissionSnapshot = _permissions.Values.ToArray();
                UpdateTabAttention();
                if (_observed.TryGetValue(session, out var entry)) PublishObservation(entry);
                foreach (var pending in _pendingRequests.Values) pending.Publish();
            }
        }
        catch (SessionApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            await RefreshPermissionsAsync(session, cancellationToken);
            throw;
        }
    }

    public Task RefreshCurrentPermissionsAsync(CancellationToken cancellationToken) => SessionId is { } id
        ? RefreshPermissionsAsync(id, cancellationToken) : Task.CompletedTask;

    private async Task RefreshPermissionCapabilitiesAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _persistentPermissionGrants = false;
        try
        {
            var capabilities = await RequestAsync(token => _client.PermissionCapabilitiesAsync(token), "permission capabilities", cancellationToken);
            lock (_gate) _persistentPermissionGrants = capabilities.Data.PersistentGrants;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
        catch (Exception) { /* Older/unavailable servers must not expose a persistent approval action. */ }
    }

    private async Task RefreshPermissionsAsync(SessionId session, CancellationToken cancellationToken)
    {
        long version;
        lock (_gate) version = _permissionVersion;
        var requests = (await RequestAsync(token => _client.ListSessionPermissionsAsync(session, token), "permission hydration", cancellationToken)).Data;
        lock (_gate)
        {
            var ids = requests.Select(request => request.Id).ToHashSet();
            foreach (var id in _permissions.Where(pair => pair.Value.SessionId == session && !ids.Contains(pair.Key)
                && _permissionVersions.GetValueOrDefault(pair.Key) <= version).Select(pair => pair.Key).ToArray())
            {
                _permissions.Remove(id);
                _tabPermissionOwners.Remove(id);
            }
            foreach (var request in requests)
                if (_permissionVersions.GetValueOrDefault(request.Id) <= version)
                {
                    _permissions[request.Id] = request;
                    _tabPermissionOwners[request.Id] = request.SessionId;
                }
            _permissionSnapshot = _permissions.Values.ToArray();
        }
    }

    private void ClearPermissions()
    {
        _permissions.Clear();
        _permissionVersions.Clear();
        _permissionSnapshot = [];
        _permissionVersion++;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var epoch = Feed.Epoch;
            await PumpConnectionAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (Feed.Epoch != epoch) failures = 0;
            var delay = Math.Min(250 * (1 << Math.Min(failures++, 5)), 5000);
            Task retry;
            lock (_gate)
            {
                retry = _retryConnection.Task;
                _feed = _feed with { Attempt = failures, RetryAt = _clock.GetUtcNow().AddMilliseconds(delay) };
            }
            using var pause = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try { await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(delay), _clock, pause.Token), retry).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            finally { await pause.CancelAsync(); }
            lock (_gate)
            {
                if (ReferenceEquals(_retryConnection.Task, retry)) _retryConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _feed = _feed with { RetryAt = null };
            }
        }
    }

    private async Task PumpConnectionAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        using var connection = _clock.CreateLinkedCancellationTokenSource(cancellationToken);
        connection.CancelAfter(_timeouts.Handshake);
        try
        {
            await foreach (var item in _client.SubscribeEventsAsync(connection.Token))
            {
                ObserveManagementEvent(item);
                if (item.Type == "server.connected")
                {
                    lock (_gate)
                    {
                        connection.CancelAfter(Timeout.InfiniteTimeSpan);
                        _connectionFailure = null;
                        _feed = new(SessionFeedPhase.Live, _feed.Epoch + 1);
                        _connected.TrySetResult();
                        foreach (var entry in _observed.Values.Where(entry => !entry.Deleted))
                        {
                            entry.Epoch = _feed.Epoch;
                            entry.Version++;
                            entry.AwaitingBaseline = true; entry.Synchronization = SessionSynchronization.Hydrating;
                            entry.NeedsReconciliation = true; entry.FormsHydrated = false;
                            entry.Buffered.Clear(); entry.MessageVersions.Clear(); entry.InboxChanges.Clear();
                            entry.Error = "Rehydrating the Session after connection.";
                            PublishObservation(entry);
                            if (entry.Creation is null) _ = RefreshLocked(entry);
                        }
                    }
                    continue;
                }
                lock (_gate)
                {
                    if (ApplyFormEvent(item))
                    {
                        NotifyPendingStateChanged();
                        continue;
                    }
                    try { ObserveEvent(item); }
                    catch (Exception error) when (error is JsonException or KeyNotFoundException or ArgumentException or OverflowException)
                    {
                        if (item.Data.TryGetProperty("sessionID", out var invalid) && invalid.ValueKind == JsonValueKind.String
                            && _observed.TryGetValue(OpenCode.Schema.SessionId.FromExisting(invalid.GetString()!), out var entry))
                        { entry.Error = Describe(error); PublishObservation(entry); _ = RefreshLocked(entry); }
                    }
                    if (item.Data.TryGetProperty("sessionID", out var activityId) && activityId.ValueKind == JsonValueKind.String)
                    {
                        var id = OpenCode.Schema.SessionId.FromExisting(activityId.GetString()!);
                        if (!_observed.ContainsKey(id))
                        {
                            var previous = _tabActivity.GetValueOrDefault(id) ?? new SessionTabActivity();
                            if (item.Type == "session.execution.started") _tabActivity = _tabActivity.SetItem(id, previous with { Busy = true });
                            if (IsTerminal(item.Type)) _tabActivity = _tabActivity.SetItem(id, previous with { Busy = false });
                            if (item.Type == "session.renamed" && item.Data.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                                _tabActivity = _tabActivity.SetItem(id, previous with { Title = title.GetString() });
                        }
                    }
                    if (item.Type == "permission.asked")
                    {
                        var request = item.Data.Deserialize(PermissionProtocolJsonContext.Default.PermissionRequest)
                            ?? throw new JsonException("Permission request payload is missing.");
                        if (_opening is { } opening && opening.Id == request.SessionId)
                            opening.Permissions[request.Id] = (++opening.Version, request);
                        _permissionVersions[request.Id] = ++_permissionVersion;
                        _permissions[request.Id] = request;
                        _permissionSnapshot = _permissions.Values.ToArray();
                        foreach (var waiting in _pendingRequests.Values.Where(waiting => waiting.SessionId == request.SessionId || FormDescendants(waiting.SessionId).Contains(request.SessionId)))
                        {
                            waiting.Publish();
                        }
                        if (_observed.TryGetValue(request.SessionId, out var entry)) PublishObservation(entry);
                        continue;
                    }
                    if (item.Type == "permission.replied"
                        && item.Data.TryGetProperty("sessionID", out var permissionSession) && permissionSession.ValueKind == JsonValueKind.String
                        )
                    {
                        if (!item.Data.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String || reply.GetString() is not ("once" or "always" or "reject"))
                            throw new JsonException("Permission reply event requires a canonical reply value.");
                        var id = PermissionId.FromExisting(item.Data.GetProperty("requestID").GetString() ?? throw new JsonException("Permission reply requires requestID."));
                        if (_opening is { } opening && permissionSession.GetString() == opening.Id.Value)
                            opening.Permissions[id] = (++opening.Version, null);
                        _permissionVersions[id] = ++_permissionVersion;
                        _permissions.Remove(id);
                        _permissionSnapshot = _permissions.Values.ToArray();
                        foreach (var resumed in _pendingRequests.Values) resumed.Publish();
                        if (_observed.TryGetValue(OpenCode.Schema.SessionId.FromExisting(permissionSession.GetString()!), out var entry)) PublishObservation(entry);
                        continue;
                    }
                    if (!item.Data.TryGetProperty("sessionID", out var session) || session.ValueKind != JsonValueKind.String) continue;
                    if (_observed.TryGetValue(OpenCode.Schema.SessionId.FromExisting(session.GetString()!), out var synchronizing) && synchronizing.Synchronization != SessionSynchronization.Live) continue;
                    foreach (var pending in _pendingRequests.Values.Where(pending => pending.SessionId.Value == session.GetString()).ToArray())
                    {
                        if (!pending.Seen.Add(item.Id)) continue;
                        try { Apply(pending, item); }
                        catch (JsonException error) { pending.ObservationEnded = true; pending.Updates.Writer.TryComplete(error); }
                        if (item.Type == "session.deleted") { pending.ObservationEnded = true; pending.Updates.Writer.TryComplete(); }
                    }
                }
            }
            failure = new IOException("The event feed disconnected. Reconnecting and rehydrating retained Sessions.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) when (connection.IsCancellationRequested)
        { failure = new TimeoutException("The event-feed connection handshake timed out."); }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (_gate)
            {
                _connectionFailure = failure;
                _feed = new(_lifetime.IsCancellationRequested ? SessionFeedPhase.Disposed : SessionFeedPhase.Reconnecting, _feed.Epoch,
                    failure is null ? null : Describe(failure));
                _persistentPermissionGrants = false;
                if (failure is not null) { _formsError = $"Pending forms may be stale: {Describe(failure)}"; PublishForms(); }
                if (failure is not null) { _connected.TrySetException(failure); _ = _connected.Task.Exception; }
                else _connected.TrySetCanceled(cancellationToken);
                if (!cancellationToken.IsCancellationRequested) _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                foreach (var entry in _observed.Values)
                {
                    entry.Invalidated = true;
                    entry.AwaitingBaseline = true; entry.NeedsReconciliation = true;
                    if (!entry.Deleted) entry.Synchronization = SessionSynchronization.Stale;
                    foreach (var admission in entry.Admissions.Values.Where(item => !item.Admitted && item.Phase == SessionAdmissionPhase.Sending))
                    { admission.Unconfirmed = true; admission.Phase = SessionAdmissionPhase.Unconfirmed; admission.Error = "The feed was lost while admission was outstanding; its item ID is retained."; }
                    if (failure is not null) entry.Error = Describe(failure);
                    PublishObservation(entry);
                    if (cancellationToken.IsCancellationRequested) foreach (var subscriber in entry.Subscribers) subscriber.Writer.TryComplete();
                }
                foreach (var pending in _pendingRequests.Values)
                {
                    if (cancellationToken.IsCancellationRequested) pending.Updates.Writer.TryComplete();
                    else pending.Publish();
                }
            }
        }
    }

    private static void Apply(Pending pending, ServerEventEnvelope item, bool replay = false, bool publish = true)
    {
        if (pending.Outcome is not null || pending.ObservationEnded) return;
        if (!replay && !pending.DeliveryObserved && (item.Type.StartsWith("session.step.", StringComparison.Ordinal)
            || item.Type.StartsWith("session.text.", StringComparison.Ordinal) || item.Type.StartsWith("session.reasoning.", StringComparison.Ordinal)
            || item.Type.StartsWith("session.tool.", StringComparison.Ordinal) || item.Type.StartsWith("session.execution.", StringComparison.Ordinal)))
        {
            if (pending.Deferred.Count >= 4096) throw new InvalidOperationException("Too many session events arrived without delivery confirmation. Reload to reconcile.");
            pending.Deferred.Add(item);
            if (pending.Delivered && item.Data.TryGetProperty("assistantMessageID", out var assistant)
                && assistant.ValueKind == JsonValueKind.String)
            {
                if (!pending.ReconciledAssistants.Contains(MessageId.FromExisting(assistant.GetString()!))) return;
                pending.DeferredApplied.Add(item.Id);
            }
        }
        switch (item.Type)
        {
            case "session.inbox.enqueued":
                if (Decode(item, OpenCodeJsonContext.Default.SessionInboxEnqueuedEventData).InboxId == pending.PromptId)
                {
                    pending.Admitted = true;
                    pending.EnqueuedSequence = Sequence(pending, item);
                    pending.BusyStartSequence = pending.Deferred.Where(value => value.Type == "session.execution.started")
                        .Select(value => Sequence(pending, value)).Where(value => value > pending.EnqueuedSequence).LastOrDefault();
                    pending.Status = "Queued";
                }
                break;
            case "session.inbox.delivered":
                if (Decode(item, OpenCodeJsonContext.Default.SessionInboxDeliveredEventData).InboxId == pending.PromptId)
                {
                    pending.Admitted = pending.Delivered = true;
                    pending.DeliveryObserved = true;
                    pending.DeliveredAt = Timestamp(item);
                    pending.Status = "Running";
                    // Ordered events before an observed delivery belong to earlier input.
                    // A read-model-confirmed missing delivery takes the replay path below.
                    pending.Deferred.Clear();
                    pending.DeferredApplied.Clear();
                }
                break;
            case "session.execution.started":
                if (Sequence(pending, item) is { } start && pending.EnqueuedSequence is { } enqueued && start > enqueued)
                    pending.BusyStartSequence = start;
                break;
            case "session.step.started" when pending.Delivered:
                var step = Decode(item, OpenCodeJsonContext.Default.SessionStepStartedEventData);
                var liveStep = GetStep(pending, step.AssistantMessageId);
                liveStep.Started = step;
                liveStep.Created = Timestamp(item);
                break;
            case "session.step.streamed" when pending.Delivered:
                var streamed = GetStep(pending, Decode(item, OpenCodeJsonContext.Default.SessionStepStreamedEventData).AssistantMessageId);
                streamed.Streamed = true;
                streamed.StreamedAt = Timestamp(item);
                break;
            case "session.step.ended" when pending.Delivered:
                var stepEnd = Decode(item, OpenCodeJsonContext.Default.SessionStepEndedEventData);
                var endedStep = GetStep(pending, stepEnd.AssistantMessageId);
                endedStep.Ended = stepEnd;
                endedStep.Completed = Timestamp(item);
                break;
            case "session.text.started" when pending.Delivered:
                var started = Decode(item, OpenCodeJsonContext.Default.SessionTextStartedEventData);
                pending.Fragments.TryAdd((started.AssistantMessageId, "text", started.Ordinal), new(pending.PartOrder++) { Created = Timestamp(item) });
                break;
            case "session.reasoning.started" when pending.Delivered:
                var reasoning = Decode(item, OpenCodeJsonContext.Default.SessionReasoningStartedEventData);
                pending.Fragments.TryAdd((reasoning.AssistantMessageId, "reasoning", reasoning.Ordinal), new(pending.PartOrder++) { State = reasoning.State, Created = Timestamp(item) });
                break;
            case "session.text.delta" or "session.reasoning.delta" when pending.Delivered:
                var delta = Decode(item, OpenCodeJsonContext.Default.SessionContentDeltaEventData);
                var key = (delta.AssistantMessageId, item.Type == "session.text.delta" ? "text" : "reasoning", delta.Ordinal);
                if (!pending.Fragments.TryGetValue(key, out var fragment)) pending.Fragments.Add(key, fragment = new(pending.PartOrder++));
                if (!fragment.Ended) fragment.Text.Append(delta.Delta);
                break;
            case "session.text.ended" or "session.reasoning.ended" when pending.Delivered:
                var ended = Decode(item, OpenCodeJsonContext.Default.SessionContentEndedEventData);
                var endedKey = (ended.AssistantMessageId, item.Type == "session.text.ended" ? "text" : "reasoning", ended.Ordinal);
                if (!pending.Fragments.TryGetValue(endedKey, out var completed)) pending.Fragments.Add(endedKey, completed = new(pending.PartOrder++));
                completed.Text.Clear().Append(ended.Text);
                completed.Ended = true;
                completed.Completed = Timestamp(item);
                completed.State = ended.State ?? completed.State;
                break;
            case "session.step.failed" when pending.Delivered:
                var failed = Decode(item, OpenCodeJsonContext.Default.SessionStepFailedEventData);
                var failedStep = GetStep(pending, failed.AssistantMessageId);
                failedStep.Failed = failed;
                failedStep.Completed = Timestamp(item);
                pending.Error = failed.Error;
                break;
            case "session.tool.input.started" when pending.Delivered:
                var toolStart = Decode(item, OpenCodeJsonContext.Default.SessionToolInputStartedEventData);
                pending.Tools.TryAdd((toolStart.AssistantMessageId, toolStart.Id), new(toolStart.Name, pending.PartOrder++) { Created = Timestamp(item) });
                break;
            case "session.tool.input.delta" when pending.Delivered:
                var toolDelta = Decode(item, OpenCodeJsonContext.Default.SessionToolInputDeltaEventData);
                var writingTool = GetTool(pending, toolDelta.AssistantMessageId, toolDelta.Id);
                if (!writingTool.InputEnded && writingTool.Completed is null) writingTool.Input.Append(toolDelta.Delta);
                break;
            case "session.tool.input.ended" when pending.Delivered:
                var toolEnd = Decode(item, OpenCodeJsonContext.Default.SessionToolInputEndedEventData);
                var endedTool = GetTool(pending, toolEnd.AssistantMessageId, toolEnd.Id);
                if (endedTool.Status == "streaming") endedTool.Input.Clear().Append(toolEnd.Text);
                endedTool.InputEnded = true;
                break;
            case "session.tool.called" when pending.Delivered:
                var called = Decode(item, OpenCodeJsonContext.Default.SessionToolCalledEventData);
                var running = GetTool(pending, called.AssistantMessageId, called.Id);
                if (running.Completed is not null) break;
                running.Status = "running";
                running.InputEnded = true;
                running.Arguments = called.Input;
                running.Metadata = ImmutableDictionary<string, JsonElement>.Empty;
                running.Ran = Timestamp(item);
                running.Executed = called.Executed;
                running.ProviderState = called.State;
                running.Input.Clear().Append(item.Data.GetProperty("input").GetRawText());
                break;
            case "session.tool.progress" when pending.Delivered:
                var progress = Decode(item, OpenCodeJsonContext.Default.SessionToolProgressEventData);
                var progressing = GetTool(pending, progress.AssistantMessageId, progress.Id);
                if (progressing.Status == "running" && progressing.Completed is null) progressing.Metadata = progress.Metadata;
                break;
            case "session.tool.success" when pending.Delivered:
                var success = Decode(item, OpenCodeJsonContext.Default.SessionToolSuccessEventData);
                var succeeded = GetTool(pending, success.AssistantMessageId, success.Id);
                if (succeeded.Status != "running" || succeeded.Completed is not null) break;
                succeeded.Status = "completed";
                succeeded.InputEnded = true;
                succeeded.Executed = success.Executed || succeeded.Executed == true;
                succeeded.ProviderResultState = success.ResultState;
                succeeded.Output = success.Content;
                succeeded.Metadata = success.Metadata;
                succeeded.Completed = Timestamp(item);
                break;
            case "session.tool.failed" when pending.Delivered:
                var toolFailure = Decode(item, OpenCodeJsonContext.Default.SessionToolFailedEventData);
                var failedTool = GetTool(pending, toolFailure.AssistantMessageId, toolFailure.Id);
                if (failedTool.Status is not ("streaming" or "running") || failedTool.Completed is not null) break;
                if (failedTool.Status == "streaming") failedTool.Arguments = ImmutableDictionary<string, JsonElement>.Empty;
                failedTool.Status = "error";
                failedTool.InputEnded = true;
                failedTool.Executed = toolFailure.Executed || failedTool.Executed == true;
                failedTool.ProviderResultState = toolFailure.ResultState;
                failedTool.Error = toolFailure.Error;
                failedTool.Output = toolFailure.Content;
                failedTool.Metadata = toolFailure.Metadata;
                failedTool.Completed = Timestamp(item);
                break;
            case "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted" when pending.Delivered && (pending.DeliveryObserved || replay):
                Complete(pending, item);
                return;
            case "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted":
                // A previous busy period can finish while this input is still queued.
                // Session-wide completion is not proof that this prompt was delivered.
                pending.Unsupported = item;
                pending.ReconcileRequested = true;
                break;
            case "session.text.started" or "session.reasoning.started" or "session.step.streamed" or "session.step.ended":
                break;
            default:
                pending.Unsupported = item;
                break;
        }
        if (publish) pending.Publish();
    }

    private void ReconcilePending(Pending pending, IReadOnlyList<SessionInboxItem> inbox, IReadOnlyList<SessionMessage> messages)
    {
        lock (_gate)
        {
            if (pending.Outcome is not null || pending.ObservationEnded) return;
            var inputIndex = messages.ToList().FindIndex(message => message.Id == pending.PromptId && message is UserMessage);
            var parked = inbox.Any(item => item.Id == pending.PromptId && item.Payload is UserInboxPayload);
            pending.Admitted |= parked || inputIndex >= 0;
            var deferred = pending.Deferred.ToArray();
            if (inputIndex >= 0)
            {
                pending.Delivered = true;
                pending.DeliveredAt = messages[inputIndex].Time.Created;
                var following = messages.Skip(inputIndex + 1).TakeWhile(message => message is not (UserMessage or SyntheticMessage))
                    .OfType<AssistantMessage>().ToArray();
                var terminalBoundary = deferred.FirstOrDefault(item => IsTerminal(item.Type) && pending.BusyStartSequence is not null
                    && Sequence(pending, item) > pending.BusyStartSequence);
                if (terminalBoundary is not null)
                {
                    var later = deferred.Where(item => item.Type == "session.step.started" && Sequence(pending, item) > Sequence(pending, terminalBoundary))
                        .Select(item => Decode(item, OpenCodeJsonContext.Default.SessionStepStartedEventData).AssistantMessageId).ToHashSet();
                    following = following.TakeWhile(message => !later.Contains(message.Id)
                        && (Timestamp(terminalBoundary) is not { } boundaryTime || message.Time.Created <= boundaryTime)).ToArray();
                }
                var assistants = following.Select(message => message.Id).ToHashSet();
                pending.ReconciledAssistants.UnionWith(assistants);
                var owned = deferred.Where(item => item.Data.TryGetProperty("assistantMessageID", out var id)
                    && id.ValueKind == JsonValueKind.String && assistants.Contains(MessageId.FromExisting(id.GetString()!))).ToArray();
                // Seed identities from actual projected tools, not invented tool-start events.
                foreach (var message in following)
                    foreach (var tool in message.Content.OfType<AssistantToolContent>())
                        pending.Tools.TryAdd((message.Id, tool.Id), new(tool.Name, pending.PartOrder++));
                foreach (var item in owned)
                    if (!pending.DeferredApplied.Remove(item.Id)) Apply(pending, item, replay: true, publish: false);
                MergeMessages(pending, following);
                // Correlate a terminal with an observed start/assistant fact from this
                // aggregate, never with a timestamp or an earlier busy period alone.
                var boundary = owned.Select(item => Sequence(pending, item)).Where(value => value is not null).Min()
                    ?? pending.BusyStartSequence;
                var terminal = deferred.FirstOrDefault(item => IsTerminal(item.Type) && boundary is not null && Sequence(pending, item) > boundary);
                if (terminal is not null) Complete(pending, terminal);
                else if (following.Length > 0 && following.All(message => message.Time.Completed is not null) && deferred.Any(item => IsTerminal(item.Type)))
                {
                    ObservationFor(pending.SessionId).NeedsReconciliation = true;
                    pending.ObservationEnded = true;
                    pending.Status = "Completion unconfirmed";
                    pending.Publish();
                    pending.Updates.Writer.TryComplete(new InvalidOperationException("Completed assistant messages were recovered, but the execution outcome could not be correlated. Reload to confirm; no new input was submitted."));
                }
                var processed = owned.Select(item => item.Id).ToHashSet();
                var previous = messages.Take(inputIndex).OfType<AssistantMessage>().Select(message => message.Id).ToHashSet();
                pending.Deferred.RemoveAll(item => processed.Contains(item.Id)
                    || item.Data.TryGetProperty("assistantMessageID", out var id) && id.ValueKind == JsonValueKind.String
                        && previous.Contains(MessageId.FromExisting(id.GetString()!))
                    || IsTerminal(item.Type) && boundary is not null && Sequence(pending, item) <= boundary);
            }
            else if (parked && pending.BusyStartSequence is { } start)
            {
                var terminal = deferred.FirstOrDefault(item => IsTerminal(item.Type) && Sequence(pending, item) > start);
                if (terminal is not null)
                {
                    ObservationFor(pending.SessionId).NeedsReconciliation = true;
                    if (terminal.Type != "session.execution.succeeded") Complete(pending, terminal);
                    else
                    {
                        pending.ObservationEnded = true;
                        pending.Updates.Writer.TryComplete(new InvalidOperationException("The execution ended without delivering this input. It remains queued; reload to inspect it."));
                    }
                }
            }
            pending.Publish();
        }
    }

    private static bool IsTerminal(string type) => type is "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted";

    private static void MergeMessages(Pending pending, IReadOnlyList<AssistantMessage> messages)
    {
        foreach (var message in messages)
        {
            var step = GetStep(pending, message.Id);
            step.ReadModel = message;
            step.Created = message.Time.Created;
            step.Completed = message.Time.Completed ?? step.Completed;
            step.StreamedAt = message.Time.Streamed ?? step.StreamedAt;
            step.Streamed |= message.Time.Streamed is not null;
            pending.Error ??= message.Error;
            var textOrdinal = 0;
            var reasoningOrdinal = 0;
            foreach (var content in message.Content)
            {
                if (content is AssistantTextContent text)
                    MergeText("text", textOrdinal++, text.Text, text.State, null, message.Time.Completed);
                if (content is AssistantReasoningContent reasoning)
                    MergeText("reasoning", reasoningOrdinal++, reasoning.Text, reasoning.State, reasoning.Time?.Created, reasoning.Time?.Completed ?? message.Time.Completed);
                if (content is not AssistantToolContent source) continue;
                var tool = GetTool(pending, message.Id, source.Id);
                if (tool.Completed is not null && source.Time.Completed is null) continue;
                tool.Created = source.Time.Created;
                tool.Completed = source.Time.Completed;
                tool.Ran = source.Time.Ran ?? tool.Ran;
                tool.Executed = source.Executed ?? tool.Executed;
                tool.ProviderState = source.ProviderState ?? tool.ProviderState;
                tool.ProviderResultState = source.ProviderResultState ?? tool.ProviderResultState;
                tool.InputEnded |= source.State is not ToolStateStreaming;
                if (tool.Completed is null && tool.Input.Length > 0) continue;
                switch (source.State)
                {
                    case ToolStateStreaming streaming:
                        tool.Status = "streaming";
                        tool.Input.Clear().Append(streaming.Input);
                        break;
                    case ToolStateRunning running:
                        tool.Status = "running";
                        tool.Arguments = running.Input;
                        tool.Metadata = running.Metadata;
                        tool.Input.Clear().Append(JsonSerializer.Serialize(running.Input));
                        break;
                    case ToolStateCompleted completed:
                        tool.Status = "completed";
                        tool.Arguments = completed.Input;
                        tool.Metadata = completed.Metadata;
                        tool.Output = completed.Content;
                        tool.Input.Clear().Append(JsonSerializer.Serialize(completed.Input));
                        break;
                    case ToolStateError error:
                        tool.Status = "error";
                        tool.Arguments = error.Input;
                        tool.Metadata = error.Metadata;
                        tool.Output = error.Content;
                        tool.Error = error.Error;
                        tool.Input.Clear().Append(JsonSerializer.Serialize(error.Input));
                        break;
                }
            }

            void MergeText(string kind, double ordinal, string text, IReadOnlyDictionary<string, JsonElement>? state, DateTimeOffset? created, DateTimeOffset? completed)
            {
                var key = (message.Id, kind, ordinal);
                if (!pending.Fragments.TryGetValue(key, out var fragment)) pending.Fragments.Add(key, fragment = new(pending.PartOrder++));
                if (completed is not null || fragment.Text.Length == 0) fragment.Text.Clear().Append(text);
                fragment.State = state ?? fragment.State;
                fragment.Created ??= created;
                fragment.Completed = completed ?? fragment.Completed;
                fragment.Ended |= completed is not null;
            }
        }
        var order = messages.Select(message => message.Id).Concat(pending.StepOrder.OrderBy(pair => pair.Value).Select(pair => pair.Key)).Distinct().ToArray();
        pending.StepOrder.Clear();
        foreach (var id in order) pending.StepOrder.Add(id, pending.StepOrder.Count);
    }

    private static double? Sequence(Pending pending, ServerEventEnvelope item)
    {
        if (item.Durable is null) return null;
        if (item.Durable.AggregateId != pending.SessionId.Value) throw new JsonException("Session event aggregate does not match its session.");
        return item.Durable.Seq;
    }

    private static void Complete(Pending pending, ServerEventEnvelope item)
    {
        pending.Status = item.Type["session.execution.".Length..];
        if (item.Type == "session.execution.interrupted")
        {
            if (!item.Data.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
                || reason.GetString() is not ("user" or "shutdown" or "superseded")) throw new JsonException("Execution interruption requires a supported reason.");
            pending.InterruptionReason = reason.GetString();
        }
        if (item.Type == "session.execution.failed")
        {
            if (!item.Data.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) throw new JsonException("Execution failure requires a structured error.");
            pending.Error ??= error.Deserialize(OpenCodeJsonContext.Default.SessionStructuredError) ?? throw new JsonException("Execution failure requires a structured error.");
        }
        pending.Outcome = item.Type switch
        {
            "session.execution.succeeded" => SessionOutcome.Succeeded,
            "session.execution.failed" => SessionOutcome.Failed,
            _ => SessionOutcome.Interrupted
        };
        pending.Publish();
        pending.Updates.Writer.TryComplete();
    }

    private static T Decode<T>(ServerEventEnvelope item, JsonTypeInfo<T> metadata) where T : class =>
        item.Data.Deserialize(metadata) ?? throw new JsonException($"Event '{item.Type}' has no supported payload.");

    private static DateTimeOffset? Timestamp(ServerEventEnvelope item) => item.Created is { } created
        ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)created)) : null;

    private static Tool GetTool(Pending pending, MessageId message, string id) => pending.Tools.TryGetValue((message, id), out var tool)
        ? tool : throw new JsonException("Tool state arrived before its canonical input-start event.");

    private static Step GetStep(Pending pending, MessageId id)
    {
        if (pending.Steps.TryGetValue(id, out var step)) return step;
        pending.StepOrder.Add(id, pending.StepOrder.Count);
        pending.Steps.Add(id, step = new());
        return step;
    }

    private async Task<IReadOnlyList<SessionMessage>> ReadMessagesAsync(SessionId id, CancellationToken cancellationToken)
    {
        var messages = new List<SessionMessage>();
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await RequestAsync(token => _client.MessagesAsync(id,
                new SessionMessagesQuery(200, cursor is null ? SessionOrder.Ascending : null, cursor), token), "message reconciliation", cancellationToken);
            messages.AddRange(page.Data);
            cursor = page.Cursor.Next;
            if (page.Data.Count == 0 && cursor is not null) throw new InvalidOperationException("The server returned a continuation cursor for an empty message page.");
            if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("The server repeated a message cursor; reconciliation could not finish.");
        } while (cursor is not null);
        return messages;
    }

    private async Task<T> RequestAsync<T>(Func<CancellationToken, Task<T>> request, string operation, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _httpOperations.Add(completed.Task);
        }
        using var deadline = _clock.CreateLinkedCancellationTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(_timeouts.Request);
        try { return await request(deadline.Token); }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Server {operation} did not complete within {_timeouts.Request.TotalSeconds:g} seconds.", exception);
        }
        finally
        {
            lock (_gate) _httpOperations.Remove(completed.Task);
            completed.TrySetResult();
        }
    }

    internal static string Describe(Exception exception)
    {
        if (exception is SessionApiException api && api.Payload is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return payload.TryGetProperty("ref", out var reference) && reference.ValueKind == JsonValueKind.String
                ? $"{message.GetString()} Reference: {reference.GetString()}." : message.GetString()!;
        if (exception is ServiceLifecycleException lifecycle) return lifecycle.Message;
        return exception.Message;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task[] owned;
        lock (_gate) owned = _submissions.Values.Select(submission => submission.Finished.Task)
            .Concat(_observed.Values.Select(entry => (Task?)entry.Refresh).OfType<Task>()).Concat(_httpOperations).Append(_pump).ToArray();
        try
        {
            await _lifetime.CancelAsync();
            await Task.WhenAll(owned);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            try { _client.Dispose(); }
            finally { _subscription?.Dispose(); _connectionGate.Dispose(); _lifetime.Dispose(); }
        }
    }
}
