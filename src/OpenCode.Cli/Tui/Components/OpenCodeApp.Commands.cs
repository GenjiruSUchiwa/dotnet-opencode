namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Commands;
using OpenCode.Cli.Tui.Keymap;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Schema;
using OpenTui.Blazor.Keymap;
using System.Text;

public partial class OpenCodeApp
{
    [Parameter] public Func<ComposerAnchor?>? ReadComposerAnchor { get; set; }
    [Parameter] public Func<CommandSubmission, Func<SessionInfo, Task>, CancellationToken, Task<SessionInfo>>? AdmitCommand { get; set; }
    private readonly HashSet<Guid> _commandAdmissions = [];
    private readonly Dictionary<Guid, string> _commandErrors = [];
    private Guid? _commandView;
    private ComposerAnchor? _commandAnchor;
    private IReadOnlyList<SlashCommand> _commandOptions = [];
    private string? _commandQuery;
    private string? _dismissedCommandInput;
    private int _commandIndex;
    private static readonly SlashCommand[] LocalSlashCommands = [
        new("sessions", null, "session.list", ["resume", "continue"]),
        new("new", null, "session.new", ["clear"]),
        new("models", null, "model.list", ["mo"]),
        new("agents", null, "agent.list"), new("variants", null, "variant.list"),
        new("settings", null, "opencode.settings"), new("status", null, "opencode.status"),
        new("themes", null, "theme.switch"), new("connect", null, "provider.connect"),
        new("mcps", null, "mcp.list"), new("terminal", null, "session.terminal"),
        new("exit", null, "app.exit", ["quit", "q"])
    ];
    private bool CommandAutocompleteVisible => _commandQuery is not null && _commandAnchor is not null && !PromptBlocked && !PromptOverlayOpen
        && !ShellMode && !_activitiesOpen && !_terminalListOpen && !_terminalFocused;

    private void UpdateCommandAutocomplete()
    {
        if (_commandView != _tabs.Selected)
        {
            _commandView = _tabs.Selected;
            if (_commandErrors.TryGetValue(_tabs.Selected, out var error)) { _inputError = error; _dirty = true; }
        }
        var query = ShellMode || _dismissedCommandInput == _input ? null : SlashCompletion.Query(_input, _cursor);
        var anchor = query is null ? null : ReadComposerAnchor?.Invoke();
        if (_commandQuery != query || _commandAnchor != anchor) _dirty = true;
        if (_commandQuery != query) _commandIndex = 0;
        _commandQuery = query;
        _commandAnchor = anchor;
        if (query is null) { _commandOptions = []; return; }
        var context = _lastKeyContext with
        {
            Data = new Dictionary<string, object?>(_lastKeyContext.Data) { [TuiKeymapLayer.ModeKey] = TuiKeymapLayer.BaseMode }
        };
        var commands = _keyLayers.ReachableCommands(context).ToDictionary(command => command.Name);
        _commandOptions = SlashCompletion.Filter(LocalSlashCommands.Where(item => commands.ContainsKey(item.ClientCommand!))
            .Select(item => item with { Description = commands[item.ClientCommand!].Description ?? commands[item.ClientCommand!].Title })
            .Concat((_presentation?.Commands ?? []).Select(command => new SlashCommand(command.Name, command.Description)))
            .Concat((ActiveSkills?.SlashCommands((_presentation?.Commands ?? []).Select(command => command.Name).ToHashSet(StringComparer.Ordinal)) ?? [])
                .Select(skill => new SlashCommand(skill.Selection.Skill.Id.Value, skill.Description, Skill: skill.Selection))), query);
        _commandIndex = Math.Clamp(_commandIndex, 0, Math.Max(0, _commandOptions.Count - 1));
    }

