namespace OpenCode.Core.Permissions;

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Channels;
using OpenCode.Core.Tools;
using OpenCode.Schema;

public abstract record PermissionNotification;
public sealed record PermissionAsked(PermissionRequest Request) : PermissionNotification;
public sealed record PermissionReplied(SessionId SessionId, PermissionId RequestId, PermissionReply Reply) : PermissionNotification;
public sealed record PermissionCancelled(SessionId SessionId, PermissionId RequestId) : PermissionNotification;
public sealed record PermissionDecision(PermissionId Id, PermissionEffect Effect);
public sealed record PermissionEvaluation(PermissionEffect Effect, string? Message = null);

/// <summary>Canonical Core ask/assert input. Omitted Agent resolves through the current Session;
/// Source remains absent for non-tool callers. SessionId is supplied by the trusted route/context.</summary>
public sealed record PermissionAskInput(
    SessionId SessionId,
    string Action,
    IReadOnlyList<string> Resources,
    PermissionId? Id = null,
    AgentId? Agent = null,
    IReadOnlyList<string>? Save = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    PermissionSource? Source = null);

public interface IPermissionEvaluationHook
{
    ValueTask<PermissionEvaluation> EvaluateAsync(PermissionRequest request, AgentId? agent, PermissionEffect effect, CancellationToken ct);
}

/// <summary>Location-owned permission evaluation and pending approvals. Consume Notifications from one host
/// dispatcher. No model/tool API may synthesize replies; the authenticated user boundary owns ReplyAsync.</summary>
public sealed class PermissionService : IToolPermission, IAsyncDisposable
{
    private sealed record Pending(PermissionRequest Request, AgentId? Agent, TaskCompletionSource Completion);
    private readonly string _projectId;
    private readonly IPermissionRuleSource _configured;
    private readonly IPermissionGrantStore _saved;
    private readonly IPermissionEvaluationHook? _hook;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PermissionId, Pending> _pending = [];
    private readonly Channel<PermissionNotification> _notifications = Channel.CreateUnbounded<PermissionNotification>(
        new UnboundedChannelOptions { AllowSynchronousContinuations = false, SingleReader = true });
    private bool _disposed;
    public ChannelReader<PermissionNotification> Notifications => _notifications.Reader;
    public bool PersistentGrants => _saved.Persistent;
    public string ProjectId => _projectId;
    public bool IsDisposed => Volatile.Read(ref _disposed);

    public PermissionService(string projectId, IPermissionRuleSource configured, IPermissionGrantStore saved,
        IPermissionEvaluationHook? hook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        _projectId = projectId;
        _configured = configured ?? throw new ArgumentNullException(nameof(configured));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _hook = hook;
    }

    public Task AssertAsync(string action, IReadOnlyList<string> resources, IReadOnlyList<string> save,
        ToolContext context, IReadOnlyDictionary<string, object>? metadata, CancellationToken ct) =>
        AssertAsync(ToolInput(action, resources, save, context, metadata), ct);

    public async Task AssertAsync(PermissionAskInput input, CancellationToken ct = default)
    {
        var request = Request(input);
        Pending? item = null;
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = await EvaluateAsync(request, input.Agent, ct).ConfigureAwait(true);
            if (result.Decision.Effect == PermissionEffect.Deny)
                throw new PermissionBlockedException(input.Action, request.Resources,
                    result.Rules.Where(rule => PermissionRules.Match(input.Action, rule.Action)).ToArray(), result.Decision.Message);
            if (result.Decision.Effect == PermissionEffect.Allow) return;
            ct.ThrowIfCancellationRequested();
            item = Create(request with { Message = result.Decision.Message }, input.Agent);
        }
        finally { _gate.Release(); }

