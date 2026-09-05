namespace OpenCode.Cli.Tui.Components;

using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using OpenTui.Blazor;
using OpenTui.Blazor.Components;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Transcript;
using OpenCode.Cli.Tui.Sessions;
using OpenCode.Cli.Tui.Permissions;
using System.Text.Json;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Cli.Tui.Models;

public partial class OpenCodeApp : ComponentBase, ITerminalApp, IHandleEvent
{
    [Inject] public TimeProvider Clock { get; set; } = TimeProvider.System;
    [Parameter] public string? ActiveModel { get; set; }
    [Parameter] public string? ActiveAgent { get; set; }
    [Parameter] public string? ActiveProvider { get; set; }
    [Parameter] public string? ActiveVariant { get; set; }
    [Parameter] public string? SelectionError { get; set; }
    [Parameter] public string? ExecutionError { get; set; }
    [Parameter] public Func<CancellationToken, Task<PromptConfiguration>>? ReloadConfiguration { get; set; }
    [Parameter] public bool ReloadOnStart { get; set; }
    [Parameter] public Func<string, CancellationToken, IAsyncEnumerable<SessionResponseSnapshot>>? NetworkPrompt { get; set; }
    [Parameter] public string Version { get; set; } = "unknown";
    [Parameter] public string CurrentDirectory { get; set; } = "";
    [Parameter] public Func<CancellationToken, Task>? NewConversation { get; set; }
    [Parameter] public Func<CancellationToken, Task<AppCatalog>>? LoadCatalog { get; set; }
    [Parameter] public Action<AgentId?, ModelRef?>? ConfigureNewSession { get; set; }
    [Parameter] public Func<SessionPickerQuery, CancellationToken, Task<SessionPickerPage>>? LoadSessions { get; set; }
    [Parameter] public Func<SessionInfo, CancellationToken, Task<PromptConfiguration>>? OpenSession { get; set; }
    [Parameter] public Func<CancellationToken, Task<PromptConfiguration>>? CreateSession { get; set; }
    [Parameter] public Func<IReadOnlyList<PermissionRequest>>? ReadPermissions { get; set; }
    [Parameter] public Func<PermissionDecision, CancellationToken, Task>? ReplyPermission { get; set; }
    [Parameter] public Func<CancellationToken, Task>? RefreshPermissions { get; set; }
    [Parameter] public Func<IReadOnlySet<SessionId>>? ReadActiveSessions { get; set; }
    [Parameter] public Func<bool>? ReadPersistentPermissionGrants { get; set; }
    private bool _canPersistAlways;
    public bool ExitRequested { get; private set; }
    private string _input = "";
    private int _cursor;
    private int _height = 24;
    private int _width = 80;
    private string _status = "Ready";
    private readonly List<string> _history = [];
    private int _historyIndex;
    private bool _dirty;
    private int _composerWidth = 1;
    private bool _palette;
    private bool _models;
    private bool _agents;
    private bool _variants;
    private bool _sessions;
    private bool PromptOverlayOpen => _palette || _models || _agents || _variants || _sessions || _settings || _tabList
        || _tabRename is not null || _transcriptRowPicker || _messageTarget is not null || _imagePreview is not null
        || _skillsOpen || _integrations || _mcps || _inboxDialog || _statusDialog || _themesDialog || _stashOpen || _recoveryMovePicker is not null;
    private bool _catalogLoading;
    private string? _catalogError;
    private AppCatalog? _catalog;
    private ModelRef? _modelSelection;
    private AgentId? _agentSelection;
    private readonly Dictionary<(string Provider, string Model), string?> _modelVariants = [];
    private ModelRef? _configuredAgentModel;
    private ModelRef? _creationFallback;
    private SessionId? _sessionId;
    private bool _childSession;
    private IReadOnlyList<PermissionRequest> _permissions = [];
    private IReadOnlySet<SessionId> _activeSessions = new HashSet<SessionId>();
    private PermissionRequest? ActivePermission => _childSession ? null : _permissions.FirstOrDefault(permission =>
        permission.SessionId == _sessionId || _formScope is { } scope && scope.Session == _sessionId && scope.Descendants.Contains(permission.SessionId));
    private AssistantToolContent? PermissionTool => ActivePermission?.Source is { } source
        ? PermissionMessages.OfType<AssistantMessage>().FirstOrDefault(message => message.Id.Value == source.MessageId)?
            .Content.OfType<AssistantToolContent>().FirstOrDefault(tool => tool.Id == source.Id) : null;
    private IReadOnlyDictionary<string, JsonElement>? PermissionInput => PermissionTool?.State switch
    {
        ToolStateRunning running => running.Input,
        ToolStateCompleted completed => completed.Input,
        ToolStateError error => error.Input,
        _ => null
    };
    private IReadOnlyDictionary<string, JsonElement>? PermissionMetadata => PermissionTool?.State switch
    {
        ToolStateRunning running => running.Metadata,
        ToolStateCompleted completed => completed.Metadata,
        ToolStateError error => error.Metadata,
        _ => null
    };
    private ModelInfo? SelectedModelInfo => _catalog?.Models.FirstOrDefault(model => model.ProviderId.Value == CurrentModelSelection?.ProviderId && model.Id.Value == CurrentModelSelection?.Id);
    private string _conversationTitle = "New session";
    private readonly CancellationTokenSource _configurationLifetime = new();
    private Task _configurationTask = Task.CompletedTask;
    private bool _configurationBusy;
    private CancellationTokenSource? _configurationOperation;
    private bool _initialReloadStarted;
    private string? _connection;
    private SessionResponseSnapshot? _responseState;
    private IReadOnlyList<SessionMessage>? _projectedHistory;
    private bool _hasConversation;
    private readonly List<SessionResponseSnapshot> _responses = [];
    private readonly Dictionary<MessageId, string> _promptTexts = [];
    public IReadOnlyList<SessionMessage> TranscriptHistory => _projectedHistory ?? [];
    public IReadOnlyList<SessionResponseSnapshot> TranscriptResponses => _responses;
    public IReadOnlyDictionary<MessageId, string> TranscriptPrompts => _promptTexts;
    public TerminalScrollState TranscriptScroll { get; private set; } = new();
    public long TranscriptRevision { get; private set; }
    public bool ShowReasoning { get; private set; }
    public bool ShowToolDetails { get; private set; }
    public bool ShowUsage { get; private set; }
    public bool ShowTimestamps { get; private set; }
    private IReadOnlyList<SessionMessage> _transcriptMessages = [];
    private long _renderedTranscriptRevision = -1;

