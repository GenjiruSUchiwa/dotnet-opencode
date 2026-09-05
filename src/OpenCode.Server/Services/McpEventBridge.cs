namespace OpenCode.Server.Services;

using System.Text.Json;
using System.Threading.Channels;
using OpenCode.Core.Mcp;
using OpenCode.Schema;

/// <summary>Attach once before a Location's first observation; dispose with its permission dispatcher.</summary>
public sealed class McpEventBridge : IAsyncDisposable, IDisposable
{
    private readonly McpRuntime _runtime;
    private readonly Channel<McpRuntimeChange> _changes = Channel.CreateUnbounded<McpRuntimeChange>(new()
    {
        SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
    });
    private readonly Task _pump;
    private int _disposed;

    public McpEventBridge(LocationInfo location, McpRuntime runtime, IEventFeedService feed)
    {
        _runtime = runtime;
        _runtime.Changed += Enqueue;
        _pump = RunAsync(location, feed);
    }

    private void Enqueue(McpRuntimeChange change)
    {
        // The callback executes under Core's lifecycle gate. Never call back into Core.
        _changes.Writer.TryWrite(change);
    }

    private async Task RunAsync(LocationInfo location, IEventFeedService feed)
    {
        await foreach (var change in _changes.Reader.ReadAllAsync())
        {
            // Current public event inventory exposes status/resources only. Tools are
            // shared transitional and prompts are Core-local; do not leak either here.
            foreach (var (kind, type) in new[] { (McpChangeKind.Status, "mcp.status.changed"), (McpChangeKind.Resources, "mcp.resources.changed") })
            {
                if ((change.Kind & kind) == 0) continue;
                feed.Publish(new OpenCodeEvent(EventId.Create(), type, feed.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    JsonSerializer.SerializeToElement(new McpStatusChangedEventData(change.Server), OpenCodeJsonContext.Default.McpStatusChangedEventData),
                    new LocationRef(location.Directory, location.WorkspaceId)));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _runtime.Changed -= Enqueue;
        _changes.Writer.TryComplete();
        await _pump;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
