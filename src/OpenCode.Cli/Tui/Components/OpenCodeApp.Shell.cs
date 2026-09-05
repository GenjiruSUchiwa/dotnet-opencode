namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenCode.Protocol.Groups;
using OpenTui.Blazor.Keymap;
using OpenCode.Cli.Tui.Attachments;

public sealed record SessionShellSubmission(SessionId? Session, LocationRef Location, string Command, AgentId? Agent, ModelRef? Model);

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionShellSubmission, Func<SessionInfo, Task>, CancellationToken, Task>? RunSessionShell { get; set; }
    private readonly Dictionary<Guid, bool> _shellModes = [];
    private readonly Dictionary<Guid, string> _shellErrors = [];
    private readonly HashSet<Guid> _shellPreparing = [];
    private Guid? _shellView;
    private bool ShellMode => _shellModes.GetValueOrDefault(EditorKey);

    private bool EnterShellMode(string text)
    {
        if (ShellMode || PromptBlocked || text != "!" || _cursor != 0 || CommandAutocompleteVisible || ReferenceAutocompleteVisible) return false;
        _shellModes[EditorKey] = true;
        _dirty = true;
        return true;
    }

    private bool HandleShellControl(KeymapEvent input)
    {
        if (!_focusedPrompt || !ShellMode || input.Type != KeyEventType.Press) return false;
        var exit = input.Stroke == new KeyStroke("escape") || _cursor == 0 && input.Stroke == new KeyStroke("backspace")
            || _input.Length == 0 && input.Stroke == new KeyStroke("c", ctrl: true);
        if (!exit) return false;
        _shellModes[EditorKey] = false;
        _dirty = true;
        return true;
    }

    private bool SubmitShell(InboxDeliveryMode? delivery)
    {
        if (delivery == InboxDeliveryMode.Queue) { _inputError = "Shell commands cannot be queued."; _dirty = true; return true; }
        if (_configurationBusy) { _inputError = "Wait for the configuration change; the shell draft was kept."; _dirty = true; return true; }
        if (string.IsNullOrWhiteSpace(_input)) return true;
        if (RunSessionShell is null) { _inputError = "Session shell execution is not connected."; _dirty = true; return true; }
        if (_retryPromptInputs.ContainsKey(EditorKey))
        { _inputError = "This draft retains a prompt admission. Reconcile it before running it as a shell command."; _dirty = true; return true; }
        if (_shellPreparing.Contains(EditorKey)) { _inputError = "The shell Session is still being prepared; the draft was kept."; _dirty = true; return true; }
        if (_sessionId is { } id && ReadSessionObservation?.Invoke(id) is { Error: not null } observed)
        { _inputError = observed.Error; _dirty = true; return true; }
        var model = CurrentModelSelection;
        if (_sessionId is null && (_agentSelection is null || model is null))
        { _inputError = "Select an agent and model before creating the shell Session."; _dirty = true; return true; }
        if (_sessionId is null && model is not null && ((_presentation?.Models ?? _catalog?.Models ?? []).FirstOrDefault(item =>
            item.ProviderId.Value == model.ProviderId && item.Id.Value == model.Id)?.Enabled != true
            || (_presentation?.Providers ?? _catalog?.Providers ?? []).FirstOrDefault(provider => provider.Id.Value == model.ProviderId)?.Activation == ProviderActivation.Disabled))
        { _inputError = "The selected model is unavailable for the new Session; the shell draft was kept."; _dirty = true; return true; }
        var tab = _tabs.Selected;
        var origin = EditorKey;
        var captured = CapturePromptAdmission(_input);
        var entry = new PromptEditDocument(new(captured.Text, captured.Files, captured.Agents, captured.Skills), captured.Metadata, GetPromptMarks().Snapshot(), ShellMode: true);
        var submission = new SessionShellSubmission(_sessionId, SelectionLocation, _input, _agentSelection, model);
        _historyDocuments[(origin, _history.Count)] = entry;
        _history.Add(_input);
        _historyIndex = _history.Count;
        _input = _draft = "";
        _cursor = 0;
        _selectionAnchor = null;
        _preferredColumn = null;
        _highSurrogate = null;
        ClearPromptAttachments(origin);
        RememberPromptMetadata(origin, null);
        _editHistory.Clear();
        _draftRevision++;
        _shellModes[origin] = false;
        _shellErrors.Remove(origin);
        _inputError = null;
        _shellPreparing.Add(origin);
        _keyTasks.Add(SendShell(tab, origin, submission, entry));
        _dirty = true;
        return true;
    }

    private async Task SendShell(Guid tab, Guid origin, SessionShellSubmission submission, PromptEditDocument entry)
    {
        try
        {
            await RunSessionShell!(submission, async session =>
            {
                await BindCommandSession(tab, origin, submission.Session, session);
                _shellPreparing.Remove(origin);
            }, _configurationLifetime.Token);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // A shell POST is a side effect, not an idempotent prompt retry. Never resubmit it here.
            var error = "Shell request failed or its outcome is unknown; it was not retried. Check activity before running again. " + SessionClientAdapter.Describe(exception);
            _shellErrors[origin] = error;
            if (EditorKey == origin)
            {
                _inputError = error;
                if (_input.Length == 0)
                {
                    _input = entry.Input.Text;
                    RestoreEditDocument(entry);
                    _cursor = _input.Length;
                    _selectionAnchor = null;
                    _shellModes[origin] = true;
                    _draftRevision++;
                }
            }
            else if (RetainsEditor(origin) && _tabViews.TryGetValue(origin, out var view) && view.Input.Length == 0)
            {
                _tabViews[origin] = view with { Input = entry.Input.Text, Cursor = entry.Input.Text.Length, SelectionAnchor = null, InputError = error };
                RestorePromptAttachments(origin, entry.Input);
                RememberPromptMetadata(origin, entry.Metadata);
                if (entry.Marks is { } marks) _promptMarkStates[origin] = AttachmentTextMarks.Restore(entry.Input, marks);
                _shellModes[origin] = true;
            }
        }
        finally { _shellPreparing.Remove(origin); _dirty = true; }
    }

    private void ReadShellMode()
    {
        if (_shellView == EditorKey) return;
        _shellView = EditorKey;
        if (_shellErrors.TryGetValue(EditorKey, out var error)) _inputError = error;
        _dirty = true;
    }
}
