namespace OpenCode.Cli.Tui.Components;

using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.Components;
using OpenCode.Cli.Tui.Attachments;

public partial class OpenCodeApp
{
    [Parameter] public Func<CancellationToken, Task<SessionTabLayout>>? LoadTabs { get; set; }
    [Parameter] public Func<SessionTabLayout, SessionTabLayout, CancellationToken, Task>? SaveTabs { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task<PromptConfiguration>>? OpenTabSession { get; set; }
    [Parameter] public Func<IReadOnlyDictionary<SessionId, SessionTabActivity>>? ReadTabActivity { get; set; }
    [Parameter] public Func<SessionId, double, CancellationToken, Task>? ReportSessionViewed { get; set; }
    [Parameter] public Func<IReadOnlySet<SessionId>>? ReadDeletedTabSessions { get; set; }
    [Parameter] public bool PreviewSessionTabs { get; set; }
    [Parameter] public bool SessionTabsEnabled { get; set; } = true;
    [Parameter] public SessionTabScope TabScope { get; set; } = SessionTabScope.Cwd;
    private IReadOnlyDictionary<SessionId, SessionTabActivity> _tabActivity = ImmutableDictionary<SessionId, SessionTabActivity>.Empty;
    private SessionTabState _tabs = SessionTabState.Create();
    private readonly Dictionary<Guid, TabView> _tabViews = [];
    private readonly CancellationTokenSource _tabLifetime = new();
    private Task _tabWrites = Task.CompletedTask;
    private bool _tabsLoaded;
    private bool _tabList;
    private string? _tabStorageError;
    private readonly HashSet<SessionId> _deletedTabs = [];
    private SessionTabViewTracker? _tabViewTracker;
    private bool _tabTerminalFocused;
    private bool _tabActionsStopped;
    private bool _refreshDeletedTabSelection;

    private sealed record TabView(string Input, int Cursor, int? SelectionAnchor, ImmutableArray<string> History, int HistoryIndex,
        TerminalScrollState Scroll, bool Reasoning, bool ToolDetails, bool Usage, bool Timestamps,
        ImmutableHashSet<string> ExpandedRows, ImmutableHashSet<string> CollapsedRows, string? InputError = null);

    private void CaptureTab()
    {
        CapturePromptDocument();
        TranscriptScroll.Detach();
        _tabViews[EditorKey] = new(_input, _cursor, _selectionAnchor, _history.ToImmutableArray(), _historyIndex,
            TranscriptScroll, ShowReasoning, ShowToolDetails, ShowUsage, ShowTimestamps, _expandedRows, _collapsedRows, _inputError);
        if (_sessionId is { } route) _familyRoutes[_tabs.Selected] = route;
        else _homeLocations[_tabs.Selected] = SelectionLocation;
    }

    private void RestoreTab(SessionTab tab, PromptConfiguration configuration)
    {
        if (_presentation?.Session?.Id != configuration.SessionId) _presentation = null;
        if (configuration.Directory is { } directory && directory != CurrentDirectory) _catalog = null;
        var editor = EditorFor(tab.Key, configuration.SessionId);
        _tabViews.TryGetValue(editor, out var view);
        var document = _editorDocuments.GetValueOrDefault(editor) ?? PromptDocumentAdapter.Prepare(
            new(_promptParts.GetValueOrDefault(editor) ?? new(view?.Input ?? ""), _promptMetadata.GetValueOrDefault(editor), ShellMode: _shellModes.GetValueOrDefault(editor)),
            view?.Cursor ?? 0, view?.SelectionAnchor);
        _draftRevision++;
        _inputError = view?.InputError;
        _history.Clear();
        if (view is not null) _history.AddRange(view.History);
        _historyIndex = view?.HistoryIndex ?? _history.Count;
        TranscriptScroll = view?.Scroll ?? new TerminalScrollState();
        ShowReasoning = view?.Reasoning ?? false;
        ShowToolDetails = view?.ToolDetails ?? false;
        ShowUsage = view?.Usage ?? false;
        ShowTimestamps = view?.Timestamps ?? false;
        _expandedRows = view?.ExpandedRows ?? [];
        _collapsedRows = view?.CollapsedRows ?? [];
        _responses.Clear();
        _promptTexts.Clear();
        _responseState = null;
        _projectedHistory = null;
        _transcriptMessages = [];
        _hasConversation = configuration.SessionId is not null;
        _sessionId = configuration.SessionId;
        if (_nativePromptOwner == editor && ActivePrompt is { } state) ObservePrompt(editor, state);
        else RestorePromptDocument(editor, document);
        if (_sessionId is { } route) _familyRoutes[tab.Key] = route;
        _conversationTitle = tab.Title;
        _permissions = [];
        _status = "Ready";
        TranscriptRevision++;
        ApplyConfiguration(configuration);
        ReadObservedSession(force: true);
    }

    private async Task<PromptConfiguration> LoadTab(SessionTab tab, CancellationToken cancellationToken)
    {
        if (tab.SessionId is { } session)
        {
            if (OpenTabSession is null) throw new InvalidOperationException("Session navigation is not connected to the server.");
            return await OpenTabSession(_familyRoutes.GetValueOrDefault(tab.Key, session), cancellationToken);
        }
        var location = _homeLocations.GetValueOrDefault(tab.Key) ?? SelectionLocation;
        if (OpenHomeLocation is not null) return await OpenHomeLocation(location, cancellationToken);
        if (NewConversation is not null) await NewConversation(cancellationToken);
        return ReloadConfiguration is not null ? await ReloadConfiguration(cancellationToken) : new(null, null, null, null);
    }

    private Task SelectTab(Guid key) => RunTabNavigation(async token =>
    {
        var tab = _tabs.Tabs.FirstOrDefault(tab => tab.Key == key) ?? throw new InvalidOperationException("The tab is no longer open.");
        if (key == _tabs.Selected) { CloseDialog(); return; }
        var configuration = await LoadTab(tab, token);
        token.ThrowIfCancellationRequested();
        CaptureTab();
        _tabs = _tabs.Select(key);
        RestoreTab(tab, configuration);
        UpdateViewedTab();
        CloseTabMenu();
        CloseDialog();
    }, ct: CancellationToken.None);

    private Task NewTab() => RunTabNavigation(async token =>
    {
        var tab = _tabs.Tabs.FirstOrDefault(tab => tab.SessionId is null) ?? new SessionTab(Guid.NewGuid(), null, "New session");
        var configuration = await LoadTab(tab, token);
        token.ThrowIfCancellationRequested();
        CaptureTab();
        if (!_tabs.Tabs.Contains(tab)) _tabs = _tabs with { Tabs = _tabs.Tabs.Add(tab) };
        _tabs = _tabs.Select(tab.Key);
        RestoreTab(tab, configuration);
        UpdateViewedTab();
        CloseTabMenu();
        CloseDialog();
    }, ct: CancellationToken.None);

    private Task CloseTab(Guid key) => RunTabNavigation(async token =>
    {
        var before = _tabs.Persisted;
        var next = _tabs.Close(key);
        PromptConfiguration? configuration = null;
        if (key == _tabs.Selected) configuration = await LoadTab(next.Current, token);
        token.ThrowIfCancellationRequested();
        CaptureTab();
        _tabs = next;
        TrimEditorRoutes();
        if (configuration is not null) RestoreTab(next.Current, configuration);
        QueueTabWrite(before, next.Persisted);
        UpdateViewedTab();
    }, ct: CancellationToken.None);

    private Task ReopenTab() => RunTabNavigation(async token =>
    {
        var next = _tabs.Reopen();
        if (next.Selected == _tabs.Selected && next.Tabs.SequenceEqual(_tabs.Tabs)) { _tabs = next; return; }
        var configuration = await LoadTab(next.Current, token);
        token.ThrowIfCancellationRequested();
        var before = _tabs.Persisted;
        CaptureTab();
        _tabs = next;
        RestoreTab(next.Current, configuration);
        QueueTabWrite(before, _tabs.Persisted);
        UpdateViewedTab();
        CloseDialog();
    }, ct: CancellationToken.None);

    private async Task InitializeTabs(CancellationToken cancellationToken)
    {
        if (_tabsLoaded) return;
        _tabsLoaded = true;
        _tabPickerAllProjects = TabScope != SessionTabScope.Cwd;
        if (LoadTabs is null) return;
        try
        {
            var stored = await LoadTabs(cancellationToken);
            _tabs = _tabs with { Tabs = stored.Tabs.Where(tab => !_deletedTabs.Contains(tab.SessionId)
                    && !_tabs.Tabs.Any(open => open.SessionId == tab.SessionId))
                .Select(tab => new SessionTab(Guid.NewGuid(), tab.SessionId, tab.Title ?? "Untitled session"))
                .Concat(_tabs.Tabs).ToImmutableArray() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { _tabStorageError = $"Could not load tabs: {exception.Message}"; }
    }

    private void RegisterCurrentTab()
    {
        if (_tabActionsStopped || _configurationBusy || !_hasConversation || _sessionId is not { } routed) return;
        var metadata = TabMetadata();
        var id = TabRoot(routed, metadata);
        if (_deletedTabs.Contains(id)) return;
        var title = id == routed ? _conversationTitle : metadata.GetValueOrDefault(id)?.Title ?? _tabs.Current.Title;
        var current = _tabs.Current;
        if (current.SessionId == id && current.Title == title) return;
        var before = _tabs.Persisted;
        _tabs = _tabs.Register(id, title);
        QueueTabWrite(before, _tabs.Persisted);
    }

    private void ReadGlobalTabActivity()
    {
        if (_tabActionsStopped) return;
        ReadRecoveryState();
        ReadObservedSession();
        var raw = ReadTabActivity?.Invoke() ?? ImmutableDictionary<SessionId, SessionTabActivity>.Empty;
        var metadata = TabMetadata();
        var permissions = ReadPermissions?.Invoke() ?? [];
        var forms = ReadForms?.Invoke();
        var active = ReadActiveSessions?.Invoke() ?? _activeSessions;
        var activity = raw.ToImmutableDictionary();
        foreach (var tab in _tabs.Tabs.Where(tab => tab.SessionId is not null))
        {
            var id = tab.SessionId!.Value;
            var root = TabRoot(id, metadata);
            var members = metadata.Keys.Where(member => TabRoot(member, metadata) == root).Append(root).ToHashSet();
            if (forms?.Session == root) members.UnionWith(forms.Descendants);
            var status = raw.GetValueOrDefault(root) ?? new SessionTabActivity();
            var busy = members.Any(member => raw.GetValueOrDefault(member)?.Busy ?? active.Contains(member));
            var permission = permissions.Any(request => members.Contains(request.SessionId));
            var question = forms?.Session == root && forms.Pending.Any(item => members.Any(member => member.Value == item.Form.SessionId));
            var liveFamily = _sessionId is { } routed && TabRoot(routed, metadata) == root && ReadPermissions is not null && forms?.Session == root;
            var attention = permission ? SessionTabAttention.Permission : question ? SessionTabAttention.Question : liveFamily ? null
                : members.Select(member => raw.GetValueOrDefault(member)?.Attention).FirstOrDefault(value => value == SessionTabAttention.Permission)
                    ?? members.Select(member => raw.GetValueOrDefault(member)?.Attention).FirstOrDefault(value => value is not null);
            var unread = status.Unread;
            if (metadata.TryGetValue(root, out var info))
                unread = info.Time.Idle is { } idle && (info.Time.Viewed is null || idle > info.Time.Viewed)
                    ? info.Outcome == SessionOutcome.Failed ? SessionTabUnread.Error : SessionTabUnread.Activity : null;
            activity = activity.SetItem(root, status with { Busy = busy, Attention = attention, Unread = unread,
                Title = status.Title ?? metadata.GetValueOrDefault(root)?.Title });
        }
        var activityChanged = activity.Count != _tabActivity.Count || activity.Any(pair => !_tabActivity.TryGetValue(pair.Key, out var value) || value != pair.Value);
        if (activityChanged) { _tabActivity = activity; _dirty = true; }
        var before = _tabs.Persisted;
        _tabs = _tabs.Normalize(metadata);
        TrimEditorRoutes();
        _tabs = _tabs with { Tabs = _tabs.Tabs.Select(tab => tab.SessionId is { } id && activity.TryGetValue(id, out var item) && item.Title is { } title
            ? tab with { Title = title } : tab).ToImmutableArray() };
        if (_tabs.Current.SessionId == _sessionId && _conversationTitle != _tabs.Current.Title) { _conversationTitle = _tabs.Current.Title; _dirty = true; }
        QueueTabWrite(before, _tabs.Persisted);
        if (!before.Tabs.SequenceEqual(_tabs.Persisted.Tabs)) _dirty = true;
        UpdateViewedTab(metadata);
        if (ReadDeletedTabSessions?.Invoke() is { } deleted)
            foreach (var id in deleted.Where(id => !_deletedTabs.Contains(id)).ToArray())
                _keyTasks.Add(PruneDeletedTabSession(id));
        if (_refreshDeletedTabSelection && TabNavigationReady) _keyTasks.Add(RefreshDeletedTabSelection());
    }

    private void QueueTabWrite(SessionTabLayout before, SessionTabLayout after)
    {
        if (_tabActionsStopped || SaveTabs is null || before.Tabs.SequenceEqual(after.Tabs)) return;
        var previous = _tabWrites;
        _tabWrites = Write();
        async Task Write()
        {
            try { await previous; await SaveTabs(before, after, _tabLifetime.Token); }
            catch (OperationCanceledException) when (_tabLifetime.IsCancellationRequested) { }
            catch (Exception exception) { _tabStorageError = $"Could not save tabs: {exception.Message}"; _dirty = true; }
        }
    }

}
