namespace OpenCode.Cli.Tui.Components;

using System.Net;
using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Cli.Tui.Activities;
using OpenCode.Cli.Tui.Keymap;
using OpenCode.Schema;
using OpenCode.Protocol.Groups;
using OpenTui.Blazor;
using OpenTui.Blazor.Keymap;

public partial class OpenCodeApp
{
    [Parameter] public Func<ActivityFeedSnapshot?>? ReadActivityFeed { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task<IReadOnlyList<SessionInfo>>>? LoadActivityFamily { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task<SessionObservationSnapshot>>? ObserveActivitySession { get; set; }
    [Parameter] public Action<LocatedShell, long?>? MergeActivityShell { get; set; }
    [Parameter] public Action<LocationRef, ShellId, long?>? RemoveActivityShell { get; set; }
    private bool _activitiesOpen;
    private ActivityTab _activityTab;
    private SessionId? _activitySession;
    private SessionHttpClient? _activityClient;
    private SessionActivities? _activities;
    private ActivityFeedSnapshot? _activityFeed;
    private IReadOnlyList<SessionInfo>? _activityFamily;
    private IReadOnlyDictionary<string, SessionActive>? _activityActive;
    private bool _activityFamilyComplete;
    private bool _activitiesLoading;
    private bool _activitiesRefreshAgain;
    private bool _activityShellsLoaded;
    private string? _activitySessionError;
    private string? _activityShellError;
    private IDisposable? _activityMode;
    private bool _activityPromptFocus;
    private SessionId? _activityRoute;
    private bool _activityChildPending;
    private IReadOnlyDictionary<SessionId, SessionObservationSnapshot> _activityObservations = new Dictionary<SessionId, SessionObservationSnapshot>();
    private readonly HashSet<(SessionHttpClient Client, LocationRef Location, ShellId Id)> _shellReads = [];
    private readonly Dictionary<(LocationRef Location, ShellId Id), long> _shellReadFailures = [];

    private IReadOnlyList<LocatedShell>? ActivityShells => _activityShellsLoaded || (_activityFeed?.Shells.Count ?? 0) > 0
        ? (_activityFeed?.Shells ?? []).Where(shell => _activitySession is { } id && ActivityProjection.BelongsTo(shell.Info, id)).ToArray() : null;
    private IReadOnlyList<SessionInfo>? ActivityFamily => _activityFamily?.Select(info => ReadSessionObservation?.Invoke(info.Id)?.Session ?? info).ToArray();
    private IReadOnlyDictionary<SessionId, IReadOnlyList<SessionMessage>>? ActivityMessages => _activityFamily?
        .Select(info => ReadSessionObservation?.Invoke(info.Id)).Where(snapshot => snapshot is not null)
        .ToDictionary(snapshot => snapshot!.SessionId, snapshot => snapshot!.Messages);

    private async Task OpenActivities(ActivityTab tab)
    {
        if (_sessionId is not { } id || RequireSessionClient is null) return;
        CloseDialog();
        FocusSessionPane();
        _activitySession = id;
        _activityTab = tab;
        _activitiesOpen = true;
        _activityMode = _keyMode.Push("composer");
        _activityFamily = null;
        _activityActive = null;
        _activityFamilyComplete = false;
        _activityShellsLoaded = false;
        _activitySessionError = _activityShellError = null;
        _dirty = true;
        try { _activityClient = await RequireSessionClient(_configurationLifetime.Token); await RefreshActivities(); }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _activitySessionError = SessionClientAdapter.Describe(exception); _dirty = true; }
    }

    private void CloseActivities()
    {
        if (!_activitiesOpen) return;
        _activitiesOpen = false;
        _activityPromptFocus = true;
        _activityMode?.Dispose();
        _activityMode = null;
        _dirty = true;
        FocusSessionPane();
    }

    private Task CloseActivityPanel() => _childSession && ReadSessionObservation?.Invoke(_sessionId!.Value)?.Session?.ParentId is { } parent
        ? OpenActivitySession(parent) : CloseRootActivities();

    private Task CloseRootActivities() { CloseActivities(); return Task.CompletedTask; }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (_activityPromptFocus && !_activitiesOpen && !PromptOverlayOpen && !_terminalListOpen)
        { _activityPromptFocus = false; FocusSessionPane(); }
        return Task.CompletedTask;
    }

