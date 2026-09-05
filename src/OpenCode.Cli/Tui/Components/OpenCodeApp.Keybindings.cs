namespace OpenCode.Cli.Tui.Components;

using System.Globalization;
using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Keymap;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Models;
using OpenTui.Blazor.TextMarks;
using OpenTui.Blazor;
using OpenTui.Blazor.Keymap;

public partial class OpenCodeApp
{
    [Parameter] public TuiKeybindConfig? Keybindings { get; set; }
    private readonly KeymapLayerRegistry _keyLayers = new();
    private readonly TuiKeymapMode _keyMode = new();
    private readonly List<IDisposable> _keyLeases = [];
    private readonly List<Task> _keyTasks = [];
    private KeymapDispatcher? _keyDispatcher;
    private IDisposable? _modalKeyMode;
    private bool _focusedPrompt;
    private string? _keyHint;
    private KeymapContext _lastKeyContext = new();
    private TuiKeybindConfig? _resolvedBindings;
    private KeymapContext _paletteContext = new();
    public IReadOnlyList<KeymapCommand> PaletteCommands => _keyLayers.ReachableCommands(_paletteContext)
        .Where(command => command.Title is not null && command.Name != "command.palette.show").ToArray();

    protected override void OnInitialized()
    {
        ConnectMessageActions();
        ConnectThemes();
        ConnectModelPreferences();
        _keyTasks.Add(LoadPromptStash());
        var config = Keybindings ?? TuiKeybindConfig.Defaults();
        _resolvedBindings = config;
        _keyDispatcher = config.CreateDispatcher();
        ConnectSettings();
        var editor = TuiEditorKeymap.CommandIds
            .Select(id => new KeymapCommand(id, _ => ExecuteEditor(id))
            {
                Title = id is "input.undo" or "input.redo" or "input.newline" ? DefaultBindings.All.FirstOrDefault(binding => binding.Id == id)?.Description : null,
                Category = "Prompt",
                Palette = id is "input.undo" or "input.redo" or "input.newline",
                Condition = new() { When = _ => ActivePrompt is { } state && (id != "input.undo" && id != "input.redo" || (id == "input.undo" ? state.CanUndo : state.CanRedo)) }
            }).ToArray();
        _keyLeases.Add(_keyLayers.Register(TuiEditorKeymap.CreateCommands(editor, PromptFocus)));
        _keyLeases.Add(_keyLayers.Register(TuiEditorKeymap.CreateBindings(config, context => PromptFocus(context)
            && context.Data.GetValueOrDefault("terminal.multiline") is true)));

        var commands = new List<TuiKeymapCommand>
        {
            Command("command.palette.show", invocation => { _paletteContext = invocation.Context; _palette = true; _dirty = true; StateHasChanged(); return true; }),
            Command("prompt.submit", invocation => PromptFocus(invocation.Context) && SubmitPrompt()),
            Command("app.exit", invocation => GuardedExit(invocation.Event)),
            Command("session.interrupt", _ => RequestSessionInterrupt()),
            Command("session.new", _ => TabKey(NewTab)),
            Command("session.list", _ => { if (!TabNavigationReady || LoadSessions is null || OpenSession is null) return false; _sessions = true; _dirty = true; StateHasChanged(); return true; }),
            Command("session.tab.next", _ => CycleTab(1)),
            Command("session.tab.previous", _ => CycleTab(-1)),
            Command("session.tab.close", _ => TabKey(() => CloseTab(_tabs.Selected))),
            Command("session.tab.reopen", _ => !_tabs.Closed.IsEmpty && TabKey(ReopenTab)),
            Command("model.list", _ => LoadCatalog is not null && IdleKey(() => OpenCatalog("model"))),
            Command("agent.list", _ => LoadCatalog is not null && IdleKey(() => OpenCatalog("agent"))),
            Command("variant.list", _ => CurrentModelSelection is not null && IdleKey(() => OpenCatalog("variant"))),
            Command("agent.cycle", _ => LoadCatalog is not null && IdleKey(() => CycleSelection("agent"))),
            Command("agent.cycle.reverse", _ => LoadCatalog is not null && IdleKey(() => CycleSelection("agent", -1))),
            Command("variant.cycle", _ => IdleKey(() => CyclePreferredModel(ModelPreferenceAction.VariantCycle)), available: () =>
                SelectionReady && CurrentModelSelection is not null && _modelController is not null && LoadCatalog is not null),
            Command("session.page.up", _ => ScrollKey(-1, false)),
            Command("session.page.down", _ => ScrollKey(1, false)),
            Command("session.half.page.up", _ => ScrollKey(-1, true)),
            Command("session.half.page.down", _ => ScrollKey(1, true)),
            Command("session.first", context => { if (PromptFocus(context.Context)) return false; TranscriptScroll.ScrollBy(-TranscriptScroll.ContentHeight); return true; }),
            Command("session.last", context => { if (PromptFocus(context.Context)) return false; TranscriptScroll.ScrollToEnd(); return true; }),
            Command("prompt.history.previous", _ => MoveHistory(-1)),
            Command("prompt.history.next", _ => MoveHistory(1)),
            Command("session.toggle.thinking", _ => ToggleThinking())
        };
        commands.AddRange([
            Command("prompt.stash", _ => PushPromptStash(), "Stash prompt", () => StashReady && _input.Length > 0 && StashAccess().Allowed),
            Command("prompt.stash.pop", _ => PopStash(), "Stash pop", () => StashReady && _promptStash.Snapshot.Entries.Count > 0 && StashAccess().Allowed),
            Command("prompt.stash.list", _ => OpenStash(), "Stash list", () => StashReady && _promptStash.Snapshot.Entries.Count > 0),
            Command("model.cycle_recent", _ => IdleKey(() => CyclePreferredModel(ModelPreferenceAction.RecentCycle)), available: () =>
                SelectionReady && CurrentModelSelection is not null && _modelController is not null && LoadCatalog is not null),
            Command("model.cycle_recent_reverse", _ => IdleKey(() => CyclePreferredModel(ModelPreferenceAction.RecentCycle, -1)), available: () =>
                SelectionReady && CurrentModelSelection is not null && _modelController is not null && LoadCatalog is not null),
            Command("model.cycle_favorite", _ => IdleKey(() => CyclePreferredModel(ModelPreferenceAction.FavoriteCycle)), available: () =>
                SelectionReady && _modelController is not null && LoadCatalog is not null),
            Command("model.cycle_favorite_reverse", _ => IdleKey(() => CyclePreferredModel(ModelPreferenceAction.FavoriteCycle, -1)), available: () =>
                SelectionReady && _modelController is not null && LoadCatalog is not null),
            Command("opencode.status", _ => { _keyTasks.Add(OpenStatus()); return true; }, "View status"),
            Command("theme.switch", _ => { _keyTasks.Add(OpenThemes()); return true; }, "Switch theme", () => Themes is not null && ApplicationThemeCatalog is not null),
            Command("prompt.paste", invocation => { if (!PromptFocus(invocation.Context)) return false; _keyTasks.Add(PasteClipboard()); return true; }, "Paste from clipboard",
                () => ReadPromptClipboard is not null && !_clipboardReading && !PromptBlocked && !_activitiesOpen && !_terminalFocused && !_terminalListOpen),
            Command("session.child.first", _ => { if (_activitiesOpen) CloseActivities(); else _keyTasks.Add(OpenActivities(Activities.ActivityTab.Subagents)); return true; },
                "Toggle subagent picker", () => _sessionId is not null && !PromptBlocked && RequireSessionClient is not null),
            Command("session.shells", _ => { _keyTasks.Add(OpenActivities(Activities.ActivityTab.Shells)); return true; },
                "View shell commands", () => _sessionId is not null && !PromptBlocked && RequireSessionClient is not null, false),
            Command("skill.list", _ => { _keyTasks.Add(OpenSkills()); return true; }, "Select skill", () => RequireSessionClient is not null && !PromptBlocked),
            Command("recovery.retry", _ => { _keyTasks.Add(RetryRecoveryConnection()); return true; }, "Retry connection now",
                () => RetryRecoveryFeed is not null && !RecoveryBusy, false),
            Command("recovery.reload", _ => { _keyTasks.Add(ReloadSelectedRecoverySession()); return true; }, "Reload session data",
                () => ReloadRecoverySession is not null && _sessionId is not null && !RecoveryBusy, false),
            Command("recovery.cancel", _ => { CancelRecoveryWait(); return true; }, "Cancel recovery wait", () => RecoveryBusy, false),
            Command("prompt.images.view", _ => { _keyTasks.Add(OpenImagePreview(new(PromptImages))); return true; }, "View image attachments",
                () => CreateImageLoader is not null && PromptImages.Count > 0),
            Command("terminal.select", _ => { _keyTasks.Add(OpenTerminalList()); return true; }, "Select terminal", () => _sessionId is not null && RequireSessionClient is not null && !_terminalLoading),
            Command("terminal.toggle", _ => { _keyTasks.Add(OpenTerminalList(toggle: true)); return true; }, "Toggle terminal pane", () => _sessionId is not null && RequireSessionClient is not null && !_terminalLoading),
            Command("terminal.close", _ => { HideTerminal(); return true; }, "Close terminal pane", () => TerminalVisible),
            Command("session.terminal", _ => { _keyTasks.Add(OpenTerminalList(create: true)); return true; }, "New terminal", () => _sessionId is not null && RequireSessionClient is not null && !_terminalLoading),
            Command("pane.focus.left", _ => { FocusSessionPane(); return true; }, "Focus session pane", () => TerminalVisible),
            Command("pane.focus.right", _ => { _focusTerminal?.Invoke(); return true; }, "Focus terminal pane", () => TerminalVisible && _focusTerminal is not null),
            Command("session.queued_prompts", _ => { _keyTasks.Add(OpenQueuedPrompts()); return true; }, "View queued prompts",
                () => MutateInbox is not null && QueuedInputs.Count > 0),
            Command("session.pending_prompts", _ => { _keyTasks.Add(OpenPendingInputs(false)); return true; }, "View pending prompts",
                () => MutateInbox is not null && PendingInputs.Count > 0, false),
            Command("prompt.queue", invocation => PromptFocus(invocation.Context) && SubmitPrompt(InboxDeliveryMode.Queue),
                "Queue prompt", () => NetworkPromptInput is not null && !ShellMode && !PromptBlocked && !_configurationBusy
                    && !string.IsNullOrWhiteSpace(_input)
                    && ReadAdmissionAvailability?.Invoke(_sessionId, CapturePromptAdmission(_input) with { Delivery = InboxDeliveryMode.Queue }).Allowed == true),
            Command("provider.connect", _ => { _keyTasks.Add(OpenManagement(false)); return true; }, "Connect an integration", () => RequireSessionClient is not null && !_configurationBusy),
            Command("mcp.list", _ => { _keyTasks.Add(OpenManagement(true)); return true; }, "MCP servers", () => RequireSessionClient is not null && !_configurationBusy),
            Command("opencode.settings", _ => { _keyTasks.Add(OpenSettings()); return true; }, "Open settings", () => Settings is not null),
            Command("app.toggle.diffwrap", _ => { _keyTasks.Add(Settings!.ChangeAsync("diffs.wrap", 1, _configurationLifetime.Token)); return true; },
                "Toggle diff wrapping", () => Settings?.Loaded == true && !Settings.Saving),
            Command("session.toggle.exploration_grouping", _ => { _keyTasks.Add(Settings!.ChangeAsync("session.grouping", 1, _configurationLifetime.Token)); return true; },
                "Toggle related tool call grouping", () => Settings?.Loaded == true && !Settings.Saving),
            Command("session.sidebar.toggle", _ => { ToggleSidebar(); return true; }, "Toggle sidebar", () => _hasConversation && !_childSession),
            Command("session.tools.toggle", _ => { ShowToolDetails = !ShowToolDetails; _dirty = true; return true; }, "Toggle tool details", () => _hasConversation, false),
            Command("session.usage.toggle", _ => { ShowUsage = !ShowUsage; _dirty = true; return true; }, "Toggle token usage", () => _hasConversation, false),
            Command("session.timestamps.toggle", _ => { ShowTimestamps = !ShowTimestamps; _dirty = true; return true; }, "Toggle timestamps", () => _hasConversation, false),
            Command("session.rows.show", _ => { _transcriptRowPicker = true; _dirty = true; StateHasChanged(); return true; }, "Expand or collapse transcript row", () => TranscriptRowOptions().Count > 0, false),
            Command("session.follow", _ => { TranscriptScroll.ScrollToEnd(); _dirty = true; return true; }, "Follow latest output", () => _hasConversation && !TranscriptScroll.AtBottom, false),
            Command("session.tabs.show", _ => { _tabList = true; _dirty = true; StateHasChanged(); return true; }, "Switch session tab", () => NavigationReady, false),
            Command("configuration.reload", _ => IdleKey(() => RunConfigurationAction(async token => { await RefreshConfiguration(token); }, cancellationToken: CancellationToken.None)), "Reload configuration", () => NavigationReady && ReloadConfiguration is not null, false),
            Command("form.refresh", _ => { _keyTasks.Add(RefreshFormState()); return true; }, "Refresh pending forms", () => RefreshForms is not null && !_refreshingForms, false)
        ]);
        for (var index = 0; index < 10; index++)
        {
            var tab = index;
            commands.Add(Command($"session.tab.select.{index + 1}", _ => _tabs.SelectIndex(tab) is { } target && TabKey(() => SelectTab(target.Key)),
                available: () => TabNavigationReady && _tabs.SelectIndex(tab) is not null));
        }
        _keyLeases.Add(_keyLayers.Register(TuiKeymapLayer.Create(config, commands, priority: 10,
            condition: new() { When = context => !Equals(context.Data.GetValueOrDefault("terminal.focusKey"), "permission") })));
        _keyLeases.Add(_keyLayers.Register(TuiKeymapLayer.Create(config,
            [Command("prompt.clear", _ => ClearPrompt())], priority: 20,
            condition: new() { When = context => PromptFocus(context) && (HasSelection || _input.Length > 0) })));
    }

