namespace OpenCode.Core.Plugins;

using System.Text.Json;
using OpenCode.Core.Tools;
using OpenCode.Schema;

public sealed record NativePluginDefinition(PluginId Id, string Version,
    Func<NativePluginScope, CancellationToken, ValueTask> Initialize, PluginSource? Source = null);

/// <summary>Ordered native SDK/instance contributions. This does not load JS configuration.</summary>
public interface INativePluginSource
{
    IReadOnlyList<NativePluginDefinition> Definitions(LocationInfo location);
}

public sealed class NativePluginRegistration(Action remove) : IDisposable, IAsyncDisposable
{
    private int _disposed;
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) remove(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Setup-scoped registrations; rollback and close unwind resources in reverse order.</summary>
public sealed class NativePluginScope(LocationInfo location, Action? changed = null) : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _resources = [];
    private readonly List<Action<IToolDraft>> _transforms = [];
    private readonly List<IToolExecutionHooks> _hooks = [];
    private readonly CancellationTokenSource _lifetime = new();
    private bool _sealed;
    private bool _closed;
    public LocationInfo Location { get; } = location;
    public CancellationToken Lifetime => _lifetime.Token;
    internal IReadOnlyList<IToolExecutionHooks> Hooks { get { lock (_hooks) return _hooks.ToArray(); } }

    public T Own<T>(T resource) where T : IAsyncDisposable
    {
        if (_sealed || _closed) throw new InvalidOperationException("Plugin resources must be registered during initialization.");
        _resources.Add(resource);
        return resource;
    }

    public NativePluginRegistration TransformTools(Action<IToolDraft> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var registration = Own(new NativePluginRegistration(() =>
        {
            lock (_transforms) _transforms.Remove(transform);
            if (_sealed && !_closed) changed?.Invoke();
        }));
        _transforms.Add(transform);
        return registration;
    }

    public NativePluginRegistration HookTools(IToolExecutionHooks hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var registration = Own(new NativePluginRegistration(() => { lock (_hooks) _hooks.Remove(hook); }));
        _hooks.Add(hook);
        return registration;
    }

    internal void Seal() => _sealed = true;
    internal void Apply(IToolDraft draft)
    {
        Action<IToolDraft>[] transforms;
        lock (_transforms) transforms = _transforms.ToArray();
        foreach (var transform in transforms) transform(draft);
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        var errors = new List<Exception>();
        try { await _lifetime.CancelAsync(); } catch (Exception error) { errors.Add(error); }
        foreach (var resource in _resources.AsEnumerable().Reverse())
            try { await resource.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
        _resources.Clear();
        lock (_transforms) _transforms.Clear();
        lock (_hooks) _hooks.Clear();
        _lifetime.Dispose();
        if (errors.Count > 0) throw new AggregateException("Plugin cleanup failed.", errors);
    }
}

/// <summary>Native Location runtime. No external module resolver, instruction registry, or Server dependency.</summary>
public sealed class NativePluginHost(LocationInfo location, Action<PluginId?>? changed = null,
    IToolExecutionHooks? inheritedHooks = null) : IToolExecutionHooks, IAsyncDisposable
{
    private sealed record Active(NativePluginDefinition Definition, NativePluginScope Scope);
    private readonly SemaphoreSlim _activation = new(1);
    private Active[] _active = [];
    private PluginInfo[] _inventory = [];
    private ToolRegistry? _tools;
    private ToolRegistration? _registration;
    private bool _closed;
    private bool _changing;

    public IReadOnlyList<PluginInfo> List() => Volatile.Read(ref _inventory).ToArray();

    public void Attach(ToolRegistry tools)
    {
        if (_tools is not null) throw new InvalidOperationException("Plugin host already attached.");
        _tools = tools;
        _registration = tools.Transform(draft =>
        {
            foreach (var entry in Volatile.Read(ref _active)) entry.Scope.Apply(draft);
        });
    }

    public async Task ActivateAsync(IReadOnlyList<NativePluginDefinition> definitions, CancellationToken ct = default)
    {
        if (definitions.Select(item => item.Id).Distinct().Count() != definitions.Count)
            throw new ArgumentException("Duplicate plugin ID.", nameof(definitions));
        await _activation.WaitAsync(ct);
        var next = new List<Active>();
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _changing = true;
            var tools = _tools ?? throw new InvalidOperationException("Attach the Location tool registry before activation.");
            if (_active.Length == definitions.Count && _active.Select(item => (item.Definition.Id, item.Definition.Version))
                .SequenceEqual(definitions.Select(item => (item.Id, item.Version)))) return;
            var previous = _active.ToDictionary(item => item.Definition.Id);
            var inventory = new List<PluginInfo>();
            foreach (var definition in definitions)
            {
                ct.ThrowIfCancellationRequested();
                previous.Remove(definition.Id, out var old);
                if (old is not null) await CloseAsync(old.Scope);
                var loaded = await LoadAsync(definition, ct);
                if (loaded is not null)
                {
                    next.Add(loaded);
                    inventory.Add(new(definition.Source ?? new PluginSourceBuiltin(), "active", false, definition.Id));
                    continue;
                }
                inventory.Add(new(definition.Source ?? new PluginSourceBuiltin(), "failed", false, definition.Id, "Native plugin initialization failed."));
                if (old is not null && await LoadAsync(old.Definition, ct) is { } restored) next.Add(restored);
            }
            foreach (var removed in previous.Values.Reverse()) await CloseAsync(removed.Scope);
            Volatile.Write(ref _active, next.ToArray());
            // Replay the stable host transform without moving it behind later MCP producers.
            tools.Batch(() => tools.Transform(_ => { }).Dispose());
            if (tools.RegistrationErrors.Count > 0) throw new InvalidOperationException("Native plugin tool registrations are invalid.");
            Volatile.Write(ref _inventory, inventory.ToArray());
            changed?.Invoke(null);
        }
        catch
        {
            foreach (var entry in next.Concat(_active).DistinctBy(item => item.Scope).Reverse()) await CloseAsync(entry.Scope);
            _active = [];
            _inventory = definitions.Select(definition => new PluginInfo(definition.Source ?? new PluginSourceBuiltin(),
                "failed", false, definition.Id, "Native plugin generation did not commit.")).ToArray();
            _tools?.Batch(() => _tools.Transform(_ => { }).Dispose());
            throw;
        }
        finally { _changing = false; _activation.Release(); }
    }

    private async Task<Active?> LoadAsync(NativePluginDefinition definition, CancellationToken ct)
    {
        var scope = new NativePluginScope(location, () =>
        {
            if (!_closed && !_changing) _tools!.Batch(() => _tools.Transform(_ => { }).Dispose());
        });
        try
        {
            await definition.Initialize(scope, ct);
            scope.Seal();
            changed?.Invoke(definition.Id);
            return new(definition, scope);
        }
        catch (Exception error)
        {
            await CloseAsync(scope);
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            System.Diagnostics.Trace.TraceWarning("Native plugin initialization failed ({0}).", error.GetType().Name);
            return null;
        }
    }

    private static async Task CloseAsync(NativePluginScope scope)
    {
        try { await scope.DisposeAsync(); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Native plugin cleanup failed ({0}).", error.GetType().Name); }
    }

    private IEnumerable<IToolExecutionHooks> Hooks() =>
        (inheritedHooks is null ? Enumerable.Empty<IToolExecutionHooks>() : [inheritedHooks])
        .Concat(Volatile.Read(ref _active).SelectMany(item => item.Scope.Hooks)).ToArray();

    public async ValueTask<ToolInvocation> BeforeAsync(ToolInvocation invocation, ToolContext context, CancellationToken ct)
    { foreach (var hook in Hooks()) invocation = await hook.BeforeAsync(invocation, context, ct); return invocation; }
    public async ValueTask<ToolExecutionResult> AfterSuccessAsync(string name, JsonElement input, ToolContext context, ToolExecutionResult result, CancellationToken ct)
    { foreach (var hook in Hooks()) result = await hook.AfterSuccessAsync(name, input, context, result, ct); return result; }
    public async ValueTask<ToolExecutionException> AfterErrorAsync(string name, JsonElement input, ToolContext context, ToolExecutionException error, CancellationToken ct)
    { foreach (var hook in Hooks()) error = await hook.AfterErrorAsync(name, input, context, error, ct); return error; }

    public async ValueTask DisposeAsync()
    {
        await _activation.WaitAsync();
        try
        {
            if (_closed) return;
            _closed = true;
            foreach (var entry in _active.Reverse()) await CloseAsync(entry.Scope);
            _active = []; _inventory = [];
            _registration?.Dispose();
        }
        finally { _activation.Release(); }
    }
}
