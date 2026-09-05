namespace OpenCode.Core.Session;

using OpenCode.Core.Agent;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Llm;
using OpenCode.Schema;

public sealed class SessionMutationNotFoundException(SessionId sessionId) : Exception("Session not found.")
{
    public SessionId SessionId { get; } = sessionId;
}

public sealed class SessionSelectionException(string message, string field) : ArgumentException(message)
{
    public string Field { get; } = field;
}

/// <summary>Canonical mutations from session/session.ts, with native configuration/catalog validation before selection.</summary>
public sealed class SessionMutations(IDatabase database, SessionStore store, ProviderResolver providers,
    SessionExecutionEngine execution, SessionEnvironment environments)
{
    public async Task RemoveAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var projector = new SessionMutationProjector(database);
        // Unsupported related domains must be rejected before interrupting execution.
        await projector.RequireRemovalReadyAsync(sessionId, ct).ConfigureAwait(false);
        await using var reservation = (await execution.ReserveRemovalAsync(sessionId, ct).ConfigureAwait(false)).ConfigureAwait(false);
        // Core has settled prior ownership and blocks successors until cleanup ends.
        // RemoveAsync rechecks domain constraints inside its deletion transaction.
        await projector.RemoveAsync(sessionId, ct).ConfigureAwait(false);
        environments.Clear(sessionId);
    }

    public Task RenameAsync(SessionId sessionId, string title, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        return new SessionMutationProjector(database).PublishAsync(sessionId, SessionMutationProjector.Renamed,
            _ => new SessionRenameData(sessionId, title), ct);
    }

    public Task ViewAsync(SessionId sessionId, long idle, CancellationToken ct = default)
    {
        if (idle is < 0 or > 9_007_199_254_740_991) throw new ArgumentOutOfRangeException(nameof(idle));
        return new SessionMutationProjector(database).PublishAsync(sessionId, SessionMutationProjector.Viewed,
            session => session.Time.Idle is null || idle > session.Time.Idle.Value.ToUnixTimeMilliseconds() ||
                session.Time.Viewed is { } viewed && viewed.ToUnixTimeMilliseconds() >= idle
                ? null : new SessionViewData(sessionId, idle), ct);
    }

    public async Task SelectAgentAsync(SessionId sessionId, string agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var session = await store.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
        if (session.Location.WorkspaceId is not null)
            throw new NotSupportedException("Workspace agent selection requires its Location-scoped catalog.");
        if (await AgentCatalog.ResolveAsync(session.Location.Directory, AgentId.FromExisting(agent), ct).ConfigureAwait(false) is null)
            throw new SessionSelectionException("The selected agent is not available in this session's catalog.", "agent");
        await new SessionMutationProjector(database).PublishAsync(sessionId, SessionMutationProjector.AgentSelected,
            current => new SessionAgentSelectionData(sessionId, agent, current.Agent), ct).ConfigureAwait(false);
    }

    public async Task SelectModelAsync(SessionId sessionId, ModelRef model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var session = await store.GetSessionAsync(sessionId, ct).ConfigureAwait(false) ?? throw new SessionMutationNotFoundException(sessionId);
        // An unchanged selection is independent of current catalog/credential availability.
        if (session.Model is { } existing && existing.ProviderId == model.ProviderId && existing.Id == model.Id &&
            (existing.Variant ?? "default") == (model.Variant ?? "default")) return;
        if (session.Location.WorkspaceId is not null)
            throw new NotSupportedException("Workspace model selection requires its Location-scoped catalog.");
        var catalog = await providers.ReadCatalogAsync(session.Location.Directory, ct).ConfigureAwait(false);
        var selected = catalog.Models.FirstOrDefault(item => item.ProviderId == model.ProviderId && item.Id == model.Id);
        if (selected is null || !selected.Available)
            throw new SessionSelectionException("The selected model is not available in this session's catalog.", "model");
        if (!selected.TransportSupported)
            throw new NotSupportedException("The selected model transport is not implemented.");
        if (model.Variant is not (null or "default") && !selected.Variants.Any(item => item.Id == model.Variant))
            throw new SessionSelectionException("The selected variant is not available for this model.", "model");
        await new SessionMutationProjector(database).PublishAsync(sessionId, SessionMutationProjector.ModelSelected,
            current => current.Model?.ProviderId == model.ProviderId && current.Model.Id == model.Id &&
                (current.Model.Variant ?? "default") == (model.Variant ?? "default")
                ? null : new SessionModelSelectionData(sessionId, model, current.Model), ct).ConfigureAwait(false);
    }
}
