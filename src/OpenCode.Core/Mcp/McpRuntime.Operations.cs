namespace OpenCode.Core.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using OpenCode.Schema;

[Flags]
public enum McpChangeKind { Status = 1, Tools = 2, Prompts = 4, Resources = 8 }

/// <summary>Location-local invalidation, not a durable event or an MCP wire message.</summary>
public sealed record McpRuntimeChange(string Server, McpChangeKind Kind);

public sealed class McpServerNotFoundException(string server) : Exception($"MCP server not found: {server}")
{
    public string Server { get; } = server;
}

public sealed class McpNotConnectedException(string server) : InvalidOperationException("MCP server is not connected")
{
    public string Server { get; } = server;
}

public sealed partial class McpRuntime
{
    private McpConfiguration _configuration = new();
    // Null is a removal tombstone. Operational definitions take precedence over config and
    // survive unrelated observations, matching State.initial + ConfigMcpPlugin's draft.get.
    private readonly Dictionary<string, McpServerConfig?> _overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpChangeKind> _catalogChanges = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite
    });
    private Task? _worker;
    private volatile IReadOnlyList<McpServer> _servers = Array.Empty<McpServer>();

    /// <summary>Nonblocking status view, including pending handshakes. Call ObserveAsync to load config first.</summary>
    public IReadOnlyList<McpServer> Servers => _servers;

    /// <summary>
    /// Status changes are immediate; catalog changes follow the registry flush. Handlers must only
    /// enqueue work, not block or synchronously reenter this runtime. Unsubscribe when the host closes.
    /// </summary>
    public event Action<McpRuntimeChange>? Changed;

    public Task<McpObservation> AddAsync(string server, McpServerConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(config);
        return UpdateAsync(async token =>
        {
            _overrides[server] = config;
            await ReconcileAsync(token);
        }, ct);
    }

    public Task<McpObservation> RemoveAsync(string server, CancellationToken ct = default) =>
        UpdateAsync(async token =>
        {
            RequireServer(server);
            _overrides[server] = null;
            await ReconcileAsync(token);
        }, ct);

    /// <summary>Reconnects even when the definition is disabled. Does not write configuration.</summary>
    public Task<McpObservation> ConnectAsync(string server, CancellationToken ct = default) =>
        UpdateAsync(async token =>
        {
            var entry = RequireServer(server);
            await StopAsync(server, entry);
            await StartAsync(server, entry, token, force: true);
        }, ct);

    /// <summary>Stays disabled until explicit connect or an effective definition change.</summary>
    public Task<McpObservation> DisconnectAsync(string server, CancellationToken ct = default) =>
        UpdateAsync(async _ =>
        {
            var entry = RequireServer(server);
            await StopAsync(server, entry);
            SetStatus(server, entry, new McpDisabledStatus());
        }, ct);

    private async Task<McpObservation> UpdateAsync(Func<CancellationToken, Task> update, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            SettledObservation = null;
            try { await update(linked.Token); }
            finally
            {
                // Cancellation may arrive after a transport has closed. Flush the actual state even
                // then, so later requests cannot advertise disconnected tools or stale instructions.
                await FlushAsync();
            }
            return SettledObservation!;
        }
        finally { _gate.Release(); }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var servers = new Dictionary<string, McpServerConfig>(_configuration.Servers ?? new Dictionary<string, McpServerConfig>(), StringComparer.Ordinal);
        foreach (var item in _overrides)
        {
            if (item.Value is null) servers.Remove(item.Key);
            if (item.Value is not null) servers[item.Key] = item.Value;
        }
        foreach (var name in _entries.Keys.Where(name => !servers.ContainsKey(name)).ToArray())
        {
            await StopAsync(name, _entries[name]);
            _entries.Remove(name);
            PublishStatus(name);
        }
        foreach (var item in servers)
        {
            if (_entries.TryGetValue(item.Key, out var previous) && JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(previous.Config), JsonSerializer.SerializeToNode(item.Value))) continue;
            if (previous is not null) await StopAsync(item.Key, previous);
            _entries[item.Key] = new Entry(item.Value);
            PublishStatus(item.Key);
        }
        foreach (var item in _entries)
            if (item.Value.Status is McpPendingStatus) await StartAsync(item.Key, item.Value, ct);
    }

    private async Task StopAsync(string name, Entry entry)
    {
        var client = entry.Client;
        var elicitation = entry.Elicitation;
        entry.Client = null;
        entry.Elicitation = null;
        entry.Tools = [];
        entry.Prompts = [];
        entry.Resources = [];
        entry.Templates = [];
        Interlocked.Exchange(ref entry.CatalogChanged, 0);
        CatalogChanged(name, McpChangeKind.Tools | McpChangeKind.Prompts | McpChangeKind.Resources);
        try { if (elicitation is not null) await elicitation.DisposeAsync(); }
        finally { if (client is not null) await client.DisposeAsync(); }
    }

    private Entry RequireServer(string server) =>
        _entries.TryGetValue(server, out var entry) ? entry : throw new McpServerNotFoundException(server);

    private async Task<(McpClient Client, McpServerConfig Config)> ConnectedAsync(string server, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var entry = RequireServer(server);
            return (entry.Client is { Completion.IsCompleted: false } client ? client : throw new McpNotConnectedException(server), entry.Config);
        }
        finally { _gate.Release(); }
    }

    private void QueueChange(Entry entry, McpChangeKind kind)
    {
        Interlocked.Or(ref entry.CatalogChanged, (int)kind);
        _signals.Writer.TryWrite(true);
    }

    private async Task ProcessChangesAsync()
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(_shutdown.Token))
            {
                while (_signals.Reader.TryRead(out _)) { }
                try { await UpdateAsync(ApplyChangesAsync, _shutdown.Token); }
                catch (Exception error) when (!_shutdown.IsCancellationRequested)
                {
                    // A registry failure is not a successful empty observation. Leave it unavailable;
                    // the next observation or notification can rebuild it from current source state.
                    System.Diagnostics.Trace.TraceWarning("MCP notification processing failed: {0}", error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ApplyChangesAsync(CancellationToken ct)
    {
        foreach (var item in _entries)
        {
            var entry = item.Value;
            var changed = (McpChangeKind)Interlocked.Exchange(ref entry.CatalogChanged, 0);
            if (entry.Client is not { } client) continue;
            if (client.Completion.IsCompleted)
            {
                await StopAsync(item.Key, entry);
                SetStatus(item.Key, entry, new McpFailedStatus("Connection closed"));
                continue;
            }
            if ((changed & McpChangeKind.Tools) != 0 && client.ServerCapabilities.Tools is not null)
            {
                using var timeout = _registry.Clock.CreateLinkedCancellationTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Timeout(entry.Config)?.Catalog ?? 30_000));
                try
                {
                    entry.Tools = (await client.ListToolsAsync(cancellationToken: timeout.Token)).ToArray();
                    CatalogChanged(item.Key, McpChangeKind.Tools);
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    // Keep the previous tools on refresh failure, but still refresh other catalogs.
                    // Retry at the next observation/notification, not in a tight background loop.
                    Interlocked.Or(ref entry.CatalogChanged, (int)McpChangeKind.Tools);
                    System.Diagnostics.Trace.TraceWarning("MCP tool discovery failed: {0}", error.Message);
                }
            }
            if ((changed & McpChangeKind.Prompts) != 0 && client.ServerCapabilities.Prompts is not null)
            {
                entry.Prompts = await OptionalCatalogAsync(entry, "prompts", async token => await client.ListPromptsAsync(cancellationToken: token), ct);
                CatalogChanged(item.Key, McpChangeKind.Prompts);
            }
            if ((changed & McpChangeKind.Resources) != 0 && client.ServerCapabilities.Resources is not null)
            {
                entry.Resources = await OptionalCatalogAsync(entry, "resources", async token => await client.ListResourcesAsync(cancellationToken: token), ct);
                entry.Templates = await OptionalCatalogAsync(entry, "resource templates", async token => await client.ListResourceTemplatesAsync(cancellationToken: token), ct);
                CatalogChanged(item.Key, McpChangeKind.Resources);
            }
        }
    }

    private void SetStatus(string server, Entry entry, McpStatus status)
    {
        entry.Status = status;
        PublishStatus(server);
    }

    private void PublishStatus(string server)
    {
        _servers = Array.AsReadOnly(_entries.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => ServerInfo(item.Key, item.Value)).ToArray());
        RaiseChanged(new(server, McpChangeKind.Status));
    }

    private void CatalogChanged(string server, McpChangeKind kind) =>
        _catalogChanges[server] = _catalogChanges.GetValueOrDefault(server) | kind;

    private async Task FlushAsync()
    {
        _tools = _entries.SelectMany(item => item.Value.Tools.Select(tool => Registration(item.Key, item.Value, tool))).ToArray();
        await _registry.ReloadAsync();
        SettledObservation = Snapshot();
        foreach (var item in _catalogChanges) RaiseChanged(new(item.Key, item.Value));
        _catalogChanges.Clear();
    }

    private void RaiseChanged(McpRuntimeChange change)
    {
        if (Changed is not { } changed) return;
        foreach (Action<McpRuntimeChange> handler in changed.GetInvocationList())
        {
            try { handler(change); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("MCP change subscriber failed: {0}", error.Message); }
        }
    }
}