    private TuiKeymapCommand Command(string name, Func<KeymapInvocation, bool> callback, string? title = null,
        Func<bool>? available = null, bool bind = true) => new(new(name, callback)
        {
            Title = title ?? DefaultBindings.All.FirstOrDefault(binding => binding.Id == name)?.Description ?? name,
            Palette = true,
            Suggested = _ => name is "session.new" or "session.list" or "model.list",
            Category = name.StartsWith("input.", StringComparison.Ordinal) || name.StartsWith("prompt.", StringComparison.Ordinal) ? "Prompt"
                : name.StartsWith("session.", StringComparison.Ordinal) ? "Session"
                : name.StartsWith("model.", StringComparison.Ordinal) || name.StartsWith("variant.", StringComparison.Ordinal) ? "Model"
                : name.StartsWith("agent.", StringComparison.Ordinal) ? "Agent" : "System",
            Condition = new() { When = context => (available?.Invoke() ?? CommandAvailable(name))
                && (name is not ("session.first" or "session.last") || !PromptFocus(context)) }
        }) { Bind = bind, Palette = true };

    private bool CommandAvailable(string name) => name switch
    {
        "session.new" => TabNavigationReady && NewConversation is not null,
        "session.list" => TabNavigationReady && LoadSessions is not null && OpenSession is not null,
        "session.tab.reopen" => TabNavigationReady && !_tabs.Closed.IsEmpty,
        "model.list" => SelectionReady && LoadCatalog is not null,
        "variant.list" => SelectionReady && CurrentModelSelection is not null && _modelController is not null && LoadCatalog is not null,
        "agent.list" or "agent.cycle" or "agent.cycle.reverse" => SelectionReady && LoadCatalog is not null,
        "session.interrupt" => SelectedSessionRunning,
        "session.toggle.thinking" => _hasConversation,
        "session.first" or "session.last" or "session.page.up" or "session.page.down" or "session.half.page.up" or "session.half.page.down" => _hasConversation,
        _ => !name.StartsWith("session.tab.", StringComparison.Ordinal) || TabNavigationReady
    };
    private bool PromptFocus(KeymapContext context) => context.Data.GetValueOrDefault("terminal.textarea") is TextareaState state
        && ReferenceEquals(state, ActivePrompt) && Equals(context.Data.GetValueOrDefault("terminal.focusKey"), "prompt");
    private bool NavigationReady => _request is null && !_configurationBusy;
    private bool SelectionReady => !_configurationBusy;

