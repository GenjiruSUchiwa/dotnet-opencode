namespace OpenCode.Server.Pty;

using System.Threading.Channels;
using OpenCode.Core.Pty;
using OpenCode.Schema;
using OpenCode.Server.Services;

/// <summary>One ordered, nonblocking publisher per Location; endpoints never publish duplicate events.</summary>
public sealed class PtyEventBridge : IAsyncDisposable
{
    private readonly PtyService runtime;
    private readonly Channel<PtyChange> changes = Channel.CreateUnbounded<PtyChange>(new() { SingleReader = true });
    private readonly Task pump;

    public PtyEventBridge(LocationInfo location, PtyService runtime, IEventFeedService feed, ILogger<PtyEventBridge> logger)
    {
        this.runtime = runtime;
        runtime.Changed += OnChanged;
        pump = RunAsync(new(location.Directory, location.WorkspaceId), feed, logger);
    }

    private void OnChanged(PtyChange change) => changes.Writer.TryWrite(change);

    private async Task RunAsync(LocationRef location, IEventFeedService feed, ILogger logger)
    {
        await foreach (var change in changes.Reader.ReadAllAsync())
        {
            try
            {
                var id = EventId.Create();
                var created = feed.Clock.GetUtcNow().ToUnixTimeMilliseconds();
                var value = change.Type switch
                {
                    "pty.created" => PtyEventDefinitions.Created.Create(id, created, new(change.Info), location),
                    "pty.updated" => PtyEventDefinitions.Updated.Create(id, created, new(change.Info), location),
                    "pty.exited" when change.Info.ExitCode is { } code => PtyEventDefinitions.Exited.Create(id, created, new(change.Info.Id, code), location),
                    "pty.deleted" => PtyEventDefinitions.Deleted.Create(id, created, new(change.Info.Id), location),
                    _ => throw new InvalidOperationException("A canonical PTY exit event requires an observed process exit code.")
                };
                feed.Publish(value);
            }
            catch (Exception error) { logger.LogError(error, "Failed to publish {PtyEvent} for {PtyId}", change.Type, change.Info.Id); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        runtime.Changed -= OnChanged;
        changes.Writer.TryComplete();
        await pump;
    }
}
