namespace OpenCode.Cli.Tui.Components;

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenTui.Blazor;

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionId, SessionObservationSnapshot?>? ReadSessionObservation { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task>? InterruptObservedSession { get; set; }
    [Parameter] public Func<SessionId?, SessionPromptInput, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>? NetworkPromptInput { get; set; }
    [Parameter] public Func<SessionId?, SessionPromptInput?, SessionAdmissionAvailability>? ReadAdmissionAvailability { get; set; }
    private sealed class OriginRequest(Guid key, SessionId? session, CancellationTokenSource cancellation)
    {
        internal readonly Guid Key = key;
        internal readonly Guid RequestId = Guid.NewGuid();
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal SessionId? Session = session;
        internal MessageId? Input;
        internal bool Admitted;
        internal bool Recorded;
        internal string? Error;
        internal SessionResponseSnapshot? Last;
    }
    private readonly Dictionary<Guid, OriginRequest> _originRequests = [];
    private readonly Dictionary<Guid, Guid> _armedOrigins = [];
    private readonly List<Task> _originStreams = [];
    private (SessionId Id, long Revision)? _observedView;
    private (SessionId Id, string Error)? _observedError;
    private readonly Dictionary<Guid, SessionPromptInput> _retryPromptInputs = [];
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, JsonElement>> _promptMetadata = [];

    // Existing submit/keybindings still assign _request/_stream. Their backing ownership is now
    // per-origin; changing selection does not cancel work or make another tab look busy.
    private CancellationTokenSource? _request
    {
        get => _originRequests.Values.LastOrDefault(origin => !origin.Admitted
            && (origin.Key == _tabs.Selected || _sessionId is { } id && origin.Session == id))?.Cancellation;
        set
        {
            if (value is null) { _armedOrigins.Remove(_tabs.Selected); return; }
            var origin = new OriginRequest(_tabs.Selected, _sessionId, value);
            _originRequests.Add(origin.RequestId, origin);
            _armedOrigins[_tabs.Selected] = origin.RequestId;
        }
    }
    private Task _stream
    {
        get => Task.WhenAll(_originStreams);
        set { _originStreams.RemoveAll(task => task.IsCompletedSuccessfully); _originStreams.Add(value); }
    }
    private bool SelectedSessionRunning => _sessionId is { } id && ReadSessionObservation?.Invoke(id)?.Running == true || _request is not null;

    private bool OriginVisible(OriginRequest origin) => _tabs.Selected == origin.Key
        && (_sessionId == origin.Session || _sessionId is null && _tabs.Current.SessionId is null);

    private bool CanSubmitPrompt(SessionPromptInput input)
    {
        if (_configurationBusy || PromptBlocked) return false;
        var availability = ReadAdmissionAvailability?.Invoke(_sessionId, input);
        if (availability?.Allowed == true) return true;
        _inputError = availability?.Reason ?? "Session admission state is not connected.";
        _dirty = true;
        return false;
    }

    private SessionPromptInput CapturePromptAdmission(string text)
    {
        var prompt = CapturePromptInput(text);
        var previous = _retryPromptInputs.GetValueOrDefault(_tabs.Selected);
        var captured = SessionClientAdapter.SnapshotPrompt((previous ?? new SessionPromptInput(text)) with
        {
            Text = prompt.Text, Files = prompt.Files, Agents = prompt.Agents, Skills = prompt.Skills,
            Metadata = _promptMetadata.GetValueOrDefault(_tabs.Selected) ?? previous?.Metadata
        });
        return previous is not null && !SessionClientAdapter.SamePrompt(captured, previous) ? captured with { Id = null } : captured;
    }

    private void RememberPromptMetadata(Guid tab, IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null) _promptMetadata.Remove(tab);
        else _promptMetadata[tab] = metadata.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private Task StreamAsync(string prompt, CancellationToken ct) => StreamAsync(new SessionPromptInput(prompt), ct);
    private Task StreamAsync(PromptInput input, CancellationToken ct) => StreamAsync(new SessionPromptInput(input.Text,
        Files: input.Files, Agents: input.Agents, Skills: input.Skills), ct);

    private async Task StreamAsync(SessionPromptInput supplied, CancellationToken cancellationToken)
    {
        var input = SessionClientAdapter.SnapshotPrompt(supplied);
        var prompt = input.Text;
        var origin = _armedOrigins.Remove(_tabs.Selected, out var requestId) ? _originRequests.GetValueOrDefault(requestId) : null;
        origin = origin
            ?? throw new InvalidOperationException("Prompt submission has no originating tab.");
        _retryPromptInputs.Remove(origin.Key);
        _promptMetadata.Remove(origin.Key);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _configurationLifetime.Token);
        var ct = lifetime.Token;
        try
        {
            if (NetworkPromptInput is null && (NetworkPrompt is null || input.Id is not null || input.Files is not null
                || input.Agents is not null || input.Skills is not null || input.Metadata is not null || input.Delivery is not null || input.Resume is not null))
                throw new InvalidOperationException("Typed prompt admission is not connected. The complete input was retained; no text-only fallback was sent.");
            if (origin.Session is null)
            {
                var agent = _agentSelection;
                var configured = agent is { } selected ? _catalog?.Agents.FirstOrDefault(item => item.Id == selected)?.Model ?? _configuredAgentModel : null;
                var model = agent is { } id ? _agentModelChoices.GetValueOrDefault(id) ?? configured ?? _creationFallback : _unassignedModelChoice ?? _creationFallback;
                ConfigureNewSession?.Invoke(agent, model);
            }
            // Do not await a selected-view reload before capturing the adapter's origin. PromptAsync
            // owns origin-scoped readiness; the composer has already captured and cleared its text.
            ct.ThrowIfCancellationRequested();
            _responseState = null;
            _status = "Submitting to server...";
            _dirty = true;
            var updates = NetworkPromptInput is not null ? NetworkPromptInput(origin.Session, input, ct) : NetworkPrompt!(prompt, ct);
            await foreach (var update in updates.WithCancellation(ct))
            {
                var visible = OriginVisible(origin);
                origin.Session ??= update.SessionId;
                if (origin.Session != update.SessionId) throw new InvalidOperationException("Prompt observation changed its originating Session.");
                origin.Input = update.PromptId;
                origin.Last = update;
                origin.Admitted |= update.Admitted || update.Delivered;
                BindOriginSession(origin, update);
                if (!origin.Recorded)
                {
                    if (visible)
                    {
                        var browsing = _historyIndex < _history.Count;
                        _history.Add(prompt);
                        if (!browsing) _historyIndex = _history.Count;
                    }
                    else if (_tabViews.TryGetValue(origin.Key, out var view))
                        _tabViews[origin.Key] = view with { History = view.History.Add(prompt),
                            HistoryIndex = view.HistoryIndex < view.History.Length ? view.HistoryIndex : view.History.Length + 1 };
                    origin.Recorded = true;
                }
                if (!visible) { _dirty = true; continue; }
                _sessionId = update.SessionId;
                _responseState = update;
                _hasConversation |= update.Admitted || update.Delivered;
                if (ReadSessionObservation?.Invoke(update.SessionId) is null)
                {
                    _promptTexts[update.PromptId] = prompt;
                    var index = _responses.FindIndex(response => response.PromptId == update.PromptId);
                    if (index < 0) _responses.Add(update); else _responses[index] = update;
                    TranscriptRevision++;
                }
                if (update.SessionTitle is not null) _conversationTitle = update.SessionTitle;
                if (update.Error is not null && update.Steps.All(step => step.Failed is null && step.ReadModel?.Error is null)) ExecutionError = update.Error.Message;
                if (update.Steps.LastOrDefault()?.Started is { } step) ApplyObservedSelection(step.Agent, step.Model);
                _status = update.Status;
                ReadObservedSession();
                _dirty = true;
            }
            if (OriginVisible(origin))
            {
                _status = origin.Last?.Outcome switch
                { SessionOutcome.Failed => "Request failed", SessionOutcome.Interrupted => "Interrupted", SessionOutcome.Succeeded => "Ready", _ => "Observation complete" };
                ReadObservedSession(force: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (OriginVisible(origin)) _status = "Observation stopped";
        }
        catch (Exception error)
        {
            origin.Error = SessionClientAdapter.Describe(error);
            if (OriginVisible(origin)) { _status = "Request failed"; ExecutionError = origin.Error; }
        }
        finally
        {
            if (origin.Session is { } id && origin.Input is { } inputId && ReadSessionObservation?.Invoke(id) is { } observed)
                origin.Admitted |= observed.Inbox.Any(item => item.Id == inputId) || observed.Messages.Any(message => message.Id == inputId);
            if (!origin.Admitted && !_configurationLifetime.IsCancellationRequested)
            {
                // Source restoreEntry uses emptiness, not a draft revision. Apply it only to the
                // origin editor; a failed background request must never touch the selected draft.
                if (OriginVisible(origin) && _input.Length == 0)
                {
                    _editHistory.Record(CurrentEdit);
                    _input = prompt; _cursor = prompt.Length; _selectionAnchor = null;
                    _highSurrogate = null; _preferredColumn = null; _draftRevision++;
                    RestoreCompletePrompt(origin.Key, input with { Id = origin.Input ?? input.Id });
                }
                else if (_tabViews.TryGetValue(origin.Key, out var view) && view.Input.Length == 0)
                {
                    view.EditHistory.Record(new(view.Input, view.Cursor, view.SelectionAnchor));
                    _tabViews[origin.Key] = view with { Input = prompt, Cursor = prompt.Length, SelectionAnchor = null };
                    RestoreCompletePrompt(origin.Key, input with { Id = origin.Input ?? input.Id });
                }
            }
            _originRequests.Remove(origin.RequestId);
            origin.Cancellation.Dispose();
            _dirty = true;
        }
    }

    private void RestoreCompletePrompt(Guid origin, SessionPromptInput input)
    {
        _retryPromptInputs[origin] = input;
        RestorePromptAttachments(origin, new PromptInput(input.Text, input.Files, input.Agents, input.Skills));
        RememberPromptMetadata(origin, input.Metadata);
    }

    private void TrimPromptDocuments(IReadOnlySet<Guid> retained)
    {
        foreach (var key in _retryPromptInputs.Keys.Concat(_promptMetadata.Keys).Distinct().Where(key => !retained.Contains(key)).ToArray())
        {
            _retryPromptInputs.Remove(key);
            _promptMetadata.Remove(key);
            ClearPromptAttachments(key);
        }
    }

    private void BindOriginSession(OriginRequest origin, SessionResponseSnapshot update)
    {
        var tab = _tabs.Tabs.FirstOrDefault(tab => tab.Key == origin.Key);
        if (tab is null) return; // Closing a pending new tab never resurrects it on admission.
        var before = _tabs.Persisted;
        _tabs = _tabs with { Tabs = _tabs.Tabs.Replace(tab, tab with { SessionId = tab.SessionId ?? update.SessionId,
            Title = update.SessionTitle ?? tab.Title }) };
        QueueTabWrite(before, _tabs.Persisted);
    }

    private void ReadObservedSession(bool force = false)
    {
        if (_sessionId is not { } id || ReadSessionObservation?.Invoke(id) is not { } snapshot || snapshot.Deleted) return;
        if (!force && _observedView == (id, snapshot.Revision)) return;
        _observedView = (id, snapshot.Revision);
        if (snapshot.Synchronization != SessionSynchronization.Live)
        {
            _status = snapshot.Error ?? snapshot.Synchronization.ToString();
            if (snapshot.Error is not null) { ExecutionError = snapshot.Error; _observedError = (id, snapshot.Error); }
            _dirty = true;
            return; // Keep the last good transcript and every draft while the feed rehydrates.
        }
        _projectedHistory = snapshot.Messages;
        _responses.Clear();
        _promptTexts.Clear();
        if (snapshot.Session is { } session)
        {
            _hasConversation = true;
            _conversationTitle = session.Title ?? _conversationTitle;
            CurrentDirectory = session.Location.Directory;
            _childSession = session.ParentId is not null;
            if (session.Agent is { } agent && session.Model is { } model) ApplyObservedSelection(agent, model);
        }
        var error = snapshot.Error ?? snapshot.ExecutionError;
        if (error is not null) { ExecutionError = error; _observedError = (id, error); }
        else if (_observedError is { } previous && previous.Id == id && ExecutionError == previous.Error) { ExecutionError = null; _observedError = null; }
        _status = error is not null ? "Session requires attention" : snapshot.Running ? "Running"
            : snapshot.Inbox.Count > 0 ? "Queued" : "Ready";
        TranscriptRevision++;
        _dirty = true;
    }

    private void ApplyObservedSelection(string agent, ModelRef model)
    {
        ActiveAgent = _presentation?.Agents?.FirstOrDefault(item => item.Id.Value == agent)?.Name ?? agent;
        _agentSelection = AgentId.FromExisting(agent);
        if (_unassignedModelChoice is { } choice && choice == model) { _agentModelChoices[_agentSelection.Value] = choice; _unassignedModelChoice = null; }
        ActiveModel = _presentation?.Models.FirstOrDefault(item => item.ProviderId.Value == model.ProviderId && item.Id.Value == model.Id)?.Name ?? model.Id;
        _modelSelection = model;
        ActiveProvider = _presentation?.Providers?.FirstOrDefault(item => item.Id.Value == model.ProviderId)?.Name ?? model.ProviderId;
        ActiveVariant = model.Variant;
    }

    private Task InterruptActiveSession() => _sessionId is { } id && InterruptObservedSession is not null
        ? InterruptObservedSession(id, _configurationLifetime.Token)
        : Task.FromException(new InvalidOperationException("Session interruption is not connected to the server."));
}
