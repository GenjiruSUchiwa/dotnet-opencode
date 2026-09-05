namespace OpenCode.Server.Services;

using System.Threading.Channels;
using OpenCode.Core.Event;
using OpenCode.Schema;

/// <summary>
/// One host-scoped subscription to Core's volatile notifications. Core supplies
/// committed durable envelopes and distinct ephemeral envelopes; neither is rebuilt here.
/// </summary>
internal sealed class SessionEventBridge(IEventFeedService feed)
{
    private readonly Channel<OpenCodeEvent> _events = Channel.CreateUnbounded<OpenCodeEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private IDisposable? _subscription;
    private Task _publication = Task.CompletedTask;

    internal bool IsRunning => _subscription is not null && !_publication.IsCompleted;

    internal void Start()
    {
        // Observers only enqueue. EventFeed serialization cannot delay a committed transaction.
        _subscription = SessionEvents.Subscribe(value => _events.Writer.TryWrite(value));
        // Install the subscription before the pump can fault, so cleanup cannot
        // race a later assignment that would leave an observer attached.
        _publication = PublishAsync();
    }

    internal async Task StopAsync()
    {
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        _events.Writer.TryComplete();
        await _publication;
    }

    private async Task PublishAsync()
    {
        try
        {
            await foreach (var value in _events.Reader.ReadAllAsync()) feed.Publish(value);
        }
        finally
        {
            Interlocked.Exchange(ref _subscription, null)?.Dispose();
            _events.Writer.TryComplete();
            // Publication faults remain on _publication and are awaited by StopAsync.
        }
    }
}