    private bool HandleCommandAutocomplete(ConsoleKeyInfo key)
    {
        UpdateCommandAutocomplete();
        if (!CommandAutocompleteVisible || !_focusedPrompt || _keyHint is not null) return false;
        var name = KeyName(key);
        if (name is null || _resolvedBindings is null) return false;
        var stroke = new KeyStroke(name, key.Modifiers.HasFlag(ConsoleModifiers.Control),
            key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt));
        var command = new[] { "prompt.autocomplete.prev", "prompt.autocomplete.next", "prompt.autocomplete.hide",
            "prompt.autocomplete.select", "prompt.autocomplete.complete", "prompt.clear" }
            .FirstOrDefault(id => _resolvedBindings.Get(id).Any(binding => binding.Sequence.Count == 1 && binding.Sequence[0].Stroke == stroke));
        if (command is "prompt.autocomplete.hide" or "prompt.clear")
        {
            if (command == "prompt.clear")
            {
                if (!ReplacePromptRange(0, _cursor, "")) return true;
            }
            _dismissedCommandInput = _input;
            _commandQuery = null;
            _dirty = true;
            return true;
        }
        if (command is "prompt.autocomplete.prev" or "prompt.autocomplete.next")
        {
            if (_commandOptions.Count > 0)
                HighlightCommand((_commandIndex + (command == "prompt.autocomplete.prev" ? -1 : 1) + _commandOptions.Count) % _commandOptions.Count);
            return true;
        }
        if (command is not ("prompt.autocomplete.select" or "prompt.autocomplete.complete")) return false;
        ChooseCommand(_commandIndex);
        return true;
    }

    private void HighlightCommand(int index) { _commandIndex = index; _dirty = true; }

    private bool HandleRichAutocomplete(KeymapEvent input)
    {
        UpdateReferenceAutocomplete();
        UpdateCommandAutocomplete();
        if (!_focusedPrompt || _keyDispatcher?.Pending.Count > 0 || _resolvedBindings is null) return false;
        var reference = ReferenceAutocompleteVisible;
        if (!reference && !CommandAutocompleteVisible) return false;
        var fallback = input.BaseCode is >= 32 and not 127 && Rune.IsValid(input.BaseCode.Value)
            ? new KeyStroke(new Rune(input.BaseCode.Value).ToString(), input.Stroke.Ctrl, input.Stroke.Shift, input.Stroke.Meta, input.Stroke.Super, input.Stroke.Hyper) : null;
        var command = new[] { "prompt.autocomplete.prev", "prompt.autocomplete.next", "prompt.autocomplete.hide",
            "prompt.autocomplete.select", "prompt.autocomplete.complete", "prompt.clear" }.FirstOrDefault(id =>
                _resolvedBindings.Get(id).Any(binding => binding.Event == input.Type && binding.Sequence.Count == 1
                    && (binding.Sequence[0].Stroke == input.Stroke || fallback is not null && binding.Sequence[0].Stroke == fallback)));
        if (command is null) return false;
        if (command is "prompt.autocomplete.hide" or "prompt.clear")
        {
            if (!reference && command == "prompt.clear" && !ReplacePromptRange(0, _cursor, "")) return true;
            if (reference) { _dismissedReferenceText = _input; _referenceQuery = null; }
            else { _dismissedCommandInput = _input; _commandQuery = null; }
            _dirty = true;
            return true;
        }
        if (command is "prompt.autocomplete.prev" or "prompt.autocomplete.next")
        {
            var count = reference ? _referenceOptions.Count : _commandOptions.Count;
            if (count == 0) return true;
            var selected = reference ? _referenceIndex : _commandIndex;
            var next = (selected + (command == "prompt.autocomplete.prev" ? -1 : 1) + count) % count;
            if (reference) HighlightReference(next); else HighlightCommand(next);
            return true;
        }
        if (reference) SelectReference(_referenceIndex, command == "prompt.autocomplete.complete");
        else ChooseCommand(_commandIndex);
        return true;
    }

    private void ChooseCommand(int index)
    {
        if (_commandOptions.ElementAtOrDefault(index) is not { } command || PromptBlocked) return;
        if (command.Skill is { } skill)
        {
            AttachSkill(skill, "/" + command.Name, 0, _cursor);
            _commandQuery = null;
            return;
        }
        if (!ReplacePromptRange(0, _cursor, command.ClientCommand is null ? $"/{command.Name} " : "")) return;
        _dismissedCommandInput = _input;
        _commandQuery = null;
        _dirty = true;
        if (command.ClientCommand is not { } id) return;
        var context = _lastKeyContext with
        {
            Data = new Dictionary<string, object?>(_lastKeyContext.Data) { [TuiKeymapLayer.ModeKey] = TuiKeymapLayer.BaseMode }
        };
        if (_keyDispatcher?.DispatchCommand(id, _keyLayers, context).Handled != true)
            _inputError = $"Command '{id}' is unavailable in the current context.";
    }

    private bool SubmitSlashCommand(InboxDeliveryMode delivery = InboxDeliveryMode.Steer)
    {
        var slash = SlashHead.Parse(_input);
        if (slash is null) return false;
        var local = LocalSlashCommands.FirstOrDefault(command => command.Name == slash.Name || command.Aliases?.Contains(slash.Name) == true);
        if (local?.ClientCommand is { } id && string.IsNullOrWhiteSpace(slash.Arguments))
        {
            if (delivery == InboxDeliveryMode.Queue)
            { _inputError = "This command cannot be queued."; _dirty = true; return true; }
            var context = _lastKeyContext with
            {
                Data = new Dictionary<string, object?>(_lastKeyContext.Data) { [TuiKeymapLayer.ModeKey] = TuiKeymapLayer.BaseMode }
            };
            if (!_keyLayers.ReachableCommands(context).Any(command => command.Name == id))
            { _inputError = $"Command '/{slash.Name}' is unavailable."; _dirty = true; return true; }
            // Clear this origin synchronously before a local command can navigate to another tab.
            if (!ReplacePromptRange(0, _input.Length, "")) return true;
            ClearPromptAttachments(_tabs.Selected);
            RememberPromptMetadata(_tabs.Selected, null);
            if (_keyDispatcher?.DispatchCommand(id, _keyLayers, context).Handled != true)
            { _inputError = $"Command '/{slash.Name}' could not run."; _dirty = true; }
            return true;
        }
        if (_configurationBusy)
        {
            _inputError = "Wait for the current configuration change; the command draft was kept.";
            _dirty = true;
            return true;
        }
        if (_commandAdmissions.Contains(_tabs.Selected))
        {
            _inputError = "A command admission is already in progress for this tab; the draft was kept.";
            _dirty = true;
            return true;
        }
        if (_presentation?.CommandError is not null)
        {
            _inputError = _presentation.CommandError;
            _dirty = true;
            return true;
        }
        // Upstream submits unknown slash-prefixed text as an ordinary prompt.
        if (!(_presentation?.Commands ?? []).Any(command => command.Name == slash.Name)) return false;
        if (AdmitCommand is null)
        {
            _inputError = "Command admission is not connected to the session observer.";
            _dirty = true;
            return true;
        }
        if (_retryPromptInputs.ContainsKey(_tabs.Selected))
        {
            _inputError = "This draft retains a prompt admission. Reconcile it instead of submitting it as a new command.";
            _dirty = true;
            return true;
        }
        var availability = ReadAdmissionAvailability?.Invoke(_sessionId, CapturePromptAdmission(_input));
        if (availability?.Allowed != true)
        {
            _inputError = availability?.Reason ?? "Session admission state is not connected.";
            _dirty = true;
            return true;
        }
        var model = _modelSelection ?? (_agentSelection is { } agent ? _agentModelChoices.GetValueOrDefault(agent) : _unassignedModelChoice)
            ?? _configuredAgentModel ?? _creationFallback;
        if (_sessionId is null && (_agentSelection is null || model is null))
        {
            _inputError = "Select an available agent and model before starting a command session.";
            _dirty = true;
            return true;
        }
        if (model is not null && ((_presentation?.Models ?? _catalog?.Models ?? []).FirstOrDefault(item =>
                item.ProviderId.Value == model.ProviderId && item.Id.Value == model.Id)?.Enabled != true
            || (_presentation?.Providers ?? _catalog?.Providers ?? []).FirstOrDefault(provider => provider.Id.Value == model.ProviderId)?.Activation == ProviderActivation.Disabled))
        {
            _inputError = "The selected model is unavailable in this location. The command draft was kept.";
            _dirty = true;
            return true;
        }
        var tab = _tabs.Selected;
        _commandErrors.Remove(tab);
        var entry = new PromptEditDocument(CapturePromptInput(_input), _promptMetadata.GetValueOrDefault(tab), GetPromptMarks().Snapshot(), ShellMode);
        RememberPromptHistory(CapturePromptAdmission(_input));
        var submission = new CommandSubmission(_sessionId, _presentation?.Location ?? new LocationRef(CurrentDirectory),
            slash.Name, entry.Input with { Text = slash.Arguments }, _agentSelection, model, delivery);
        _history.Add(entry.Input.Text);
        _historyIndex = _history.Count;
        _input = _draft = "";
        _cursor = 0;
        _selectionAnchor = null;
        _preferredColumn = null;
        _editHistory.Clear();
        ClearPromptAttachments(tab);
        RememberPromptMetadata(tab, null);
        _highSurrogate = null;
        _draftRevision++;
        _commandQuery = null;
        _commandAdmissions.Add(tab);
        _keyTasks.Add(AdmitSlashCommand(tab, entry, submission));
        _dirty = true;
        return true;
    }

    private async Task AdmitSlashCommand(Guid origin, PromptEditDocument entry, CommandSubmission submission)
    {
        var target = submission.Session;
        try
        {
            await AdmitCommand!(submission, session =>
            {
                target = session.Id;
                return BindCommandSession(origin, submission.Session, session);
            }, _configurationLifetime.Token);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var error = $"Command failed or its outcome is unknown; it was not retried. {SessionClientAdapter.Describe(exception)}";
            _commandErrors[origin] = error;
            var visible = _tabs.Selected == origin && (_sessionId == target || submission.Session is null && _sessionId is null);
            if (visible) _inputError = error;
            if (visible && _input.Length == 0)
            {
                _input = entry.Input.Text;
                _cursor = _input.Length;
                _selectionAnchor = null;
                RestoreEditDocument(entry);
                _shellModes[origin] = entry.ShellMode;
                _draftRevision++;
            }
            else if (_tabs.Selected != origin && _tabs.Tabs.Any(tab => tab.Key == origin && tab.SessionId == target)
                && _tabViews.TryGetValue(origin, out var view) && view.Input.Length == 0)
            {
                _tabViews[origin] = view with { Input = entry.Input.Text, Cursor = entry.Input.Text.Length, SelectionAnchor = null };
                RestorePromptAttachments(origin, entry.Input);
                RememberPromptMetadata(origin, entry.Metadata);
                if (entry.Marks is { } marks) _promptMarkStates[origin] = AttachmentTextMarks.Restore(entry.Input, marks);
                _shellModes[origin] = entry.ShellMode;
            }
        }
        finally { _commandAdmissions.Remove(origin); _dirty = true; }
    }

    private async Task BindCommandSession(Guid origin, SessionId? previous, SessionInfo session)
    {
        if (previous is not null) return;
        var tab = _tabs.Tabs.FirstOrDefault(tab => tab.Key == origin);
        if (tab is null) return; // Closing a view never deletes the admitted server session.
        var before = _tabs.Persisted;
        _tabs = _tabs with { Tabs = _tabs.Tabs.Replace(tab, tab with { SessionId = session.Id, Title = session.Title ?? tab.Title }) };
        QueueTabWrite(before, _tabs.Persisted);
        if (_tabs.Selected != origin || OpenTabSession is null) return;
        await RunTabNavigation(async token =>
        {
            var configuration = await OpenTabSession(session.Id, token);
            if (_tabs.Selected != origin) return;
            ApplyConfiguration(configuration); // Do not replace text typed after submission.
        }, throwErrors: true, ct: CancellationToken.None);
    }
}
