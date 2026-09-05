namespace OpenCode.Cli.Tui.Components;

using OpenCode.Cli.Tui.Attachments;
using OpenCode.Cli.Tui.Stash;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.TextMarks;

public partial class OpenCodeApp
{
    private PromptStashStore? _ownedPromptStash;
    private PromptStashStore _promptStash => _ownedPromptStash ??= new(clock: Clock);
    private StashSnapshot? _stashSnapshot;
    private bool _stashOpen;
    private bool _stashStopped;
    private string? _stashActionError;
    private string? _stashPersistenceException;
    private readonly Lock _stashUpdateGate = new();
    private Task _stashUpdates = Task.CompletedTask;
    private string? StashPersistenceError => _stashSnapshot?.Error ?? _stashPersistenceException;
    private bool StashReady => !_stashStopped && _stashSnapshot?.Loaded == true;

    private async Task LoadPromptStash()
    {
        _promptStash.Changed += StashChanged;
        try { await _promptStash.LoadAsync(CancellationToken.None); }
        catch (Exception exception) { _stashActionError = "Could not load the prompt stash: " + exception.Message; }
        _stashSnapshot = _promptStash.Snapshot;
        _dirty = true;
    }

    private PromptStashAccess StashAccess()
    {
        if (!StashReady) return new(false, null, "The prompt stash is not ready.");
        // Inspect the retained retry before CapturePromptAdmission: that method deliberately
        // clears an ID when edited text differs, which is not stash admission authority.
        if (_retryPromptInputs.TryGetValue(_tabs.Selected, out var retry))
            return new(false, retry.Id, "This draft retains a retry admission. Reconcile it before stashing or replacing it.");
        var origin = _originRequests.Values.FirstOrDefault(request => !request.Admitted
            && (request.Key == _tabs.Selected || _sessionId is { } session && request.Session == session));
        if (origin is not null) return new(false, origin.Input, "An input admission is still in flight.");
        if (_commandAdmissions.Contains(_tabs.Selected) || _shellPreparing.Contains(_tabs.Selected))
            return new(false, null, "A command or shell admission is still in flight.");
        if (_commandErrors.ContainsKey(_tabs.Selected) || _shellErrors.ContainsKey(_tabs.Selected))
            return new(false, null, "Resolve the previous command or shell outcome before stashing or replacing this draft.");
        if (_configurationBusy || PromptBlocked || _terminalFocused || _terminalListOpen || _activitiesOpen)
            return new(false, null, "Return to an editable prompt before stashing or replacing it.");
        var input = CapturePromptAdmission(_input);
        if (input.Id is { } bound) return new(false, bound, "A bound input cannot become a new stashed request.");
        if (_sessionId is not { } id) return new(true, null);
        var observation = ReadSessionObservation?.Invoke(id);
        var availability = ReadAdmissionAvailability?.Invoke(id, input);
        if (availability?.Unconfirmed.FirstOrDefault() is { Value: not null } uncertain)
            return new(false, uncertain, "Reconcile the unconfirmed Session input before changing the stash draft.");
        if (observation is null || observation.Synchronization != SessionSynchronization.Live || observation.Error is not null || availability?.Allowed != true)
            return new(false, null, observation?.Error ?? availability?.Reason ?? "Session admission state is unavailable.");
        var admission = observation.Admissions.FirstOrDefault(item => item.Input is { } captured && SessionClientAdapter.SamePrompt(captured, input)
            && (item.Unconfirmed || item.Phase is SessionAdmissionPhase.Waiting or SessionAdmissionPhase.Preparing or SessionAdmissionPhase.Sending or SessionAdmissionPhase.Pending));
        return admission is null ? new(true, null) : new(false, admission.Id, "This input is still bound to a pending admission.");
    }

    private bool PushPromptStash()
    {
        try
        {
            var access = StashAccess();
            access.RequireEditable();
            if (_input.Length == 0) return false;
            var edit = CurrentEdit;
            if (!_editDocuments.TryGetValue(edit, out var document)) throw new InvalidOperationException("The complete editor snapshot is unavailable.");
            // Current paste is literal text or typed file data. The root creates no collapsed
            // pasted-text descriptors; do not invent them from text resembling a placeholder.
            var mutation = _promptStash.Push(StashPrompt.Capture(document, Array.Empty<StashPastedText>()), access);
            if (mutation.Entry is null) throw new InvalidOperationException("The stash did not accept the prompt in memory.");
            _input = _draft = "";
            ClearPromptAttachments(_tabs.Selected);
            RememberPromptMetadata(_tabs.Selected, null);
            _cursor = 0;
            _selectionAnchor = null;
            _highSurrogate = null;
            _preferredColumn = null;
            _historyIndex = _history.Count;
            _historyDrafts.Remove(_tabs.Selected);
            _editHistory.Clear();
            _draftRevision++;
            _stashActionError = _inputError = null;
            _keyTasks.Add(ObserveStashWrite(mutation.Persistence));
            CloseDialog();
        }
        catch (Exception exception) { _stashActionError = exception.Message; _dirty = true; }
        return true;
    }

