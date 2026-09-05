namespace OpenCode.Server.Services;

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCode.Schema;

public interface IEventFeedService
{
    TimeProvider Clock => TimeProvider.System;
    EventFeedSubscriber Subscribe();
    void Unsubscribe(EventFeedSubscriber subscriber);
    void Publish(OpenCodeEvent evt);
    void PublishRaw(string eventType, object data);
}

public sealed class SubscriberOverflowException(int capacity)
    : InvalidOperationException("Subscriber overflowed queue capacity.")
{
    public int Capacity { get; } = capacity;
}

public sealed class EventFeedEncodingException(EventId eventId, string eventType, Exception cause)
    : Exception("Failed to encode public event.", cause)
{
    public EventId EventId { get; } = eventId;
    public string EventType { get; } = eventType;
}

public sealed class EventFeedSubscriber
{
    private readonly Channel<string> _channel;
    public int Capacity { get; }

    public EventFeedSubscriber(int capacity = 4096)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            // TryWrite must reject a full queue, not report success while dropping the frame.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _channel = Channel.CreateBounded<string>(options);
        Capacity = capacity;
    }

    public bool TryWrite(string frame) => _channel.Writer.TryWrite(frame);

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete(Exception? ex = null) => _channel.Writer.TryComplete(ex);
}

public sealed class EventFeedService : IEventFeedService, IDisposable
{
    public TimeProvider Clock { get; }
    private readonly Lock _gate = new();
    private readonly List<EventFeedSubscriber> _subscribers = [];
    private readonly ILogger<EventFeedService> _logger;
    private bool _disposed;

    public EventFeedService() : this(NullLogger<EventFeedService>.Instance, TimeProvider.System) { }

    public EventFeedService(ILogger<EventFeedService> logger, TimeProvider? clock = null)
    {
        Clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public EventFeedSubscriber Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscriber = new EventFeedSubscriber();
            _subscribers.Add(subscriber);
            return subscriber;
        }
    }

    public void Unsubscribe(EventFeedSubscriber subscriber)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscriber);
            subscriber.Complete();
        }
    }

    public void Publish(OpenCodeEvent evt)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscribers.Count == 0) return;
            string frame;
            try
            {
                var json = JsonSerializer.Serialize(evt, OpenCodeJsonContext.Default.OpenCodeEvent);
                frame = $"data: {json}\n\n";
            }
            catch (Exception cause)
            {
                FailEncoding(evt.Id, evt.Type, cause);
                return;
            }
            BroadcastFrame(frame);
        }
    }

    public void PublishRaw(string eventType, object data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscribers.Count == 0) return;
            var payload = new
            {
                id = EventId.Create().Value,
                type = eventType,
                created = Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                data
            };
            string frame;
            try
            {
                frame = $"data: {JsonSerializer.Serialize(payload)}\n\n";
            }
            catch (Exception cause)
            {
                FailEncoding(EventId.FromExisting(payload.id), eventType, cause);
                return;
            }
            BroadcastFrame(frame);
        }
    }

    private void FailEncoding(EventId eventId, string eventType, Exception cause)
    {
        // Like Queue.failCauseUnsafe, completion preserves backlog before surfacing failure.
        var error = new EventFeedEncodingException(eventId, eventType, cause);
        foreach (var subscriber in _subscribers) subscriber.Complete(error);
        _subscribers.Clear();
        _logger.LogError(cause, "Failed to encode public event {EventId} of type {EventType}", eventId, eventType);
    }

    private void BroadcastFrame(string frame)
    {
        // Call under _gate so all subscribers observe the same publication order.
        for (var i = _subscribers.Count - 1; i >= 0; i--)
        {
            var subscriber = _subscribers[i];
            if (subscriber.TryWrite(frame)) continue;
            _subscribers.RemoveAt(i);
            // Completion retains accepted frames; the reader sees failure after draining them.
            subscriber.Complete(new SubscriberOverflowException(subscriber.Capacity));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var subscriber in _subscribers) subscriber.Complete();
            _subscribers.Clear();
        }
    }
}