    private async Task RefreshActivities()
    {
        if (_activitiesLoading) { _activitiesRefreshAgain = true; return; }
        if (_activitySession is not { } id || _activityClient is not { } client) return;
        _activitiesLoading = true;
        try
        {
            do
            {
                _activitiesRefreshAgain = false;
                try
                {
                    if (LoadActivityFamily is null) throw new InvalidOperationException("Session-family loading is not connected.");
                    var family = await LoadActivityFamily(id, _configurationLifetime.Token);
                    var active = await client.ActiveAsync(_configurationLifetime.Token);
                    if (_activitySession != id || _activityClient != client) return;
                    _activityFamily = family;
                    foreach (var member in family) _navigationSessions[member.Id] = member;
                    _activityFamilyComplete = true;
                    _activityActive = active.Data;
                    _activitySessionError = null;
                }
                catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { throw; }
                catch (Exception exception) { _activitySessionError = SessionClientAdapter.Describe(exception); _activityFamilyComplete = false; }
                try
                {
                    var location = ReadSessionObservation?.Invoke(id)?.Session?.Location
                        ?? _activityFamily?.FirstOrDefault(session => session.Id == id)?.Location
                        ?? throw new InvalidOperationException("The activity Session location is unavailable.");
                    var version = ReadActivityFeed?.Invoke()?.Revision;
                    var shells = await client.ListShellsAsync(location.Directory, location.WorkspaceId?.Value, _configurationLifetime.Token);
                    var execution = new LocationRef(shells.Location.Directory, shells.Location.WorkspaceId);
                    if (_activitySession != id || _activityClient != client) return;
                    foreach (var shell in shells.Data) MergeActivityShell?.Invoke(new(shell, execution), version);
                    foreach (var retained in (ReadActivityFeed?.Invoke()?.Shells ?? []).Where(shell => ActivityProjection.BelongsTo(shell.Info, id)
                        && shell.Info.Status == ShellStatus.Running && (shell.Location != execution || !shells.Data.Any(item => item.Id == shell.Info.Id))))
                        await RefreshActivityShell(client, new(retained.Location, retained.Info.Id, version ?? 0));
                    _activityShellsLoaded = true;
                    _activityShellError = null;
                    _shellReadFailures.Clear();
                }
                catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { throw; }
                catch (Exception exception) { _activityShellError = SessionClientAdapter.Describe(exception); }
                _dirty = true;
            } while (_activitiesRefreshAgain && _activitiesOpen && _activitySession == id);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        finally
        {
            _activitiesLoading = false;
            _dirty = true;
            if (_activitiesRefreshAgain && _activitiesOpen && (_activitySession != id || _activityClient != client)) _keyTasks.Add(RefreshActivities());
        }
    }

    private void ReadActivities()
    {
        if (_activitiesOpen && (_activitySession != _sessionId || _activityClient is not null && ReadSessionClient?.Invoke() != _activityClient)) CloseActivities();
        if (_activityRoute != _sessionId)
        {
            _activityRoute = _sessionId;
            _activityChildPending = _childSession;
        }
        if (_activityChildPending && !PromptBlocked && !_terminalFocused)
        { _activityChildPending = false; _keyTasks.Add(OpenActivities(ActivityTab.Subagents)); }
        if (_activitiesOpen)
        {
            var observations = (_activityFamily ?? []).Select(info => ReadSessionObservation?.Invoke(info.Id))
                .OfType<SessionObservationSnapshot>().ToDictionary(snapshot => snapshot.SessionId);
            if (observations.Count != _activityObservations.Count || observations.Any(pair => !ReferenceEquals(_activityObservations.GetValueOrDefault(pair.Key), pair.Value)))
            { _activityObservations = observations; _dirty = true; }
        }
        var feed = ReadActivityFeed?.Invoke();
        if (feed is null) return;
        if (!ReferenceEquals(feed, _activityFeed))
        {
            var previous = _activityFeed;
            _activityFeed = feed;
            if (_activitiesOpen && previous?.SessionsRevision != feed.SessionsRevision) _keyTasks.Add(RefreshActivities());
            if (_activitiesOpen && previous?.ShellEventsRevision != feed.ShellEventsRevision && _activities is { } panel)
                _keyTasks.Add(RefreshActivityOutput(panel));
            _dirty = true;
        }
        if (ReadSessionClient?.Invoke() is not { } client) return;
        foreach (var request in feed.Refresh)
        {
            var key = (request.Location, request.Id);
            if (_shellReadFailures.GetValueOrDefault(key, -1) == request.Revision || !_shellReads.Add((client, request.Location, request.Id))) continue;
            _keyTasks.Add(RefreshActivityShell(client, request));
        }
    }

    private async Task RefreshActivityShell(SessionHttpClient client, ShellActivityRefresh request)
    {
        try
        {
            var shell = await client.GetShellAsync(request.Id, request.Location.Directory, request.Location.WorkspaceId?.Value, _configurationLifetime.Token);
            var location = new LocationRef(shell.Location.Directory, shell.Location.WorkspaceId);
            if (location != request.Location) throw new InvalidOperationException("Shell refresh resolved a different execution Location.");
            if (ReadSessionClient?.Invoke() == client) MergeActivityShell?.Invoke(new(shell.Data, location), request.Revision);
        }
        catch (SessionApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        { if (ReadSessionClient?.Invoke() == client) RemoveActivityShell?.Invoke(request.Location, request.Id, request.Revision); }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _shellReadFailures[(request.Location, request.Id)] = request.Revision;
            _activityShellError = SessionClientAdapter.Describe(exception);
        }
        finally { _shellReads.Remove((client, request.Location, request.Id)); _dirty = true; }
    }

    private async Task RefreshActivityOutput(SessionActivities panel)
    {
        try { await panel.RefreshOutputAsync(); }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested || !_activitiesOpen) { }
        catch (Exception exception) { _activityShellError = SessionClientAdapter.Describe(exception); _dirty = true; }
    }

