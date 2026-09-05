namespace OpenCode.Cli.Tui.Components;

using OpenCode.Cli.Tui.Models;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Client;

public partial class OpenCodeApp
{
    private readonly Dictionary<(LocationRef Location, AgentId Agent), ModelRef> _homeModelChoices = [];
    private readonly Dictionary<LocationRef, AgentId> _homeAgentChoices = [];
    private readonly Dictionary<SessionId, ModelRef> _sessionModelDrafts = [];
    private readonly Dictionary<SessionId, AgentId> _sessionAgentDrafts = [];
    private readonly Dictionary<SessionId, long> _observedSelectionCommits = [];
    private readonly Dictionary<Guid, PromptSelection> _retryPromptSelections = [];
    private SessionHttpClient? _selectionObserver;
    private LocationRef? _selectionLocation;
    private LocationRef SelectionLocation => _sessionId is { } id && ReadSessionObservation?.Invoke(id)?.Session is { } session
        ? session.Location : _selectionLocation ?? _presentation?.Location ?? new LocationRef(CurrentDirectory);

    private ModelRef? CurrentModelSelection
    {
        get
        {
            if (_sessionId is { } id)
            {
                if (_sessionModelDrafts.TryGetValue(id, out var draft)) return draft;
                return ReadSessionObservation?.Invoke(id)?.Session is { } session ? session.Model : _modelSelection;
            }
            var models = _catalog?.Models ?? _presentation?.Models ?? [];
            var agent = (_catalog?.Agents ?? _presentation?.Agents ?? []).FirstOrDefault(item => item.Id == _agentSelection);
            var candidates = new[] { agent is null ? null : _homeModelChoices.GetValueOrDefault((SelectionLocation, agent.Id)), agent?.Model }
                .Concat(ModelPreferenceState?.Value.Recent ?? []).Concat([_creationFallback])
                .Concat(models.Select(model => new ModelRef(model.ProviderId.Value, model.Id.Value)));
            var selected = candidates.FirstOrDefault(candidate => candidate is not null && models.Any(model =>
                model.ProviderId.Value == candidate.ProviderId && model.Id.Value == candidate.Id));
            if (selected is null) return null;
            var variant = ModelPreferences.NormalizeVariant(ModelPreferenceState?.Value.Variant.GetValueOrDefault(ModelPreferences.Key(selected)));
            return selected with { Variant = variant is not null && models.Any(model => model.ProviderId.Value == selected.ProviderId
                && model.Id.Value == selected.Id && model.Variants.Any(item => item.Id.Value == variant)) ? variant : null };
        }
    }

    private PromptSelection CaptureSelection(SessionPromptInput input)
    {
        if (_retryPromptInputs.TryGetValue(EditorKey, out var retry) && input.Id == retry.Id && SessionClientAdapter.SamePrompt(input, retry)
            && _retryPromptSelections.TryGetValue(EditorKey, out var selection)) return selection;
        return new(SelectionLocation, _agentSelection, CurrentModelSelection);
    }

    private void ReconcileSelectionDrafts()
    {
        var client = ReadSessionClient?.Invoke();
        if (_selectionObserver != client) { _observedSelectionCommits.Clear(); _selectionObserver = client; }
        foreach (var id in _sessionEditors.Keys
            .Concat(_sessionModelDrafts.Keys).Concat(_sessionAgentDrafts.Keys).Distinct().ToArray())
        {
            foreach (var admission in (ReadSessionObservation?.Invoke(id)?.Admissions ?? []).Where(admission =>
                admission.SelectionCommitted && admission.SelectionRevision > _observedSelectionCommits.GetValueOrDefault(id)).OrderBy(admission => admission.SelectionRevision))
            {
                if (admission.Selection is not { } selection) continue;
                _observedSelectionCommits[id] = admission.SelectionRevision;
                if (_sessionModelDrafts.TryGetValue(id, out var model) && model == selection.Model) _sessionModelDrafts.Remove(id);
                if (_sessionAgentDrafts.TryGetValue(id, out var agent) && agent == selection.Agent) _sessionAgentDrafts.Remove(id);
            }
        }
    }

    private void RefreshDraftSelection()
    {
        ReconcileSelectionDrafts();
        var previous = (_agentSelection, ActiveAgent, ActiveModel, ActiveProvider, ActiveVariant);
        if (_sessionId is { } id)
        {
            var session = ReadSessionObservation?.Invoke(id)?.Session;
            _agentSelection = _sessionAgentDrafts.TryGetValue(id, out var agent) ? agent
                : session?.Agent is { } stored ? AgentId.FromExisting(stored) : _agentSelection;
        }
        else if ((_catalog?.Agents ?? _presentation?.Agents) is { } agents)
        {
            var preferred = _homeAgentChoices.TryGetValue(SelectionLocation, out var chosen)
                ? agents.FirstOrDefault(agent => agent.Id == chosen && !agent.Hidden && agent.Mode != AgentMode.Subagent) : null;
            _agentSelection = (preferred ?? agents.FirstOrDefault(agent => !agent.Hidden && agent.Mode != AgentMode.Subagent))?.Id;
        }
        var models = _catalog?.Models ?? _presentation?.Models ?? [];
        var selected = CurrentModelSelection;
        ActiveAgent = (_catalog?.Agents ?? _presentation?.Agents ?? []).FirstOrDefault(item => item.Id == _agentSelection)?.Name ?? _agentSelection?.Value;
        ActiveModel = selected is null ? null : models.FirstOrDefault(item => item.ProviderId.Value == selected.ProviderId && item.Id.Value == selected.Id)?.Name ?? selected.Id;
        ActiveProvider = selected is null ? null : (_catalog?.Providers ?? _presentation?.Providers ?? []).FirstOrDefault(item => item.Id.Value == selected.ProviderId)?.Name ?? selected.ProviderId;
        ActiveVariant = selected?.Variant;
        if (previous != (_agentSelection, ActiveAgent, ActiveModel, ActiveProvider, ActiveVariant)) _dirty = true;
    }
}