    private bool PopStash()
    {
        try
        {
            if (_promptStash.Snapshot.Entries.LastOrDefault() is not { } entry) return false;
            var access = CanRestoreStash(entry);
            access.RequireEditable();
            // Restore is synchronous and fully prepared before consumption; no awaited write
            // may replace text typed after this action.
            RestoreStashedPrompt(entry);
            var entries = _promptStash.Snapshot.Entries;
            var mutation = _promptStash.Take(entries.Count - 1, entry, access);
            _keyTasks.Add(ObserveStashWrite(mutation.Persistence));
            CloseDialog();
        }
        catch (Exception exception) { _stashActionError = exception.Message; _dirty = true; }
        return true;
    }

    private bool OpenStash()
    {
        if (!StashReady || _promptStash.Snapshot.Entries.Count == 0) return false;
        CloseDialog();
        _stashOpen = true;
        _dirty = true;
        StateHasChanged();
        return true;
    }

    private PromptStashAccess CanRestoreStash(StashEntry entry)
    {
        var access = StashAccess();
        if (!access.Allowed || access.AdmissionId is not null) return access;
        try { PrepareStashRestore(entry); return access; }
        catch (Exception exception) { return new(false, null, exception.Message); }
    }

    private (PromptEditDocument Document, AttachmentTextMarks Marks) PrepareStashRestore(StashEntry entry)
    {
        var data = entry.Prompt.RestoreData();
        if (data.Pasted.Count > 0)
            throw new NotSupportedException("This stash contains tracked pasted-text descriptors that this editor cannot reconstruct. The entry was kept.");
        var document = data.Document;
        var state = document.Marks is { } snapshot ? AttachmentTextMarks.Restore(document.Input, snapshot) : new AttachmentTextMarks(document.Input);
        if (document.Marks is { } original)
        {
            state.Reconcile(document.Input);
            var restored = state.Snapshot();
            if (!original.Bindings.SequenceEqual(restored.Bindings) || !original.Marks.Marks.SequenceEqual(restored.Marks.Marks)
                || original.Marks.NextId <= original.Marks.Marks.Select(mark => mark.Id).DefaultIfEmpty(0).Max())
                throw new InvalidOperationException("The stash has unsupported or inconsistent virtual-mark bindings. The entry was kept.");
        }
        if (state.Marks.All.Count > 0)
        {
            var map = new TerminalTextMap(document.Input.Text, MeasureMentionElement);
            if (state.Marks.All.Any(mark => mark.Id <= 0 || mark.Start < 0 || mark.End < mark.Start || mark.End > map.DisplayLength))
                throw new InvalidOperationException("The stash has invalid virtual-mark ranges. The entry was kept.");
            _ = state.Project(MeasureMentionElement, PromptMarkStyle);
        }
        return (document, state);
    }

    private void RestoreStashedPrompt(StashEntry entry)
    {
        StashAccess().RequireEditable();
        var restored = PrepareStashRestore(entry);
        // No await: admission validation, undo capture, and complete restore are one dispatcher action.
        _editHistory.Record(CurrentEdit);
        _input = restored.Document.Input.Text;
        _promptParts[_tabs.Selected] = restored.Document.Input;
        _promptMarkStates[_tabs.Selected] = restored.Marks;
        RememberPromptMetadata(_tabs.Selected, restored.Document.Metadata);
        _shellModes[_tabs.Selected] = restored.Document.ShellMode;
        _cursor = _input.Length;
        _selectionAnchor = null;
        _highSurrogate = null;
        _preferredColumn = null;
        _historyIndex = _history.Count;
        _draft = "";
        _historyDrafts.Remove(_tabs.Selected);
        _dismissedReferenceText = _dismissedCommandInput = _input;
        _draftRevision++;
        _stashActionError = _inputError = null;
        _dirty = true;
    }

    private async Task ObserveStashWrite(Task<StashWriteResult> persistence)
    {
        try { await persistence; }
        catch (Exception exception) { _stashPersistenceException = "The stash changed in memory, but persistence failed: " + exception.Message; }
        _stashSnapshot = _promptStash.Snapshot; // Latest revision, never an older write's apparent success.
        if (!_stashSnapshot.Dirty && _stashSnapshot.Error is null) _stashPersistenceException = null;
        _dirty = true;
    }

    private async Task RetryStashSave(TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (!StashReady) return;
        await ObserveStashWrite(_promptStash.RetrySaveAsync());
    }

    private void StashChanged()
    {
        lock (_stashUpdateGate)
        {
            if (_stashStopped) return;
            _stashUpdates = DispatchStashUpdate(_stashUpdates);
        }
    }

    private async Task DispatchStashUpdate(Task previous)
    {
        await previous.ConfigureAwait(false);
        await InvokeAsync(() =>
        {
            if (_stashStopped) return;
            _stashSnapshot = _promptStash.Snapshot;
            if (!_stashSnapshot.Dirty && _stashSnapshot.Error is null) _stashPersistenceException = null;
            _dirty = true;
        });
    }

    private void StopStashMutations()
    {
        lock (_stashUpdateGate) _stashStopped = true;
        _promptStash.Changed -= StashChanged;
    }
}