    private Task OpenActivitySession(SessionId id) => RunTabNavigation(async token =>
    {
        if (OpenTabSession is null) throw new InvalidOperationException("Session navigation is not connected.");
        if (LoadActivityFamily is not null)
            foreach (var member in await LoadActivityFamily(id, token)) _navigationSessions[member.Id] = member;
        var configuration = await OpenTabSession(id, token);
        if (configuration.SessionId != id) throw new InvalidOperationException("Session navigation returned a different Session.");
        CaptureTab();
        RestoreTab(_tabs.Current, configuration);
        CloseActivities();
    }, throwErrors: true, ct: CancellationToken.None);

    private async Task RequestActivityMessages(SessionId id)
    {
        if (ObserveActivitySession is null) return;
        try
        {
            var snapshot = await ObserveActivitySession(id, _configurationLifetime.Token);
            if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
        }
        catch (Exception exception) { _activitySessionError = SessionClientAdapter.Describe(exception); }
        _dirty = true;
    }
    private void MergeObservedShell(LocatedShell shell) => MergeActivityShell?.Invoke(shell, null);
    private void RemoveObservedShell(LocatedShell shell) => RemoveActivityShell?.Invoke(shell.Location, shell.Info.Id, null);
    private void FocusActivity(string key) => FocusComponent?.Invoke(key);

    private string? ResolveComposerCommand(ConsoleKeyInfo key)
    {
        if (KeyName(key) is not { } name || _resolvedBindings is null) return null;
        var stroke = new KeyStroke(name, key.Modifiers.HasFlag(ConsoleModifiers.Control), key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt));
        var prefix = Equals(_lastKeyContext.Data.GetValueOrDefault("terminal.focusKey"), "activity-subagents") ? "composer.subagent." : "composer.shell.";
        // Source binds this toggle literally, outside the configurable command inventory.
        if (prefix == "composer.subagent." && stroke == new KeyStroke("a", ctrl: true)) return "composer.subagent.toggle-activity";
        return new[] { "composer.shell.up", "composer.shell.down", "composer.shell.kill", "composer.subagent.up", "composer.subagent.down",
            "composer.subagent.select", "composer.subagent.interrupt", "composer.subagent.toggle-activity" }
            .FirstOrDefault(id => id.StartsWith(prefix, StringComparison.Ordinal) && _resolvedBindings.Get(id).Any(binding => binding.Sequence.Count == 1 && binding.Sequence[0].Stroke == stroke));
    }
}
