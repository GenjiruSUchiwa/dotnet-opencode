namespace OpenCode.Core.Session.Subagents;

using System.Diagnostics;
using System.Text.Json;
using OpenCode.Core.Agent;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Jobs;
using OpenCode.Core.Permissions;
using OpenCode.Core.Tools;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Schema;

public sealed record SubagentResult(SessionId SessionId, string Status, string Output);

/// <summary>Host-owned subagent jobs over the SAME Session execution coordinator. No model loop or tool registry.</summary>
public sealed partial class SessionSubagents : IAsyncDisposable
{
    private const string NoText = "Subagent completed without a text response.";
    private sealed record Completion(string Status, string? Output = null, string? Error = null);
    private readonly SessionStore _sessions;
    private readonly SessionQueries _queries;
    private readonly SessionMutationProjector _selection;
    private readonly SessionExecutionEngine _execution;
    private readonly JobRuntime? _jobs;
    private readonly RestartPersistence _restart;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<(string Id, double Started)> _notifications = [];
    private readonly HashSet<Task> _owned = [];
    private volatile bool _closed;

    public SessionSubagents(IDatabase database, SessionStore sessions, SessionQueries queries,
        SessionExecutionEngine execution, CancellationToken lifetime, JobRuntime? jobs = null)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Subagent jobs require a cancellable host lifetime.", nameof(lifetime));
        _sessions = sessions; _queries = queries; _execution = execution;
        _selection = new SessionMutationProjector(database);
        _jobs = jobs;
        _restart = new RestartPersistence(database);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _lifetime.Token;
    }

    private JobRuntime Jobs => _jobs ?? throw new NotSupportedException("Subagents require the host's shared JobRuntime; no private job registry is substituted.");

    public async Task<SubagentResult> RunAsync(string agentId, string description, string prompt, SessionId? existingId,
        bool background, ToolContext context, PermissionService permissions, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _ = Jobs;
        _token.ThrowIfCancellationRequested();
        var parent = await _sessions.GetSessionAsync(context.SessionId, ct) ?? throw new ToolExecutionException($"Parent session not found: {context.SessionId}");
        var current = parent;
        var depth = 0;
        while (current.ParentId is { } ancestor)
        {
            depth++;
            current = await _sessions.GetSessionAsync(ancestor, ct) ?? throw new ToolExecutionException($"Parent session not found: {ancestor}");
        }
        var limit = ConfigLoader.LoadDocument(directory: parent.Location.Directory)["experimental"]?["subagent_depth"]?.GetValue<double>() ?? 1;
        if (!double.IsFinite(limit) || limit < 0 || Math.Truncate(limit) != limit) throw new ToolExecutionException("experimental.subagent_depth must be a nonnegative integer.");
        if (depth >= limit) throw new ToolExecutionException($"Subagent depth limit reached ({limit}). Increase \"experimental.subagent_depth\" to allow nested subagents.");
        var agent = await AgentCatalog.ResolveAsync(parent.Location.Directory, AgentId.FromExisting(agentId), ct) ?? throw new ToolExecutionException($"Unknown agent: {agentId}");
        if (agent.Mode == AgentMode.Primary) throw new ToolExecutionException($"Agent {agentId} cannot run as a subagent");
        try
        {
            await permissions.AssertAsync(new PermissionAskInput(context.SessionId, "subagent", [agent.Id.Value], Agent: context.AgentId,
                Save: [agent.Id.Value], Source: new PermissionSource("tool", context.RequireMessageId().Value, context.CallId)), ct);
        }
        catch (Exception error) when (error is PermissionBlockedException or PermissionCorrectedException)
        { throw new ToolExecutionException($"Subagent denied: {agent.Id}", error); }

        var existing = existingId is { } requested
            ? await _sessions.GetSessionAsync(requested, ct) ?? throw new ToolExecutionException($"Subagent session not found: {requested}") : null;
        if (existing is not null && existing.ParentId != parent.Id) throw new ToolExecutionException($"Session {existing.Id} is not a child of the current session");
        if (existing is not null && existing.Agent != agent.Id.Value)
        {
            // Source child selection publishes policy state; it does not resolve credentials/models before admission.
            await _selection.PublishAsync(existing.Id, SessionMutationProjector.AgentSelected,
                selected => new SessionAgentSelectionData(existing.Id, agent.Id.Value, selected.Agent), ct);
            if (agent.Model is { } model)
                await _selection.PublishAsync(existing.Id, SessionMutationProjector.ModelSelected,
                    selected => selected.Model is { } previous && previous.ProviderId == model.ProviderId && previous.Id == model.Id &&
                        (previous.Variant ?? "default") == (model.Variant ?? "default") ? null : new SessionModelSelectionData(existing.Id, model, selected.Model), ct);
        }
        var child = existing ?? await _sessions.CreateSessionAsync(parent.Location.Directory, description, parent.ProjectId,
            ct: ct, agent: agent.Id.Value, model: agent.Model ?? parent.Model, location: parent.Location, metadata: parent.Metadata,
            parentId: parent.Id, subpath: parent.Subpath);
        await context.ReportProgress(new Dictionary<string, object> { ["sessionID"] = child.Id.Value, ["status"] = "running" });
        var detached = false;
        try
        {
            await _execution.AdmitPromptAsync(child.Id, new PromptInput(existing is null ? "You are a subagent spawned by another session.\n" + prompt : prompt), ct: ct);
            if (!background || existing is not null) await _execution.WakeAsync(child.Id, _token);
            var recovery = new JobSubagentRecovery(parent.Id, child.Id, agent.Name, description);
            var info = await Jobs.StartAsync(new JobStartInput("subagent", token => ExecuteAsync(child.Id, token),
                child.Id.Value, description, new Dictionary<string, JsonElement>(), recovery), ct);
            if (background)
            {
                var promoted = await Jobs.BackgroundAsync(info.Id, ct) ?? throw new InvalidOperationException("Known subagent job disappeared during promotion.");
                detached = true;
                await NotifyWhenDoneAsync(recovery, promoted, ct: ct);
                return BackgroundResult(child.Id);
            }
            var result = await Jobs.BlockAsync(info.Id, parent.Id, ct) ?? throw new InvalidOperationException("Known subagent job disappeared while waiting.");
            if (result.Backgrounded)
            {
                detached = true;
                await NotifyWhenDoneAsync(recovery, result.Info, ct: ct);
                return BackgroundResult(child.Id);
            }
            if (result.Info.Status == JobStatus.Error) throw new ToolExecutionException($"Subagent failed (sessionID: {child.Id}): {result.Info.Error ?? "unknown error"}");
            if (result.Info.Status == JobStatus.Cancelled) throw new ToolExecutionException($"Subagent cancelled (sessionID: {child.Id})");
            if (result.Info.Status != JobStatus.Completed) throw new InvalidOperationException("Subagent wait did not settle.");
            return new(child.Id, "completed", result.Info.Output ?? NoText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested && !detached)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15), _sessions.Clock);
                await _execution.InterruptAsync(child.Id, cleanup.Token);
                await Jobs.CancelAsync(child.Id.Value, cleanup.Token);
                await _execution.AwaitIdleAsync(child.Id, cleanup.Token);
            }
            throw;
        }
    }

    /// <summary>Explicit host/UI detachment. A foreground waiter receives the same running result as background=true.</summary>
    public async Task<bool> BackgroundAsync(SessionId parentId, SessionId childId, CancellationToken ct = default)
    {
        if (await _sessions.GetSessionAsync(childId, ct) is not { } child || child.ParentId != parentId) return false;
        var info = await Jobs.GetAsync(childId.Value, ct);
        if (info is null || info.Type != "subagent") return false;
        var promoted = await Jobs.BackgroundAsync(childId.Value, ct);
        if (promoted is null) return false;
        var marker = (await Jobs.PendingBackgroundAsync(ct)).FirstOrDefault(item => item.NotificationId == promoted.NotificationId);
        if (marker?.Recovery is not JobSubagentRecovery recovery) throw new InvalidOperationException("Promoted subagent has no recovery descriptor.");
        await NotifyWhenDoneAsync(recovery, promoted, ct: ct);
        return true;
    }

    public async Task<bool> CancelAsync(SessionId parentId, SessionId childId, CancellationToken ct = default)
    {
        if (await _sessions.GetSessionAsync(childId, ct) is not { } child || child.ParentId != parentId) return false;
        if (await Jobs.GetAsync(childId.Value, ct) is not { Type: "subagent" }) return false;
        await _execution.InterruptAsync(childId, ct);
        await Jobs.CancelAsync(childId.Value, ct);
        return true;
    }

    private async Task<string> ExecuteAsync(SessionId child, CancellationToken token, Task? recovered = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
        token = lifetime.Token;
        try
        {
            await (recovered ?? _execution.ResumeChildJobAsync(child, token, _token));
            // Source Session.messages checks existence before its typed message query.
            if (await _sessions.GetSessionAsync(child, token) is null) throw new SessionMutationNotFoundException(child);
            var messages = recovered is null
                ? await _queries.MessagesAsync(child, 20, SessionQueryOrder.Descending, null, token)
                : (await _queries.ContextAsync(child, token)).Reverse().ToArray();
            var assistant = messages
                .OfType<AssistantMessage>().FirstOrDefault(message => message.Time.Completed is not null && message.Error is null);
            var text = assistant is null ? "" : string.Concat(assistant.Content.OfType<AssistantTextContent>().Select(part => part.Text));
            return text.Length == 0 ? NoText : text;
        }
        catch (Exception error) when (error is PermissionDeclinedException or QuestionCancelledException)
        {
            throw new OperationCanceledException(error.Message, error, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15), _sessions.Clock);
            await _execution.InterruptAsync(child, cleanup.Token);
            await _execution.AwaitIdleAsync(child, cleanup.Token);
            throw;
        }
    }

    private async Task NotifyWhenDoneAsync(JobSubagentRecovery recovery, JobInfo generation,
        Func<SessionId, bool>? suppressWake = null, CancellationToken ct = default)
    {
        if (generation.NotificationId is null) throw new InvalidOperationException("A background subagent requires a durable notification identity.");
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_notifications.Add((generation.Id, generation.StartedAt))) return;
            Own(NotifyAsync(recovery, generation, suppressWake));
        }
        finally { _gate.Release(); }
    }

    private async Task NotifyAsync(JobSubagentRecovery recovery, JobInfo generation, Func<SessionId, bool>? suppressWake)
    {
        try
        {
            var result = (await Jobs.WaitAsync(generation.Id, ct: _token)).Info
                ?? throw new InvalidOperationException("Subagent job disappeared before notification.");
            if (result.StartedAt != generation.StartedAt || result.NotificationId != generation.NotificationId)
            {
                // A later generation may replace the ID before this observer attaches. The prior
                // terminal marker is authoritative; never send the later output under the old ID.
                var previous = (await Jobs.PendingBackgroundAsync(_token)).FirstOrDefault(item => item.NotificationId == generation.NotificationId);
                if (previous is null) return;
                if (previous.Status == JobStatus.Running) throw new InvalidOperationException("Prior subagent generation has no terminal recovery outcome yet.");
                if (previous.Recovery is not JobSubagentRecovery prior || prior.ChildSessionId != recovery.ChildSessionId || prior.ParentSessionId != recovery.ParentSessionId)
                    throw new InvalidOperationException("Prior subagent notification belongs to a different recovery owner.");
                await DeliverAsync(new SubagentRecovery(prior.ParentSessionId, prior.ChildSessionId, prior.Agent, prior.Description),
                    generation.NotificationId!.Value, CompletionOf(previous.Status, previous.Output, previous.Error), suppressWake);
                return;
            }
            await DeliverAsync(new SubagentRecovery(recovery.ParentSessionId, recovery.ChildSessionId, recovery.Agent, recovery.Description),
                generation.NotificationId!.Value, CompletionOf(result.Status, result.Output, result.Error), suppressWake);
        }
        finally
        {
            await _gate.WaitAsync();
            try { _notifications.Remove((generation.Id, generation.StartedAt)); }
            finally { _gate.Release(); }
        }
    }

    private async Task DeliverAsync(SubagentRecovery recovery, MessageId notificationId, Completion result, Func<SessionId, bool>? suppressWake = null)
    {
        _token.ThrowIfCancellationRequested();
        var text = result.Status == "completed" ? result.Output ?? NoText : result.Status == "error" ? result.Error ?? "Subagent failed" : "Subagent cancelled";
        await _sessions.AdmitInboxAsync(recovery.ParentSessionId, notificationId,
            new SyntheticInboxPayload($"<subagent sessionID=\"{recovery.ChildSessionId}\" state=\"{result.Status}\" description=\"{recovery.Description}\">\n{text}\n</subagent>", recovery.Description,
                new Dictionary<string, JsonElement>
                {
                    ["source"] = JsonSerializer.SerializeToElement("subagent"), ["childID"] = JsonSerializer.SerializeToElement(recovery.ChildSessionId.Value),
                    ["agent"] = JsonSerializer.SerializeToElement(recovery.Agent), ["state"] = JsonSerializer.SerializeToElement(result.Status)
                }), ct: _token);
        var parent = await _sessions.GetSessionAsync(recovery.ParentSessionId, _token) ?? throw new SessionMutationNotFoundException(recovery.ParentSessionId);
        if (parent.Revert is null && suppressWake?.Invoke(parent.Id) != true) await _execution.WakeAsync(parent.Id, _token);
        await Jobs.CompleteBackgroundAsync(notificationId, _token);
    }

    private static Completion CompletionOf(JobStatus status, string? output, string? error) => new(status switch
    {
        JobStatus.Completed => "completed", JobStatus.Error => "error", JobStatus.Cancelled => "cancelled",
        _ => throw new InvalidOperationException("A running subagent is not a completion.")
    }, output, error);

    // Both drains and notification delivery are host-owned and awaited on disposal.
    private void Own(Task task)
    {
        _owned.Add(task);
        _ = ObserveAsync(task);
    }
    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Trace.TraceWarning("Subagent task failed; pending recovery markers are retained: {0}", error.Message); }
        finally
        {
            await _gate.WaitAsync();
            try { _owned.Remove(task); }
            finally { _gate.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        await _gate.WaitAsync();
        try { if (_closed) return; _closed = true; tasks = _owned.ToArray(); }
        finally { _gate.Release(); }
        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            // Execution ownership remains with the shared engine. The host must settle that
            // engine before disposing stores/Locations; a joiner must not stop another owner.
        }
        finally { _lifetime.Dispose(); }
    }

    private static SubagentResult BackgroundResult(SessionId id) => new(id, "running",
        $"The subagent is working in the background (sessionID: {id}). You will be notified automatically when it finishes.\n" +
        "DO NOT sleep, poll for progress, ask the subagent for status, or duplicate this subagent's work; avoid working with the same files or topics it is using.\n" +
        "Work on non-overlapping tasks, or briefly tell the user what you launched and end your response.");
}