    public KeymapDispatchResult? DispatchKeymap(ConsoleKeyInfo key, KeymapContext context, TimeSpan now)
    {
        if (_keyDispatcher is null) return null;
        NoteTabKeyboardFocus();
        if (HandleTabMenuKey(key)) return new KeymapDispatchResult(true, true, true, [], [], KeymapDispatchReason.Handled);
        if (HandleSidebarKey(key)) return new KeymapDispatchResult(true, true, true, [], [], KeymapDispatchReason.Handled);
        _lastKeyContext = context;
        var modal = context.Data.GetValueOrDefault("terminal.modal") is true;
        if (modal && _modalKeyMode is null) _modalKeyMode = _keyMode.Push(TuiKeymapLayer.ModalMode);
        if (!modal && _modalKeyMode is not null) { _modalKeyMode.Dispose(); _modalKeyMode = null; }
        _focusedPrompt = PromptFocus(context);
        if (context.Data.GetValueOrDefault("terminal.focusKey") is string focus && (focus == "terminal-list" || focus.StartsWith("activity-", StringComparison.Ordinal))) return null;
        if (HandleReferenceAutocomplete(key) || HandleCommandAutocomplete(key)) return new KeymapDispatchResult(true, true, true, [], [], KeymapDispatchReason.Handled);
        if (KeyName(key) is { } shellKey && HandleShellControl(new(new KeyStroke(shellKey, key.Modifiers.HasFlag(ConsoleModifiers.Control),
            key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt)))))
            return new KeymapDispatchResult(true, true, true, [], [], KeymapDispatchReason.Handled);
        if (Equals(context.Data.GetValueOrDefault("terminal.focusKey"), "form"))
        {
            _keyDispatcher.ClearPending();
            _focusedPrompt = false;
            _keyHint = null;
            return null; // Let the form's focused Input consume editing, submit, and cancellation.
        }
        var name = KeyName(key);
        if (name is null) return null;
        var result = _keyDispatcher.Dispatch(new(new KeyStroke(name, key.Modifiers.HasFlag(ConsoleModifiers.Control),
            key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt))), _keyLayers, _keyMode.Apply(context), now);
        var hint = result.Pending.Count == 0 ? null : string.Join(" ", result.Pending.Select(part => part.Display));
        if (_keyHint != hint) { _keyHint = hint; _dirty = true; }
        return result;
    }

    public KeymapDispatchResult? DispatchKeymap(TerminalKeyInput input, KeymapContext context, TimeSpan now)
    {
        var projection = input.ProjectConsoleKeys();
        if (projection.IsExact && projection.Keys.Count == 1) return DispatchKeymap(projection.Keys[0], context, now);
        if (_keyDispatcher is null) return null;
        NoteTabKeyboardFocus();
        _lastKeyContext = context;
        var modal = context.Data.GetValueOrDefault("terminal.modal") is true;
        if (modal && _modalKeyMode is null) _modalKeyMode = _keyMode.Push(TuiKeymapLayer.ModalMode);
        if (!modal && _modalKeyMode is not null) { _modalKeyMode.Dispose(); _modalKeyMode = null; }
        _focusedPrompt = PromptFocus(context);
        if (context.Data.GetValueOrDefault("terminal.focusKey") is string focus && (focus is "form" or "terminal-list" || focus.StartsWith("activity-", StringComparison.Ordinal))) return null;
        if (!input.TryGetKeymapEvent(out var key)) return null;
        if (HandleRichAutocomplete(key)) return new KeymapDispatchResult(true, true, true, [], [], KeymapDispatchReason.Handled);
        if (HandleShellControl(key)) return new KeymapDispatchResult(true, true, true, [], [], KeymapDispatchReason.Handled);
        var result = _keyDispatcher.Dispatch(key, _keyLayers, _keyMode.Apply(context), now);
        var hint = result.Pending.Count == 0 ? null : string.Join(" ", result.Pending.Select(part => part.Display));
        if (_keyHint != hint) { _keyHint = hint; _dirty = true; }
        return result;
    }

    private string Shortcut(string command) => _resolvedBindings?.Get(command).FirstOrDefault() is { } binding
        ? string.Join(" ", binding.Sequence.Select(part => part.Stroke.ToString())) : "";
    private string AllShortcuts(string command) => string.Join(", ", (_resolvedBindings?.Get(command) ?? [])
        .Select(binding => string.Join(" ", binding.Sequence.Select(part => part.Stroke.ToString()))));

    public void TickKeymap(TimeSpan now)
    {
        if (_keyDispatcher?.Expire(now) == true) { _keyHint = null; _dirty = true; }
        for (var index = _keyTasks.Count - 1; index >= 0; index--)
        {
            if (!_keyTasks[index].IsCompleted) continue;
            var task = _keyTasks[index];
            _keyTasks.RemoveAt(index);
            task.GetAwaiter().GetResult();
        }
    }

    private bool IdleKey(Func<Task> action)
    {
        if (!SelectionReady) return false;
        var task = action();
        if (task.IsCompleted) task.GetAwaiter().GetResult();
        else _keyTasks.Add(task);
        return true;
    }

    private bool CycleTab(int direction)
    {
        return TabKey(() => SelectAdjacentTab(direction));
    }

    private bool ScrollKey(int direction, bool half)
    {
        if (!_hasConversation) return false;
        TranscriptScroll.ScrollBy(direction * Math.Max(1, TranscriptScroll.ViewportHeight / (half ? 2 : 1)));
        _dirty = true;
        return true;
    }

    private bool GuardedExit(KeymapEvent? input)
    {
        if (input is null) { ExitRequested = true; return true; }
        if (input?.Stroke is { Ctrl: true, Name: "c" } && _focusedPrompt && (HasSelection || _input.Length > 0)) return ClearPrompt();
        if (input?.Stroke is { Ctrl: true, Name: "d" } && _focusedPrompt && _input.Length > 0) return false;
        if (SelectedSessionRunning) RequestSessionInterrupt();
        else ExitRequested = true;
        return true;
    }

    private bool RequestSessionInterrupt()
    {
        _keyTasks.Add(InterruptWithFeedback());
        return true;
    }

    private async Task InterruptWithFeedback()
    {
        try { await InterruptActiveSession(); }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _inputError = SessionClientAdapter.Describe(exception); }
        _dirty = true;
    }

    private bool ClearPrompt()
    {
        if (ActivePrompt is not { } state) return false;
        if (HasSelection) state.ClearSelection();
        else if (state.Text.Length > 0) { ResetPromptDocument(); _historyIndex = _history.Count; }
        else return false;
        ObservePrompt(EditorKey, state);
        return true;
    }

    private bool MoveHistory(int direction)
    {
        if (!_focusedPrompt || ActivePrompt is not { } state || _history.Count == 0) return false;
        ObservePrompt(EditorKey, state);
        if (direction < 0 && _cursor != 0 || direction > 0 && _cursor != _input.Length)
        {
            var before = state.Snapshot.CursorUtf16;
            state.Execute(direction < 0 ? TextareaCommand.Up : TextareaCommand.Down);
            if (state.Snapshot.CursorUtf16 == before) state.Execute(direction < 0 ? TextareaCommand.BufferHome : TextareaCommand.BufferEnd);
            ObservePrompt(EditorKey, state);
            return true;
        }
        if (direction < 0 && _historyIndex == _history.Count)
        {
            _historyDrafts[EditorKey] = CapturePromptDocument();
        }
        var next = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        var document = next == _history.Count ? _historyDrafts.GetValueOrDefault(EditorKey) : _historyDocuments.GetValueOrDefault((EditorKey, next));
        if (document is null) { OnInputError("The structured history document is unavailable."); return true; }
        RestorePromptDocument(EditorKey, PromptDocumentAdapter.Prepare(document, direction < 0 ? 0 : document.Input.Text.Length));
        _historyIndex = next;
        return true;
    }

    private bool ExecuteEditor(string command)
    {
        if (!_focusedPrompt || ActivePrompt is not { } state || TuiEditorKeymap.EditorCommand(command) is not { } binding) return false;
        return state.Execute(binding.Command, binding.Select);
    }

    private bool SubmitPrompt(InboxDeliveryMode? delivery = null)
    {
        if (ActivePrompt is not { } state) { OnInputError("The native prompt is not ready."); return true; }
        ObservePrompt(EditorKey, state);
        if (PromptBlocked)
        {
            _status = "Answer the pending request first; the draft was kept.";
            _dirty = true;
            return true;
        }
        if (_shellPreparing.Contains(EditorKey))
        { _inputError = "The shell Session is still being prepared; the draft was kept."; _dirty = true; return true; }
        if (ShellMode) return SubmitShell(delivery);
        if (_input.StartsWith('/') && !HasAttachedSkillSlash && SubmitSlashCommand(delivery ?? InboxDeliveryMode.Steer)) return true;
        if (_configurationBusy)
        {
            _status = "A configuration change is in progress; the draft was kept.";
            _dirty = true;
            return true;
        }
        if (string.IsNullOrWhiteSpace(_input)) { PromoteFirstQueued(); return true; }
        if (HasPromptAttachments && NetworkPromptInput is null && NetworkSelectedPrompt is null)
        {
            _inputError = "The restored attachments were kept. Typed prompt admission must be connected before this draft can be sent.";
            _dirty = true;
            return true;
        }
        // Match the upstream composer: capture and clear before any asynchronous
        // preparation so subsequent typing belongs to the next draft.
        var prompt = CapturePromptAdmission(_input);
        if (delivery is { } mode) prompt = prompt with { Delivery = mode };
        if (_retryPromptInputs.TryGetValue(EditorKey, out var previous) && !RetryIsUncertain(previous) && !SessionClientAdapter.SamePrompt(prompt, previous))
            prompt = prompt with { Id = null };
        if (!CanSubmitPrompt(prompt)) return true;
        var selection = CaptureSelection(prompt);
        if (NetworkSelectedPrompt is not null && (selection.Agent is null || selection.Model is null))
        { _inputError = "Select an agent and model before submitting. The draft was kept."; _dirty = true; return true; }
        if (prompt.Id is null && selection.Model is { } selectedModel)
        {
            try { ModelPreferenceCatalog.RequireSelection(selectedModel, _catalog?.Models ?? _presentation?.Models ?? [], _catalog?.Providers ?? _presentation?.Providers ?? []); }
            catch (InvalidOperationException exception) { _inputError = exception.Message; _dirty = true; return true; }
        }
        RememberPromptHistory(prompt);
        var document = _historyDocuments[(EditorKey, _history.Count)];
        ResetPromptDocument();
        _request = new CancellationTokenSource();
        _status = "Checking configuration...";
        _stream = StreamAsync(prompt, _request.Token, selection, document);
        _dirty = true;
        return true;
    }

    private static string? KeyName(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.Enter => "return", ConsoleKey.Escape => "escape", ConsoleKey.Tab => "tab", ConsoleKey.Spacebar => "space",
        ConsoleKey.Backspace => "backspace", ConsoleKey.Delete => "delete", ConsoleKey.Insert => "insert",
        ConsoleKey.OemMinus => "-", ConsoleKey.OemPeriod => ".",
        ConsoleKey.LeftArrow => "left", ConsoleKey.RightArrow => "right", ConsoleKey.UpArrow => "up", ConsoleKey.DownArrow => "down",
        ConsoleKey.Home => "home", ConsoleKey.End => "end", ConsoleKey.PageUp => "pageup", ConsoleKey.PageDown => "pagedown",
        >= ConsoleKey.A and <= ConsoleKey.Z => ((char)('a' + key.Key - ConsoleKey.A)).ToString(),
        >= ConsoleKey.D0 and <= ConsoleKey.D9 => ((char)('0' + key.Key - ConsoleKey.D0)).ToString(),
        >= ConsoleKey.F1 and <= ConsoleKey.F24 => key.Key.ToString().ToLowerInvariant(),
        _ => !char.IsControl(key.KeyChar) && !char.IsSurrogate(key.KeyChar) ? key.KeyChar.ToString() : null
    };
}
