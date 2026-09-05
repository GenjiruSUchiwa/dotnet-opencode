namespace OpenCode.Server.Integrations;

using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCode.Core.Database;
using OpenCode.Core.Instructions;
using OpenCode.Core.Integrations;
using OpenCode.Core.Llm;
using OpenCode.Core.Locations;
using OpenCode.Core.Mcp;
using OpenCode.Core.Tools;
using OpenCode.Schema;
using OpenCode.Server.Services;

public sealed class IntegrationLocationLease(ToolLocationLease tools, IntegrationRuntime runtime) : IAsyncDisposable
{
    public LocationInfo Location => tools.Location;
    public IntegrationRuntime Runtime { get; } = runtime;
    public ValueTask DisposeAsync() => tools.DisposeAsync();
}

/// <summary>One integration runtime per authoritative tool Location; attempts never cross Location keys.</summary>
public sealed class IntegrationHostService : IHostedService, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocationRef, Entry> _locations = [];
    private readonly HashSet<LocationRef> _refresh = [];
    private readonly HashSet<string> _switched = new(StringComparer.Ordinal);
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CredentialStore _credentials;
    private readonly IntegrationProviders _providers;
    private readonly IEventFeedService _feed;
    private readonly ILogger<IntegrationHostService> _log;
    private readonly IReadOnlyList<IIntegrationCommandSource> _commandSources;
    private Task? _worker;
    private bool _closed;

    private sealed class Entry(LocationInfo location, McpRuntime mcp, IntegrationRuntime runtime)
    {
        public LocationInfo Location { get; } = location;
        public McpRuntime Mcp { get; } = mcp;
        public IntegrationRuntime Runtime { get; } = runtime;
        public SemaphoreSlim Reload { get; } = new(1);
        public Action<McpRuntimeChange>? Changed;
    }

    public IntegrationHostService(CredentialStore credentials, ConsoleIntegrationService console, OpenAiOAuthService openAi,
        IIntegrationCallbackFactory callbacks, IEventFeedService feed, ILogger<IntegrationHostService> log,
        IEnumerable<IIntegrationCommandSource>? commandSources = null)
    {
        _credentials = credentials;
        _feed = feed;
        _log = log;
        _commandSources = commandSources?.ToArray() ?? [];
        OAuth = new McpOAuthService(new McpOAuthCredentialStore(credentials, PublishCommittedAsync), credentials.Clock);
        _providers = new IntegrationProviders(credentials, console, openAi, callbacks, PublishCommittedAsync);
    }

    public McpOAuthService OAuth { get; }

    /// <summary>Use from LocalToolOptions.McpCreated. Does not acquire the Location map or perform I/O.</summary>
    public IDisposable AttachMcp(LocationInfo location, McpRuntime mcp, IDisposable? additionalSubscription = null)
    {
        try
        {
            var entry = ForLocation(location, mcp);
            return new Subscription(() =>
            {
                try { if (entry.Changed is { } handler) mcp.Changed -= handler; }
                finally { additionalSubscription?.Dispose(); }
            });
        }
        catch { additionalSubscription?.Dispose(); throw; }
    }

    public async ValueTask<IntegrationLocationLease> AcquireAsync(LocationRef location, ToolLocationFactory factory,
        PermissionLocationMap map, bool observe = true, CancellationToken ct = default)
    {
        var tools = await factory.AcquireAsync(map, location, ct);
        try
        {
            var entry = ForLocation(tools.Location, tools.Mcp);
            if (observe)
            {
                await tools.Mcp.ObserveAsync(InstructionCatalog.ReadMcpConfiguration(tools.Location.Directory), ct);
                await ReloadAsync(entry, ct);
            }
            return new IntegrationLocationLease(tools, entry.Runtime);
        }
        catch { await tools.DisposeAsync(); throw; }
    }

    private Entry ForLocation(LocationInfo location, McpRuntime mcp)
    {
        var key = PermissionLocationMap.Canonical(new(location.Directory, location.WorkspaceId));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_locations.TryGetValue(key, out var found))
            {
                if (!ReferenceEquals(found.Mcp, mcp)) throw new InvalidOperationException("The integration Location still owns a different MCP runtime.");
                return found;
            }
            var runtime = new IntegrationRuntime(_credentials, PublishCommittedAsync, () => _feed.Publish(
                IntegrationEventDefinitions.Updated.Create(EventId.Create(), _credentials.Clock.GetUtcNow().ToUnixTimeMilliseconds(), new EmptyEventData(), key)));
            var entry = new Entry(location, mcp, runtime);
            entry.Changed = change =>
            {
                if ((change.Kind & McpChangeKind.Status) == 0) return;
                lock (_gate) { if (_closed) return; _refresh.Add(key); }
                _signals.Writer.TryWrite(true);
            };
            mcp.Changed += entry.Changed;
            _locations.Add(key, entry);
            return entry;
        }
    }

    /// <summary>Use for mutations committed by the host's other credential endpoints too. Values never enter events.</summary>
    public Task PublishCommittedAsync(CredentialMutation mutation, CancellationToken ct)
    {
        foreach (var notification in mutation.Notifications)
        {
            var now = _credentials.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            switch (notification)
            {
                case CredentialNotification.Updated:
                    _feed.Publish(CredentialEventDefinitions.Updated.Create(EventId.Create(), now, new EmptyEventData()));
                    break;
                case CredentialNotification.Switched switched:
                    _feed.Publish(CredentialEventDefinitions.Switched.Create(EventId.Create(), now,
                        new CredentialSwitchedEventData(IntegrationId.FromExisting(switched.IntegrationId),
                            switched.CredentialId is { } id ? CredentialId.FromExisting(id) : null)));
                    lock (_gate) if (!_closed) _switched.Add(switched.IntegrationId);
                    _signals.Writer.TryWrite(true);
                    break;
            }
        }
        return Task.CompletedTask;
    }

    private async Task ReloadAsync(Entry entry, CancellationToken ct)
    {
        await entry.Reload.WaitAsync(ct);
        try
        {
            var definitions = (await _providers.DefinitionsAsync(entry.Mcp, ct)).ToDictionary(item => item.Reference.Id.Value, StringComparer.Ordinal);
            foreach (var registration in _commandSources.SelectMany(source => source.Methods(entry.Location)))
            {
                var id = registration.Integration.Id.Value;
                var definition = definitions.GetValueOrDefault(id) ?? new IntegrationDefinition(registration.Integration, [],
                    new Dictionary<string, Func<FormAnswer?, string?, CancellationToken, Task<IntegrationAuthorization>>>());
                definitions[id] = definition with { Methods = definition.Methods.Where(method => method is not IntegrationCommandMethod command
                    || command.Id != registration.Method.Id).Append(registration.Method).ToArray() };
            }
            entry.Runtime.Define(definitions.Values.ToArray());
        }
        finally { entry.Reload.Release(); }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(_shutdown.Token))
            {
                while (_signals.Reader.TryRead(out _)) { }
                Entry[] entries;
                LocationRef[] refresh;
                string[] switched;
                lock (_gate)
                {
                    entries = _locations.Values.ToArray(); refresh = _refresh.ToArray(); switched = _switched.ToArray();
                    _refresh.Clear(); _switched.Clear();
                }
                foreach (var entry in entries)
                {
                    try
                    {
                        foreach (var id in switched) await entry.Mcp.CredentialChangedAsync(id, _shutdown.Token);
                        if (refresh.Contains(PermissionLocationMap.Canonical(new(entry.Location.Directory, entry.Location.WorkspaceId))))
                            await ReloadAsync(entry, _shutdown.Token);
                    }
                    catch (Exception error) when (!_shutdown.IsCancellationRequested)
                    { _log.LogWarning("Integration update failed ({ErrorType}).", error.GetType().Name); }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    /// <summary>Await from the owning tool Location's close callback, before its resources are disposed.</summary>
    public async ValueTask InvalidateAsync(LocationRef location)
    {
        Entry? entry;
        lock (_gate) _locations.Remove(PermissionLocationMap.Canonical(location), out entry);
        if (entry is null) return;
        if (entry.Changed is { } changed) entry.Mcp.Changed -= changed;
        await entry.Runtime.DisposeAsync();
    }

    public Task StartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _worker ??= RunAsync(); return Task.CompletedTask; }
    public async Task StopAsync(CancellationToken ct) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate) { if (_closed) return; _closed = true; entries = _locations.Values.ToArray(); _locations.Clear(); }
        await _shutdown.CancelAsync();
        _signals.Writer.TryComplete();
        if (_worker is not null) await _worker;
        foreach (var entry in entries)
        {
            if (entry.Changed is { } changed) entry.Mcp.Changed -= changed;
            await entry.Runtime.DisposeAsync();
        }
    }

    private sealed class Subscription(Action close) : IDisposable
    {
        private int _closed;
        public void Dispose() { if (Interlocked.Exchange(ref _closed, 1) == 0) close(); }
    }
}

public static class IntegrationHostComposition
{
    /// <summary>Requires existing CredentialStore, HttpClient, event feed, and the shared tool Location graph.</summary>
    public static IServiceCollection AddIntegrationServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ConsoleIntegrationService>();
        services.TryAddSingleton<OpenAiOAuthService>();
        services.TryAddSingleton<IIntegrationCallbackFactory, IntegrationCallbackFactory>();
        services.TryAddSingleton<IntegrationHostService>();
        services.TryAddSingleton(provider => provider.GetRequiredService<IntegrationHostService>().OAuth);
        services.AddHostedService<IntegrationHostService>(provider => provider.GetRequiredService<IntegrationHostService>());
        return services;
    }
}