    [SuppressMessage("Usage", "BL0012", Justification = "IHandleEvent bypasses ComponentBase automatic rendering. Closing must update modal focus before the host dispatches the next queued key.")]
    private void CloseDialog()
    {
        CloseActivities();
        CloseRecoveryDirectory();
        _statusDialog = _themesDialog = false;
        _stashOpen = false;
        CloseTabMenu();
        if (_tabRename is not null) CloseTabRename();
        if (_catalogLoading) _configurationOperation?.Cancel();
        _palette = _models = _agents = _variants = _sessions = _tabList = false;
        _settings = false;
        _messageTarget = null;
        _imagePreview = null;
        _skillsOpen = false;
        _integrations = _mcps = false;
        _inboxDialog = false;
        _terminalListOpen = false;
        _pendingSteer = null;
        _transcriptRowPicker = false;
        _modalKeyMode?.Dispose();
        _modalKeyMode = null;
        _activityMode?.Dispose();
        _activityMode = null;
        _dirty = true;
        StateHasChanged();
    }

    [SuppressMessage("Usage", "BL0012", Justification = "IHandleEvent bypasses ComponentBase automatic rendering. Command-driven modal changes must be rendered before the host dispatches another queued key.")]
    private Task RunCommand(string command)
    {
        CloseDialog();
        _focusedPrompt = PromptFocus(_paletteContext);
        if (_keyDispatcher?.DispatchCommand(command, _keyLayers, _paletteContext).Handled != true)
            _inputError = $"Command '{command}' is unavailable in the current context.";
        _dirty = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task OpenCatalog(string kind)
    {
        _modelProvider = null;
        if (LoadCatalog is null || _configurationBusy) return;
        _models = kind == "model";
        _agents = kind == "agent";
        _variants = kind == "variant";
        _catalogLoading = true;
        _catalogError = null;
        _dirty = true;
        StateHasChanged();
        await RunConfigurationAction(async token =>
        {
            try
            {
                _catalog = await LoadCatalog(token);
                var location = SelectionLocation;
                _modelCatalogContext = (ReadSessionClient?.Invoke(), location, ReadManagementRevision?.Invoke(location) ?? default);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { _catalogError = SessionClientAdapter.Describe(exception); }
            finally { _catalogLoading = false; }
        }, cancellationToken: CancellationToken.None);
    }

    private Task ChooseModel(ModelRef model) => AcceptExactModel(model, _configurationLifetime.Token);

    private Task ApplyModelSelection(ModelRef model, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReconcileSelectionDrafts();
        if (_catalog is null) throw new InvalidOperationException("The model catalog is unavailable.");
        ModelPreferenceCatalog.RequireSelection(model, _catalog.Models, _catalog.Providers);
        model = model with { Variant = ModelPreferences.NormalizeVariant(model.Variant) };
        if (_sessionId is { } id) _sessionModelDrafts[id] = model;
        else if (_agentSelection is { } agent) _homeModelChoices[(SelectionLocation, agent)] = model with { Variant = null };
        else throw new InvalidOperationException("Select an agent before choosing a Home model.");
        _modelPreferenceInfo = _modelPreferenceError = null;
        RefreshDraftSelection();
        _dirty = true;
        return Task.CompletedTask;
    }

    private Task ChooseAgent(AgentId agent) => RunConfigurationAction(token => ApplyAgentSelection(agent, token), throwErrors: true, cancellationToken: CancellationToken.None);

    private Task ApplyAgentSelection(AgentId agent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReconcileSelectionDrafts();
        var configured = _catalog?.Agents.FirstOrDefault(item => item.Id == agent && !item.Hidden && item.Mode != AgentMode.Subagent)
            ?? throw new InvalidOperationException("The selected agent is no longer in the catalog.");
        if (_sessionId is { } id) _sessionAgentDrafts[id] = configured.Id;
        else _homeAgentChoices[SelectionLocation] = configured.Id;
        _agentSelection = configured.Id;
        RefreshDraftSelection();
        _dirty = true;
        return Task.CompletedTask;
    }

    private Task CycleSelection(string kind, int direction = 1) => RunConfigurationAction(async token =>
    {
        if (LoadCatalog is null) return;
        _catalog = await LoadCatalog(token);
        if (kind == "agent")
        {
            var agents = _catalog.Agents.Where(agent => !agent.Hidden && agent.Mode != AgentMode.Subagent).ToArray();
            if (agents.Length == 0) return;
            var current = Array.FindIndex(agents, agent => agent.Id == _agentSelection);
            await ApplyAgentSelection(agents[(current + direction + agents.Length) % agents.Length].Id, token);
            return;
        }
    }, cancellationToken: CancellationToken.None);

    private Task ChooseSession(SessionInfo session, CancellationToken cancellationToken) => RunConfigurationAction(async token =>
    {
        if (OpenSession is null) throw new InvalidOperationException("Session selection is not connected to the server.");
        var configuration = await OpenSession(session, token);
        token.ThrowIfCancellationRequested();
        var before = _tabs.Persisted;
        CaptureTab();
        var tab = _tabs.Tabs.FirstOrDefault(tab => tab.SessionId == session.Id)
            ?? new SessionTab(Guid.NewGuid(), session.Id, session.Title ?? session.Id.Value);
        if (!_tabs.Tabs.Contains(tab)) _tabs = _tabs with { Tabs = _tabs.Tabs.Add(tab) };
        _tabs = _tabs.Select(tab.Key);
        RestoreTab(tab, configuration);
        QueueTabWrite(before, _tabs.Persisted);
    }, throwErrors: true, cancellationToken);

    private Task CreateSessionFromPicker(CancellationToken cancellationToken) => RunConfigurationAction(async token =>
    {
        if (CreateSession is null) throw new InvalidOperationException("Session creation is not connected to the server.");
        var configuration = await CreateSession(token);
        token.ThrowIfCancellationRequested();
        if (configuration.SessionId is not { } id) throw new InvalidOperationException("Session creation returned no session identity.");
        var before = _tabs.Persisted;
        CaptureTab();
        var tab = new SessionTab(Guid.NewGuid(), id, configuration.SessionTitle ?? "New session");
        _tabs = _tabs with { Tabs = _tabs.Tabs.Add(tab), Selected = tab.Key };
        RestoreTab(tab, configuration);
        QueueTabWrite(before, _tabs.Persisted);
    }, throwErrors: true, cancellationToken);

    private Task RunConfigurationAction(Func<CancellationToken, Task> action, bool throwErrors = false, CancellationToken cancellationToken = default)
    {
        if (_configurationBusy)
        {
            if (throwErrors) throw new InvalidOperationException("Another session operation is still in progress.");
            return Task.CompletedTask;
        }
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_configurationLifetime.Token, cancellationToken);
        _configurationOperation = operation;
        _configurationBusy = _dirty = true;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _configurationTask = finished.Task;
        return RunAsync();

        async Task RunAsync()
        {
            try { await action(operation.Token); }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                if (throwErrors) throw;
            }
            catch (Exception exception)
            {
                if (throwErrors) throw new InvalidOperationException(SessionClientAdapter.Describe(exception), exception);
                ExecutionError = SessionClientAdapter.Describe(exception);
                _status = "Unavailable";
            }
            finally
            {
                operation.Dispose();
                _configurationOperation = null;
                _configurationBusy = false;
                _dirty = true;
                finished.TrySetResult();
            }
        }
    }

