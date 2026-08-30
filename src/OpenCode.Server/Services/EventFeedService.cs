namespace OpenCode.Server.Services;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using OpenCode.Schema;

public interface IEventFeedService
{
    EventFeedSubscriber Subscribe();
    void Unsubscribe(EventFeedSubscriber subscriber);
    void Publish(OpenCodeEvent evt);
    void PublishRaw(string eventType, object data);
}

public sealed class EventFeedSubscriber
{
    private readonly Channel<string> _channel;

    public EventFeedSubscriber(int capacity = 4096)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<string>(options);
    }

    public bool TryWrite(string frame) => _channel.Writer.TryWrite(frame);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete(Exception? ex = null) => _channel.Writer.TryComplete(ex);
}

public sealed class EventFeedService : IEventFeedService
{
    private readonly ConcurrentDictionary<EventFeedSubscriber, byte> _subscribers = new();

    public EventFeedSubscriber Subscribe()
    {
        var subscriber = new EventFeedSubscriber();
        _subscribers.TryAdd(subscriber, 0);
        return subscriber;
    }

    public void Unsubscribe(EventFeedSubscriber subscriber)
    {
        _subscribers.TryRemove(subscriber, out _);
        subscriber.Complete();
    }

    public void Publish(OpenCodeEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, OpenCodeJsonContext.Default.OpenCodeEvent);
        var frame = $"data: {json}\n\n";
        BroadcastFrame(frame);
    }

    public void PublishRaw(string eventType, object data)
    {
        var payload = new
        {
            id = EventId.Create().Value,
            type = eventType,
            created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data
        };
        var frame = $"data: {JsonSerializer.Serialize(payload)}\n\n";
        BroadcastFrame(frame);
    }

    private void BroadcastFrame(string frame)
    {
        foreach (var sub in _subscribers.Keys)
        {
            if (!sub.TryWrite(frame))
            {
                // Bounded dropping law: lag budget exceeded, drop subscriber immediately
                _subscribers.TryRemove(sub, out _);
                sub.Complete(new InvalidOperationException("Subscriber overflowed queue capacity."));
            }
        }
    }
}
