namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    [Parameter] public Func<LocationRef, CancellationToken, Task<PromptConfiguration>>? OpenHomeLocation { get; set; }
    private readonly Dictionary<(Guid Tab, SessionId? Session), Guid> _editorRoutes = [];
    private readonly Dictionary<SessionId, Guid> _sessionEditors = [];
    private readonly Dictionary<Guid, SessionId> _familyRoutes = [];
    private readonly Dictionary<Guid, LocationRef> _homeLocations = [];
    private readonly Dictionary<SessionId, SessionInfo> _navigationSessions = [];
    private Guid EditorKey => EditorFor(_tabs.Selected, _sessionId);

    private Guid EditorFor(Guid tab, SessionId? session)
    {
        if (_editorRoutes.TryGetValue((tab, session), out var key)) return key;
        // Source draft-stash is Session-owned, including after a preview tab is replaced.
        if (session is { } id)
        {
            if (!_sessionEditors.TryGetValue(id, out key)) _sessionEditors[id] = key = Guid.NewGuid();
            return _editorRoutes[(tab, session)] = key;
        }
        return _editorRoutes[(tab, session)] = Guid.NewGuid();
    }

    private void AdoptEditorSession(Guid tab, Guid editor, SessionId session, PromptSelection? creation = null)
    {
        if (_deletedTabs.Contains(session)) return;
        var visible = EditorKey == editor;
        var agent = visible ? _agentSelection : creation is null ? null
            : _homeAgentChoices.TryGetValue(creation.Location, out var chosen) ? chosen : creation.Agent;
        var model = visible ? CurrentModelSelection : creation is not null && agent is { } selected
            ? _homeModelChoices.GetValueOrDefault((creation.Location, selected)) ?? creation.Model : creation?.Model;
        if (agent is { } initialAgent) _sessionAgentDrafts.TryAdd(session, initialAgent);
        if (model is not null) _sessionModelDrafts.TryAdd(session, model);
        _sessionEditors[session] = editor;
        if (!_tabs.Tabs.Concat(_tabs.Closed).Any(item => item.Key == tab)) return;
        _editorRoutes[(tab, session)] = editor;
        _familyRoutes[tab] = session;
    }

    private bool RetainsEditor(Guid editor) => _sessionEditors.Any(pair => pair.Value == editor && !_deletedTabs.Contains(pair.Key)) || _editorRoutes.Any(pair => pair.Value == editor &&
        _tabs.Tabs.Concat(_tabs.Closed).Any(tab => tab.Key == pair.Key.Tab) && (pair.Key.Session is null || !_deletedTabs.Contains(pair.Key.Session.Value)));

    private void TrimEditorRoutes()
    {
        var tabs = _tabs.Tabs.Concat(_tabs.Closed).Select(tab => tab.Key).ToHashSet();
        foreach (var route in _editorRoutes.Keys.Where(route => !tabs.Contains(route.Tab) || route.Session is { } id && _deletedTabs.Contains(id)).ToArray())
            _editorRoutes.Remove(route);
        foreach (var id in _sessionEditors.Keys.Where(_deletedTabs.Contains).ToArray()) _sessionEditors.Remove(id);
        var editors = _editorRoutes.Values.Concat(_sessionEditors.Values).Concat(_commandAdmissions).Concat(_shellPreparing)
            .Concat(_originRequests.Values.Select(origin => origin.Key)).ToHashSet();
        foreach (var key in _tabViews.Keys.Where(key => !editors.Contains(key)).ToArray()) _tabViews.Remove(key);
        TrimPromptDocuments(editors);
        foreach (var key in _familyRoutes.Keys.Where(key => !tabs.Contains(key) || _deletedTabs.Contains(_familyRoutes[key])).ToArray()) _familyRoutes.Remove(key);
        foreach (var key in _homeLocations.Keys.Where(key => !tabs.Contains(key)).ToArray()) _homeLocations.Remove(key);
        foreach (var id in _sessionModelDrafts.Keys.Concat(_sessionAgentDrafts.Keys).Distinct().Where(_deletedTabs.Contains).ToArray())
        { _sessionModelDrafts.Remove(id); _sessionAgentDrafts.Remove(id); }
        foreach (var id in _navigationSessions.Keys.Where(_deletedTabs.Contains).ToArray()) _navigationSessions.Remove(id);
        foreach (var id in _observedSelectionCommits.Keys.Where(_deletedTabs.Contains).ToArray()) _observedSelectionCommits.Remove(id);
    }
}