    private async Task<bool> RefreshConfiguration(CancellationToken cancellationToken)
    {
        if (ReloadConfiguration is not null)
        {
            var configuration = await ReloadConfiguration(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyConfiguration(configuration);
        }
        _status = SelectionError is null && ExecutionError is null ? "Ready" : "Unavailable";
        _dirty = true;
        return SelectionError is null && ExecutionError is null;
    }

    private void ApplyConfiguration(PromptConfiguration configuration)
    {
            if (configuration.Location is { } location && _selectionLocation != location) _catalog = null;
            _selectionLocation = configuration.Location ?? (configuration.Directory is { } path ? new LocationRef(path) : _selectionLocation);
            if (ReadPresentation?.Invoke() is { } presentation && presentation.Session?.Id == configuration.SessionId && presentation.Location == _selectionLocation)
                _presentation = presentation;
            if (configuration.Directory is { } directory) CurrentDirectory = directory;
            if (configuration.ModelSelection is { } selection)
                _modelVariants[(selection.ProviderId, selection.Id)] = selection.Variant;
            if (configuration.SessionId is not null) { _sessionId = configuration.SessionId; _hasConversation = true; }
            _childSession = configuration.ChildSession;
            _modelSelection = configuration.ModelSelection;
            _agentSelection = configuration.AgentSelection;
            _configuredAgentModel = configuration.AgentModel;
            _creationFallback = configuration.CreationFallback;
            ActiveAgent = configuration.Agent;
            ActiveModel = configuration.Model;
            ActiveProvider = configuration.Provider;
            ActiveVariant = configuration.Variant;
            SelectionError = configuration.SelectionError;
            ExecutionError = configuration.ExecutionError;
            if (Settings?.Loaded == true) ShowReasoning = _configuredThinking;
            _connection = configuration.Connection;
            if (configuration.Messages is { Count: > 0 }) _hasConversation = true;
            if (configuration.SessionTitle is not null) _conversationTitle = configuration.SessionTitle;
            if (configuration.Messages is { } messages && !ReferenceEquals(_projectedHistory, messages))
            {
                _projectedHistory = messages;
                _responses.Clear();
                _promptTexts.Clear();
                TranscriptRevision++;
                _responseState = null;
            }
        RefreshDraftSelection();
        _dirty = true;
    }

    // Streaming/editing invalidations are coalesced by OnFrame. Modal transitions
    // explicitly render sooner because the host can dispatch several keys per frame.
    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) => callback.InvokeAsync(arg);

