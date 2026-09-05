namespace OpenCode.Core.Tools;

using OpenCode.Core.Permissions;
using OpenCode.Schema;

public interface IToolDraft
{
    IReadOnlyList<ToolInfo> List();
    ToolInfo? Get(string id);
    void Add(ToolInfo tool);
    void Update(string id, Func<ToolInfo, ToolInfo> update);
    void Remove(string id);
}

public sealed class ToolRegistration : IDisposable, IAsyncDisposable
{
    private readonly ToolRegistry _owner;
    internal Action<IToolDraft> Transform { get; }
    internal bool Active = true;
    internal ToolRegistration(ToolRegistry owner, Action<IToolDraft> transform) { _owner = owner; Transform = transform; }
    public void Dispose() => _owner.Remove(this);
    public ValueTask DisposeAsync() { _owner.Remove(this); return ValueTask.CompletedTask; }
}

/// <summary>Location-owned ordered transforms. Contains no permission assertion service or default registrations.</summary>
public sealed class ToolRegistry : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<ToolRegistration> _registrations = [];
    private OrderedDictionary<string, ToolInfo> _tools = new(StringComparer.Ordinal);
    private IReadOnlyList<ToolRegistrationError> _errors = [];
    private Exception? _rebuildFailure;
    private readonly IToolExecutionHooks? _hooks;
    public TimeProvider Clock { get; }
    private readonly List<TaskCompletionSource> _reloads = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private long _requestedAt;
    private bool _closed;
    private bool _materializing;
    private int _batchDepth;
    private bool _dirty;

    // Existing SDK construction supplies HttpClient. It no longer implies builtin installation.
    public ToolRegistry(HttpClient? http = null, IToolExecutionHooks? hooks = null, TimeProvider? clock = null)
    { _hooks = hooks; Clock = clock ?? TimeProvider.System; }

    public IReadOnlyList<ToolRegistrationError> RegistrationErrors { get { lock (_gate) return _errors; } }

    public ToolRegistration Transform(Action<IToolDraft> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        lock (_gate)
        {
            RequireMutable();
            var registration = new ToolRegistration(this, transform);
            _registrations.Add(registration);
            try { Changed(); }
            catch
            {
                registration.Active = false;
                _registrations.Remove(registration);
                throw;
            }
            return registration;
        }
    }

    /// <summary>Coalesces synchronous registration/disposal changes into one rebuild. Terminal teardown uses Dispose.</summary>
    public void Batch(Action action)
    {
        lock (_gate)
        {
            RequireMutable();
            _batchDepth++;
            try { action(); }
            finally
            {
                _batchDepth--;
                if (_batchDepth == 0 && _dirty && !_closed) Materialize();
            }
        }
    }

    /// <summary>Trailing-edge 500 ms reload coalescing, as in State.reload. Cancelling a waiter does not cancel shared reload.</summary>
    public Task ReloadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            if (_materializing) throw new InvalidOperationException("Tool transforms must not reenter registry mutation.");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _reloads.Add(done);
            _requestedAt = Clock.GetTimestampMilliseconds();
            _worker ??= ReloadLoopAsync();
            return done.Task.WaitAsync(ct);
        }
    }

    public ToolSnapshot Snapshot(IReadOnlyList<PermissionRule>? permissions = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_rebuildFailure is { } failure) throw new ToolCatalogUnavailableException(failure);
            var active = _tools.Where(item => !WhollyDisabled(item.Value.Options?.Permission ?? item.Key, permissions ?? [])).ToArray();
            return new ToolSnapshot(active.Select(item => item.Value), !WhollyDisabled("execute", permissions ?? []), _hooks, clock: Clock);
        }
    }

    internal void Remove(ToolRegistration registration)
    {
        lock (_gate)
        {
            if (!registration.Active) return;
            if (_materializing) throw new InvalidOperationException("Tool transforms must not dispose registrations during replay.");
            registration.Active = false;
            _registrations.Remove(registration);
            if (!_closed) Changed();
        }
    }

    public void Dispose() => Close();

    private void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_materializing) throw new InvalidOperationException("Tool transforms must not close their registry during replay.");
            _closed = true;
            foreach (var registration in _registrations) registration.Active = false;
            _registrations.Clear();
            _tools = new(StringComparer.Ordinal);
            _errors = [];
            _shutdown.Cancel();
            foreach (var waiter in _reloads) waiter.TrySetResult();
            _reloads.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        Task? worker;
        lock (_gate) worker = _worker;
        if (worker is not null) await worker.ConfigureAwait(true);
    }

    private async Task ReloadLoopAsync()
    {
        try
        {
            while (true)
            {
                int remaining;
                lock (_gate) remaining = (int)Math.Max(0, _requestedAt + 500 - Clock.GetTimestampMilliseconds());
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), Clock, _shutdown.Token).ConfigureAwait(true);
                lock (_gate)
                {
                    if (_closed) return;
                    if (Clock.GetTimestampMilliseconds() < _requestedAt + 500) continue;
                    try
                    {
                        Materialize();
                        foreach (var waiter in _reloads) waiter.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        foreach (var waiter in _reloads) waiter.TrySetException(error);
                    }
                    _reloads.Clear();
                    _worker = null;
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private void RequireMutable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_materializing) throw new InvalidOperationException("Tool transforms must not reenter registry mutation.");
    }

    private void Changed()
    {
        _dirty = true;
        if (_batchDepth == 0) Materialize();
    }

    private void Materialize()
    {
        var draft = new Draft();
        _materializing = true;
        try
        {
            foreach (var registration in _registrations) registration.Transform(draft);
            _tools = draft.Tools;
            _errors = draft.Errors.AsReadOnly();
            _rebuildFailure = null;
            _dirty = false;
        }
        catch (Exception error)
        {
            // Registration ownership may already have changed. Never publish the previous catalog as current.
            _tools = new(StringComparer.Ordinal);
            _rebuildFailure = error;
            throw;
        }
        finally { draft.Active = false; _materializing = false; }
    }

    private static bool WhollyDisabled(string action, IReadOnlyList<PermissionRule> rules)
    {
        var rule = rules.LastOrDefault(rule => PermissionRules.Match(action, rule.Action));
        return rule is { Resource: "*", Effect: PermissionEffect.Deny };
    }

    private sealed class Draft : IToolDraft
    {
        public readonly OrderedDictionary<string, ToolInfo> Tools = new(StringComparer.Ordinal);
        public readonly List<ToolRegistrationError> Errors = [];
        public bool Active = true;
        private void RequireActive() { if (!Active) throw new InvalidOperationException("A tool draft is valid only during synchronous replay."); }
        public IReadOnlyList<ToolInfo> List() { RequireActive(); return Tools.Values.ToArray(); }
        public ToolInfo? Get(string id) { RequireActive(); return Tools.GetValueOrDefault(id); }
        public void Remove(string id) { RequireActive(); Tools.Remove(id); }
        public void Add(ToolInfo tool)
        {
            RequireActive();
            if (Validate(tool) is { } error) { Errors.Add(error); return; }
            Tools[tool.Id] = tool with { Options = tool.Options is null ? null : tool.Options with { } };
        }
        public void Update(string id, Func<ToolInfo, ToolInfo> update)
        {
            RequireActive();
            if (!Tools.TryGetValue(id, out var current)) return;
            var next = update(current with { Options = current.Options is null ? null : current.Options with { } });
            if (next is null) { Errors.Add(new(id, "Tool update returned null.")); return; }
            next = next with { Name = current.Name };
            if (next.Options?.Namespace != current.Options?.Namespace)
                next = next with { Options = (next.Options ?? new ToolOptions()) with { Namespace = current.Options?.Namespace } };
            if (Validate(next) is { } error) { Errors.Add(error); return; }
            Tools[id] = next;
        }
        private static ToolRegistrationError? Validate(ToolInfo tool)
        {
            if (tool is null) return new("", "Tool registration must not be null.");
            if (tool.Name is null) return new("", "Tool name must be a string.");
            if (tool.Options?.Namespace is { } space && !space.Split('.').All(ValidSegment))
                return new(space, $"Invalid tool namespace: {space}");
            if (!ValidSegment(ToolInfo.NormalizedName(tool.Name))) return new(tool.Name, $"Invalid tool name: {tool.Name}");
            if (tool.Options is { CodeMode: false } && tool.Id == "execute") return new(tool.Id, "Tool name execute is reserved for CodeMode.");
            if (tool.Options is { CodeMode: false, Pinned: not null }) return new(tool.Id, "Pinned is only supported for CodeMode tools.");
            if (tool.Description is null || tool.Input is null || tool.Execute is null) return new(tool.Id, "Tool requires description, input codec and executor.");
            try
            {
                // Custom native codecs own validation; their advertised schemas must still be valid JSON schema shapes.
                if (tool.Input.JsonSchema.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                    return new(tool.Id, "Invalid input JSON schema.");
                if (tool.Output is { } output && output.JsonSchema.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                    return new(tool.Id, "Invalid output JSON schema.");
                _ = tool.Input.JsonSchema.Clone();
                if (tool.Output is { } codec) _ = codec.JsonSchema.Clone();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(tool.Id, $"Invalid tool definition {tool.Id}: {error.Message}"); }
            return null;
        }
        private static bool ValidSegment(string segment) => segment.Length is >= 1 and <= 64 && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }
}
