namespace OpenCode.Cli.Tui.Components;

using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Sessions;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Schema;
using OpenTui.Blazor;

public partial class OpenCodeApp
{
    private SessionTabContextRequest? _tabMenuState;
    private IReadOnlyList<SessionTabMenuAction> _tabMenuActions = [];
    private SessionTabContextMenu? _tabMenu;
    private IDisposable? _tabMenuMode;
    private SessionTab? _tabRename;
    private bool _tabPickerAllProjects;
    private bool TabNavigationReady => !_configurationBusy && !_tabActionsStopped;

    private bool TabKey(Func<Task> action)
    {
        if (!TabNavigationReady) return false;
        var task = action();
        if (task.IsCompleted) task.GetAwaiter().GetResult();
        else _keyTasks.Add(task);
        return true;
    }

    // Uses the existing configuration operation ownership so shutdown and other configuration
    // actions still join it. Model execution is NOT a reason to silently ignore tab navigation.
    // The adapter must reject unsupported background observation before changing its active route.
    private Task RunTabNavigation(Func<CancellationToken, Task> action, bool throwErrors = false, CancellationToken ct = default)
    {
        if (!TabNavigationReady)
        {
            if (throwErrors) throw new InvalidOperationException("Another navigation/configuration operation is in progress.");
            _inputError = "Another navigation/configuration operation is in progress.";
            _dirty = true;
            return Task.CompletedTask;
        }
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_configurationLifetime.Token, _tabLifetime.Token, ct);
        _configurationOperation = operation;
        _configurationBusy = _dirty = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _configurationTask = done.Task;
        return Run();