    private int SidePadding => _width < 44 ? 1 : 2;
    private int ComposerWidth => Math.Max(1, _composerWidth);
    private int TabHeight => _height >= 6 ? 1 : 0;

    public void OnFrame()
    {
        if (_nativePromptOwner is { } owner && _nativePrompt is { IsMounted: true, IsDisposed: false } editor) ObservePrompt(owner, editor);
        ReadObservedSession();
        if (!ReferenceEquals(_previousTheme, ThemeView)) { _previousTheme = ThemeView; ApplyHostTheme(); _dirty = true; }
        ReadSessionPresentation();
        RefreshDraftSelection();
        ReadTranscriptImageSource();
        ReadShellMode();
        ReadActivities();
        ReadTerminalSelection();
        ReadManagementContext();
        ReadSystemCatalogs();
        UpdateCommandAutocomplete();
        UpdateReferenceAutocomplete();
        var settingsPersistenceError = ReadSettingsPersistenceError?.Invoke();
        if (settingsPersistenceError != _settingsPersistenceError) { _settingsPersistenceError = settingsPersistenceError; _dirty = true; }
        ReadGlobalTabActivity();
        if (ReadActiveSessions?.Invoke() is { } active && !ReferenceEquals(active, _activeSessions))
        {
            _activeSessions = active;
            _dirty = true;
        }
        if (ReadPermissions?.Invoke() is { } permissions && !ReferenceEquals(_permissions, permissions))
        {
            _permissions = permissions;
            _dirty = true;
        }
        ReadFormState();
        ReadPermissionContext();
        var canPersist = ReadPersistentPermissionGrants?.Invoke() == true;
        if (canPersist != _canPersistAlways) { _canPersistAlways = canPersist; _dirty = true; }
        if (ReloadOnStart && !_initialReloadStarted)
        {
            _initialReloadStarted = true;
            // RunConfigurationAction tracks _configurationTask and reports errors;
            // rendering stays synchronous and its work uses the root-owned lifetime.
            _ = RunConfigurationAction(async token => { await InitializeTabs(token); await RefreshConfiguration(token); }, cancellationToken: CancellationToken.None);
        }
        if (!_dirty) return;
        _dirty = false;
        RegisterCurrentTab();
        if (_renderedTranscriptRevision != TranscriptRevision)
        {
            _transcriptMessages = TranscriptMessages.Build(TranscriptHistory, _responses, _promptTexts);
            _renderedTranscriptRevision = TranscriptRevision;
        }
        StateHasChanged();
    }