        try { await item.Completion.Task.WaitAsync(ct).ConfigureAwait(true); }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                if (_pending.TryGetValue(item.Request.Id, out var current) && ReferenceEquals(current, item))
                {
                    _pending.Remove(item.Request.Id);
                    item.Completion.TrySetCanceled(ct);
                    _notifications.Writer.TryWrite(new PermissionCancelled(item.Request.SessionId, item.Request.Id));
                }
            }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Creates a host-owned pending request without waiting; its lifetime ends on reply or service disposal.</summary>
    public Task<PermissionDecision> AskAsync(string action, IReadOnlyList<string> resources, IReadOnlyList<string> save,
        ToolContext context, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken ct = default) =>
        AskAsync(ToolInput(action, resources, save, context, metadata), ct);

    public async Task<PermissionDecision> AskAsync(PermissionAskInput input, CancellationToken ct = default)
    {
        var request = Request(input);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = await EvaluateAsync(request, input.Agent, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            if (result.Decision.Effect == PermissionEffect.Ask) Create(request with { Message = result.Decision.Message }, input.Agent);
            return new(request.Id, result.Decision.Effect);
        }
        finally { _gate.Release(); }
    }

    public async Task ReplyAsync(PermissionId id, SessionId owner, PermissionReply reply, string? message = null, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(reply)) throw new ArgumentOutOfRangeException(nameof(reply));
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_pending.TryGetValue(id, out var existing) || existing.Request.SessionId != owner)
                throw new KeyNotFoundException($"Pending permission not found for Session: {id}");
            // Once admitted, a reply and its grant are settled together, independent of HTTP/client cancellation.
            if (reply == PermissionReply.Reject)
            {
                Complete(existing, reply, string.IsNullOrEmpty(message) ? new PermissionDeclinedException() : new PermissionCorrectedException(message));
                foreach (var item in _pending.Values.Where(item => item.Request.SessionId == owner).ToArray())
                    Complete(item, PermissionReply.Reject, new PermissionDeclinedException());
                return;
            }
            if (reply == PermissionReply.Always && existing.Request.Save is { Count: > 0 } save)
            {
                if (!PersistentGrants)
                    throw new NotSupportedException("Persistent permission grants are not implemented for this Location; use once or reject.");
                await _saved.AddAsync(_projectId, existing.Request.Action, save, CancellationToken.None).ConfigureAwait(true);
            }
            Complete(existing, reply);
            if (reply != PermissionReply.Always || existing.Request.Save is not { Count: > 0 }) return;
            foreach (var item in _pending.Values.ToArray())
            {
                try
                {
                    var result = await EvaluateAsync(item.Request, item.Agent, CancellationToken.None).ConfigureAwait(true);
                    if (result.Decision.Effect == PermissionEffect.Allow) Complete(item, PermissionReply.Always);
                }
                catch (PermissionSessionNotFoundException) { }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PermissionRequest>> ListAsync(SessionId? session = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try { return _pending.Values.Where(item => session is null || item.Request.SessionId == session).Select(item => item.Request).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<PermissionRequest?> GetAsync(PermissionId id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try { return _pending.GetValueOrDefault(id)?.Request; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var item in _pending.Values)
            {
                item.Completion.TrySetException(new PermissionDeclinedException());
                _notifications.Writer.TryWrite(new PermissionCancelled(item.Request.SessionId, item.Request.Id));
            }
            _pending.Clear();
            _notifications.Writer.TryComplete();
        }
        finally { _gate.Release(); }
    }

    private async ValueTask<(PermissionEvaluation Decision, IReadOnlyList<PermissionRule> Rules)> EvaluateAsync(
        PermissionRequest request, AgentId? agent, CancellationToken ct)
    {
        var configured = await _configured.GetAsync(request.SessionId, agent, ct).ConfigureAwait(true);
        if (request.Resources.Any(resource => PermissionRules.Evaluate(request.Action, resource, configured).Effect == PermissionEffect.Deny))
            return (new(PermissionEffect.Deny), configured);
        var grants = await _saved.ListAsync(_projectId, ct).ConfigureAwait(true);
        if (grants.Any(rule => rule.Effect != PermissionEffect.Allow)) throw new InvalidOperationException("Saved permission grants must be allow rules.");
        var rules = configured.Concat(grants).ToArray();
        var effect = request.Resources.Any(resource => PermissionRules.Evaluate(request.Action, resource, rules).Effect == PermissionEffect.Ask)
            ? PermissionEffect.Ask : PermissionEffect.Allow;
        var decision = _hook is null ? new PermissionEvaluation(effect) : await _hook.EvaluateAsync(request, agent, effect, ct).ConfigureAwait(true);
        if (!Enum.IsDefined(decision.Effect)) throw new InvalidOperationException("Invalid permission hook effect.");
        return (decision, rules);
    }

    private Pending Create(PermissionRequest request, AgentId? agent)
    {
        if (_pending.ContainsKey(request.Id)) throw new InvalidOperationException($"Duplicate pending permission ID: {request.Id}");
        var item = new Pending(request, agent, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        // AskAsync has no waiter; observe control-flow failures without changing what AssertAsync receives.
        _ = item.Completion.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _pending.Add(request.Id, item);
        _notifications.Writer.TryWrite(new PermissionAsked(request));
        return item;
    }

    private void Complete(Pending item, PermissionReply reply, Exception? error = null)
    {
        _notifications.Writer.TryWrite(new PermissionReplied(item.Request.SessionId, item.Request.Id, reply));
        _pending.Remove(item.Request.Id);
        if (error is null) item.Completion.TrySetResult();
        else item.Completion.TrySetException(error);
    }

    private static PermissionAskInput ToolInput(string action, IReadOnlyList<string> resources, IReadOnlyList<string> save,
        ToolContext context, IReadOnlyDictionary<string, object>? metadata) =>
        new(context.SessionId, action, resources, Agent: context.AgentId, Save: save,
            Metadata: metadata?.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
            Source: new PermissionSource("tool", context.RequireMessageId().Value, context.CallId));

    private static PermissionRequest Request(PermissionAskInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Action);
        ArgumentNullException.ThrowIfNull(input.Resources);
        if (!input.SessionId.IsInitialized()) throw new ArgumentException("Session ID is required.", nameof(input));
        if (input.Agent is { } agent && !agent.IsInitialized()) throw new ArgumentException("Agent ID must be a string.", nameof(input));
        if (input.Id is { } id && (!id.IsInitialized() || !id.Value.StartsWith("per", StringComparison.Ordinal)))
            throw new ArgumentException("Permission ID must start with per.", nameof(input));
        if (input.Source is { } source && (source.Type != "tool" || source.MessageId is null || source.Id is null))
            throw new ArgumentException("Permission source must be a tool source with messageID and id strings.", nameof(input));
        if (input.Resources.Any(resource => resource is null) || input.Save?.Any(resource => resource is null) == true)
            throw new ArgumentException("Permission resources must be strings.", nameof(input));
        return new(input.Id ?? PermissionId.Create(), input.SessionId, input.Action, Array.AsReadOnly(input.Resources.ToArray()),
            input.Save is null ? null : Array.AsReadOnly(input.Save.ToArray()),
            input.Metadata is null ? null : new ReadOnlyDictionary<string, JsonElement>(input.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())),
            input.Source);
    }
}