        async Task Run()
        {
            try { await action(operation.Token); }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { if (throwErrors) throw; }
            catch (Exception error)
            {
                if (throwErrors) throw;
                _inputError = SessionClientAdapter.Describe(error);
            }
            finally
            {
                _configurationOperation = null;
                _configurationBusy = false;
                _dirty = true;
                operation.Dispose();
                done.TrySetResult();
            }
        }
    }

    private Task<SessionPickerPage> LoadTabSessionPage(SessionPickerQuery query, CancellationToken ct) =>
        LoadSessions is not null ? LoadSessions(query, ct) : throw new InvalidOperationException("Session list HTTP callback is not connected.");

    // Replaces the old ChooseSession markup binding. It keeps the existing _tabs/_tabViews and
    // applies source preview replacement only after the real adapter has opened the target.
    private Task ChooseTabSession(SessionInfo session, CancellationToken ct) => RunTabNavigation(async token =>
    {
        if (OpenSession is null) throw new InvalidOperationException("Session navigation HTTP callback is not connected.");
        var metadata = TabMetadata();
        var root = TabRoot(session.Id, metadata);
        var target = metadata.GetValueOrDefault(root) ?? session;
        var configuration = await OpenSession(target, token);
        token.ThrowIfCancellationRequested();
        var before = _tabs.Persisted;
        CaptureTab();
        _deletedTabs.Remove(target.Id);
        _tabs = _tabs.Open(target.Id, configuration.SessionTitle ?? target.Title ?? SessionPickerRow.From(target, false, Clock.GetLocalNow()).Title,
            preview: SessionTabsEnabled && PreviewSessionTabs);
        RestoreTab(_tabs.Current, configuration);
        TrimTabViews();
        QueueTabWrite(before, _tabs.Persisted);
        UpdateViewedTab();
        CloseDialog();
    }, true, ct);

    private Task CreateTabSessionFromPicker(CancellationToken ct) => RunTabNavigation(async token =>
    {
        if (CreateSession is null) throw new InvalidOperationException("Session creation HTTP callback is not connected.");
        var configuration = await CreateSession(token);
        token.ThrowIfCancellationRequested();
        if (configuration.SessionId is not { } id) throw new InvalidOperationException("Session creation returned no Session ID.");
        var before = _tabs.Persisted;
        CaptureTab();
        _tabs = _tabs.Open(id, configuration.SessionTitle ?? "New session");
        RestoreTab(_tabs.Current, configuration);
        QueueTabWrite(before, _tabs.Persisted);
        UpdateViewedTab();
        CloseDialog();
    }, true, ct);

    private async Task RenameTabSession(SessionId id, string title, CancellationToken ct)
    {
        if (RenameSession is null) throw new InvalidOperationException("Session rename HTTP callback is not connected.");
        await RenameSession(id, title, ct);
        var before = _tabs.Persisted;
        _tabs = _tabs with { Tabs = _tabs.Tabs.Select(tab => tab.SessionId == id ? tab with { Title = title } : tab).ToImmutableArray() };
        if (_sessionId == id) _conversationTitle = title;
        QueueTabWrite(before, _tabs.Persisted);
        _dirty = true;
    }

    private Task PruneDeletedTabSession(SessionId id)
    {
        var metadata = TabMetadata();
        var family = metadata.Keys.Where(member => IsDescendantOf(member, id, metadata)).Append(id).ToHashSet();
        family.UnionWith(_deletedTabs.Where(member => IsDescendantOf(member, id, metadata)));
        _deletedTabs.UnionWith(family);
        var before = _tabs.Persisted;
        var activeDeleted = _sessionId is { } active && family.Contains(active);
        foreach (var deleted in family) _tabs = _tabs.RemoveDeleted(deleted);
        TrimTabViews();
        QueueTabWrite(before, _tabs.Persisted);
        if (_tabMenuState?.Tab?.SessionId is { } menu && family.Contains(menu)) CloseTabMenu();
        if (_tabRename?.SessionId is { } rename && family.Contains(rename)) CloseTabRename();
        _dirty = true;
        if (!activeDeleted) return Task.CompletedTask;
        // The deletion is already committed. Never put it in Closed or resurrect its tab when
        // an adapter refresh fails. Load the actual next view without interrupting any observation.
        _refreshDeletedTabSelection = true;
        return TabNavigationReady ? RefreshDeletedTabSelection() : Task.CompletedTask;
    }

    private Task RefreshDeletedTabSelection()
    {
        _refreshDeletedTabSelection = false;
        if (_sessionId is not { } selected || !_deletedTabs.Contains(selected)) return Task.CompletedTask;
        return RunTabNavigation(async token =>
        {
            var next = _tabs.Current;
            var configuration = await LoadTab(next, token);
            token.ThrowIfCancellationRequested();
            RestoreTab(next, configuration);
            UpdateViewedTab();
        }, ct: CancellationToken.None);
    }

    private static bool IsDescendantOf(SessionId member, SessionId parent, IReadOnlyDictionary<SessionId, SessionInfo> metadata)
    {
        var seen = new HashSet<SessionId>();
        while (seen.Add(member))
        {
            if (member == parent) return true;
            if (!metadata.TryGetValue(member, out var info) || info.ParentId is not { } next) return false;
            member = next;
        }
        return false;
    }

    private void TrimTabViews()
    {
        var retained = _tabs.Tabs.Concat(_tabs.Closed).Select(tab => tab.Key).ToHashSet();
        foreach (var key in _tabViews.Keys.Where(key => !retained.Contains(key)).ToArray()) _tabViews.Remove(key);
        TrimPromptDocuments(retained);
    }

    private IReadOnlyDictionary<SessionId, SessionInfo> TabMetadata()
    {
        var cache = ReadSessionCache?.Invoke() ?? [];
        var selected = ReadPresentation?.Invoke()?.Session;
        var live = _tabs.Tabs.Select(tab => tab.SessionId).Append(_sessionId).OfType<SessionId>().Distinct()
            .Select(id => ReadSessionObservation?.Invoke(id)?.Session).OfType<SessionInfo>();
        // Session observations own current watermarks. A picker page or captured readiness
        // presentation must not overwrite a newer viewed/idle state with its older snapshot.
        return (selected is null ? cache : cache.Append(selected)).Concat(live).GroupBy(session => session.Id)
            .ToDictionary(group => group.Key, group => group.Last());
    }

    private static SessionId TabRoot(SessionId id, IReadOnlyDictionary<SessionId, SessionInfo> metadata)
    {
        var seen = new HashSet<SessionId>();
        while (metadata.TryGetValue(id, out var session) && session.ParentId is { } parent && seen.Add(id)) id = parent;
        return id;
    }

    private void UpdateViewedTab(IReadOnlyDictionary<SessionId, SessionInfo>? metadata = null)
    {
        var report = ReportSessionViewed;
        if (_tabActionsStopped || report is null) return;
        _tabViewTracker ??= new(report, Clock);
        metadata ??= TabMetadata();
        var root = _sessionId is { } id && !_deletedTabs.Contains(id) ? metadata.GetValueOrDefault(TabRoot(id, metadata)) : null;
        _tabViewTracker.Update(root, _tabTerminalFocused);
    }

    public void OnTerminalFocusChanged(bool focused)
    {
        _tabTerminalFocused = focused;
        UpdateViewedTab();
    }

    // Call for a real key event, not a frame tick: source useKeyboard establishes focus when
    // terminal focus reporting is unavailable. An open modal still views the underlying Session.
    private void NoteTabKeyboardFocus() { _tabTerminalFocused = true; UpdateViewedTab(); }

    private Task OpenTabMenu(SessionTabContextRequest request)
    {
        CloseDialog();
        CloseTabMenu();
        _tabMenuState = request;
        _tabMenuActions = SessionTabMenu.Actions(request, CloseTab, NewTab,
            RenameSession is null ? null : BeginTabRename, key => { PromoteTab(key); return Task.CompletedTask; });
        _tabMenuMode = _keyMode.Push("menu");
        _dirty = true;
        return Task.CompletedTask;
    }

    private void CloseTabMenu()
    {
        _tabMenuState = null;
        _tabMenuMode?.Dispose();
        _tabMenuMode = null;
        _dirty = true;
    }

    private void DismissTabMenu(TerminalPointerEventArgs args) { args.Handled = true; CloseTabMenu(); }

    private bool HandleTabMenuKey(ConsoleKeyInfo key)
    {
        if (_tabMenuState is null) return false;
        if (_tabMenu is { } menu) _keyTasks.Add(menu.HandleKey(key));
        else if (key.Key == ConsoleKey.Escape) CloseTabMenu();
        return true;
    }

    private Task BeginTabRename(SessionTab tab)
    {
        if (tab.SessionId is null || RenameSession is null) return Task.CompletedTask;
        CloseDialog();
        CloseTabMenu();
        _tabRename = _tabs.Tabs.FirstOrDefault(item => item.Key == tab.Key) ?? tab;
        _dirty = true;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void CloseTabRename() { _tabRename = null; _dirty = true; }
    private void SetTabPickerScope(bool allProjects) { _tabPickerAllProjects = allProjects; _dirty = true; }
    private Task SelectAdjacentTab(int direction, bool unread = false)
    {
        var tab = unread ? _tabs.CycleUnread(direction, _tabActivity) : _tabs.Cycle(direction);
        return tab is null ? Task.CompletedTask : SelectTab(tab.Key);
    }

    private async Task StopTabActionsAsync()
    {
        if (_tabActionsStopped) return;
        _tabActionsStopped = true;
        CloseTabMenu();
        await StopRecoveryAsync();
        if (_tabViewTracker is not null) await _tabViewTracker.DisposeAsync();
        await _configurationTask;
        await _tabWrites;
        await _tabLifetime.CancelAsync();
    }
}