    private Task SendPermission(PermissionDecision decision, CancellationToken cancellationToken) => ReplyPermission is not null
        ? ReplyPermission(decision, cancellationToken) : throw new InvalidOperationException("Permission replies are not connected to the server.");

    private async Task RefreshPermissionState()
    {
        if (RefreshPermissions is null || _configurationLifetime.IsCancellationRequested) return;
        try { await RefreshPermissions(_configurationLifetime.Token); _permissionContextKey = null; _dirty = true; }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { ExecutionError = SessionClientAdapter.Describe(exception); _dirty = true; }
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        _dirty = true;
    }

    public void Paste(string text)
    {
        if (ActivePrompt is not { Focused: true } || PromptBlocked || PromptOverlayOpen || _activitiesOpen || _terminalFocused || _terminalListOpen) return;
        try { if (DispatchPromptPaste?.Invoke(text) != true) OnInputError("The native paste route is unavailable."); }
        catch (ArgumentException exception) { OnInputError(exception.Message); }
    }

    public void OnInputError(string message)
    {
        _inputError = message;
        _dirty = true;
    }

    // Required legacy host hook. The native textarea route consumes its own text/keys;
    // unrelated legacy controls retain their component callbacks, not a root insertion fallback.
    public void HandleKey(ConsoleKeyInfo key) { }

    public async Task StopAsync()
    {
        StopStashMutations();
        // A preview must restore through the still-live shared persistence subscription.
        if (_themeListDialog is { } themeDialog) await themeDialog.DisposeAsync();
        DisconnectThemes();
        if (Settings is not null) Settings.Changed -= SettingsChanged;
        foreach (var lease in _keyLeases) lease.Dispose();
        _keyLeases.Clear();
        _modalKeyMode?.Dispose();
        _modalKeyMode = null;
        _keyDispatcher?.ClearPending();
#pragma warning disable MA0042 // Preserve synchronous request cancellation before joining the stream/configuration shutdown tasks.
        _request?.Cancel();
#pragma warning restore MA0042
        try
        {
            await Task.WhenAll(_configurationLifetime.CancelAsync(), _stream, _configurationTask);
            await _themeUpdate;
            await _settingsUpdate;
            await Task.WhenAll(_keyTasks);
            await _stashUpdates;
            await _promptStash.FlushAsync();
            await StopTabActionsAsync();
            await _tabWrites;
        }
        finally { _configurationLifetime.Dispose(); _tabLifetime.Dispose(); }
    }

}
