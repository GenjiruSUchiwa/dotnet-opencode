namespace OpenCode.Cli.Tui.Components;

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenCode.Cli.Tui.Attachments;

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionId, SessionObservationSnapshot?>? ReadSessionObservation { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task>? InterruptObservedSession { get; set; }
    [Parameter] public Func<SessionId?, SessionPromptInput, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>? NetworkPromptInput { get; set; }
    [Parameter] public Func<SessionId?, SessionPromptInput, PromptSelection, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>? NetworkSelectedPrompt { get; set; }
    [Parameter] public Func<SessionId?, SessionPromptInput?, SessionAdmissionAvailability>? ReadAdmissionAvailability { get; set; }
    private sealed class OriginRequest(Guid tab, Guid key, SessionId? session, CancellationTokenSource cancellation)
    {
        internal readonly Guid Tab = tab;
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
            && (origin.Key == EditorKey || _sessionId is { } id && origin.Session == id))?.Cancellation;
        set
        {
            if (value is null) { _armedOrigins.Remove(EditorKey); return; }
            var origin = new OriginRequest(_tabs.Selected, EditorKey, _sessionId, value);
            _originRequests.Add(origin.RequestId, origin);
            _armedOrigins[EditorKey] = origin.RequestId;
        }
    }
    private Task _stream
    {
        get => Task.WhenAll(_originStreams);
        set { _originStreams.RemoveAll(task => task.IsCompletedSuccessfully); _originStreams.Add(value); }
    }
    private bool SelectedSessionRunning => _sessionId is { } id && ReadSessionObservation?.Invoke(id)?.Running == true || _request is not null;

    private bool OriginVisible(OriginRequest origin) => EditorKey == origin.Key
        && (_sessionId == origin.Session || _sessionId is null && _tabs.Current.SessionId is null);

    private bool CanSubmitPrompt(SessionPromptInput input)
    {
        if (_configurationBusy || PromptBlocked) return false;
        if (_retryPromptInputs.TryGetValue(EditorKey, out var retry) && !SessionClientAdapter.SamePrompt(retry, input)
            && RetryIsUncertain(retry))
        { _inputError = "The original admission is unconfirmed. Reconcile it or retry its complete captured input; edits were kept."; _dirty = true; return false; }
        var availability = ReadAdmissionAvailability?.Invoke(_sessionId, input);
        if (availability?.Allowed == true) return true;
        _inputError = availability?.Reason ?? "Session admission state is not connected.";
        _dirty = true;
        return false;
    }

    private SessionPromptInput CapturePromptAdmission(string text)
    {
        var prompt = CapturePromptInput(text);
        var previous = _retryPromptInputs.GetValueOrDefault(EditorKey);
        var captured = SessionClientAdapter.SnapshotPrompt((previous ?? new SessionPromptInput(text)) with
        {
            Text = prompt.Text, Files = prompt.Files, Agents = prompt.Agents, Skills = prompt.Skills,
            Metadata = _promptMetadata.GetValueOrDefault(EditorKey) ?? previous?.Metadata
        });
        var changedSelection = _retryPromptSelections.TryGetValue(EditorKey, out var selection)
            && selection != new PromptSelection(SelectionLocation, _agentSelection, CurrentModelSelection);
        return previous is not null && !RetryIsUncertain(previous) && (!SessionClientAdapter.SamePrompt(captured, previous) || changedSelection)
            ? captured with { Id = null } : captured;
    }

    private bool RetryIsUncertain(SessionPromptInput input) => _sessionId is not { } id || input.Id is not { } item
        || ReadSessionObservation?.Invoke(id)?.Admissions.FirstOrDefault(admission => admission.Id == item) is not
            { Unconfirmed: false, Phase: SessionAdmissionPhase.Rejected or SessionAdmissionPhase.Cancelled };

    private void RememberPromptMetadata(Guid tab, IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null) _promptMetadata.Remove(tab);
        else _promptMetadata[tab] = metadata.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private Task StreamAsync(string prompt, CancellationToken ct) => StreamAsync(new SessionPromptInput(prompt), ct);
    private Task StreamAsync(PromptInput input, CancellationToken ct) => StreamAsync(new SessionPromptInput(input.Text,
        Files: input.Files, Agents: input.Agents, Skills: input.Skills), ct);

    private async Task StreamAsync(SessionPromptInput supplied, CancellationToken cancellationToken, PromptSelection? selection = null, PromptEditDocument? document = null)
    {
        var input = SessionClientAdapter.SnapshotPrompt(supplied);
        var prompt = input.Text;
        var origin = _armedOrigins.Remove(EditorKey, out var requestId) ? _originRequests.GetValueOrDefault(requestId) : null;
        origin = origin
            ?? throw new InvalidOperationException("Prompt submission has no originating tab.");
        _retryPromptInputs.Remove(origin.Key);
        _retryPromptSelections.Remove(origin.Key);
        _promptMetadata.Remove(origin.Key);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _configurationLifetime.Token);
        var ct = lifetime.Token;
        try
        {
            if (NetworkSelectedPrompt is null && NetworkPromptInput is null && (NetworkPrompt is null || input.Id is not null || input.Files is not null
                || input.Agents is not null || input.Skills is not null || input.Metadata is not null || input.Delivery is not null || input.Resume is not null))
                throw new InvalidOperationException("Typed prompt admission is not connected. The complete input was retained; no text-only fallback was sent.");
            if (origin.Session is null && selection is not null && NetworkSelectedPrompt is null)
                ConfigureNewSession?.Invoke(selection.Agent, selection.Model);
            // Do not await a selected-view reload before capturing the adapter's origin. PromptAsync
            // owns origin-scoped readiness; the composer has already captured and cleared its text.
            ct.ThrowIfCancellationRequested();
            _responseState = null;
            _status = "Submitting to server...";
            _dirty = true;
            var updates = NetworkSelectedPrompt is not null && selection is not null ? NetworkSelectedPrompt(origin.Session, input, selection, ct)
                : NetworkPromptInput is not null ? NetworkPromptInput(origin.Session, input, ct) : NetworkPrompt!(prompt, ct);
            await foreach (var update in updates.WithCancellation(ct))
            {
                var visible = OriginVisible(origin);
                if (origin.Session is null) AdoptEditorSession(origin.Tab, origin.Key, update.SessionId, selection);
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
                    else if (RetainsEditor(origin.Key) && _tabViews.TryGetValue(origin.Key, out var view))
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
                    RestoreCompletePrompt(origin.Key, input with { Id = origin.Input ?? input.Id }, selection, document?.Marks, document?.ShellMode == true);
                }
                else if (!OriginVisible(origin) && RetainsEditor(origin.Key) && _tabViews.TryGetValue(origin.Key, out var view) && view.Input.Length == 0)
                {
                    view.EditHistory.Record(new(view.Input, view.Cursor, view.SelectionAnchor));
                    _tabViews[origin.Key] = view with { Input = prompt, Cursor = prompt.Length, SelectionAnchor = null, InputError = origin.Error };
                    RestoreCompletePrompt(origin.Key, input with { Id = origin.Input ?? input.Id }, selection, document?.Marks, document?.ShellMode == true);
                }
            }
            _originRequests.Remove(origin.RequestId);
            origin.Cancellation.Dispose();
            _dirty = true;
        }
    }

    private void RestoreCompletePrompt(Guid origin, SessionPromptInput input, PromptSelection? selection, AttachmentMarksSnapshot? marks, bool shellMode)
    {
        _retryPromptInputs[origin] = input;
        _shellModes[origin] = shellMode;
        RestorePromptAttachments(origin, new PromptInput(input.Text, input.Files, input.Agents, input.Skills));
        RememberPromptMetadata(origin, input.Metadata);
        if (selection is not null) _retryPromptSelections[origin] = selection;
        if (marks is not null) _promptMarkStates[origin] = AttachmentTextMarks.Restore(new(input.Text, input.Files, input.Agents, input.Skills), marks);
    }

    private void TrimPromptDocuments(IReadOnlySet<Guid> retained)
    {
        foreach (var key in _retryPromptInputs.Keys.Concat(_promptMetadata.Keys).Concat(_promptParts.Keys).Concat(_promptMarkStates.Keys).Concat(_shellModes.Keys)
            .Concat(_commandErrors.Keys).Concat(_shellErrors.Keys).Concat(_historyDrafts.Keys).Concat(_retryPromptSelections.Keys).Distinct().Where(key => !retained.Contains(key)).ToArray())
        {
            _retryPromptInputs.Remove(key);
            _promptMetadata.Remove(key);
            ClearPromptAttachments(key);
            _promptMarkStates.Remove(key);
            _retryPromptSelections.Remove(key);
            _shellModes.Remove(key); _shellErrors.Remove(key); _commandErrors.Remove(key); _historyDrafts.Remove(key);
        }
        foreach (var key in _historyDocuments.Keys.Where(key => !retained.Contains(key.Tab)).ToArray()) _historyDocuments.Remove(key);
    }

    private void BindOriginSession(OriginRequest origin, SessionResponseSnapshot update)
    {
        var tab = _tabs.Tabs.FirstOrDefault(tab => tab.Key == origin.Tab);
        if (tab is null) return; // Closing a pending new tab never resurrects it on admission.
        if (tab.SessionId is not null && tab.SessionId != origin.Session) return;
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
            RefreshDraftSelection();
        }
        var error = snapshot.Error ?? snapshot.ExecutionError;
        if (error is not null) { ExecutionError = error; _observedError = (id, error); }
        else if (_observedError is { } previous && previous.Id == id && ExecutionError == previous.Error) { ExecutionError = null; _observedError = null; }
        _status = error is not null ? "Session requires attention" : snapshot.Running ? "Running"
            : snapshot.Inbox.Count > 0 ? "Queued" : "Ready";
        TranscriptRevision++;
        _dirty = true;
    }

    private Task InterruptActiveSession() => _sessionId is { } id && InterruptObservedSession is not null
        ? InterruptObservedSession(id, _configurationLifetime.Token)
        : Task.FromException(new InvalidOperationException("Session interruption is not connected to the server."));
}
