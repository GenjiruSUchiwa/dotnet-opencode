namespace OpenCode.Core.Event;

using System.Diagnostics;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// Volatile process-local event subscription. Durable payloads are the actual
/// committed events, never reconstructed responses. This is not log replay.
/// Observers must enqueue and return; do not synchronously republish on the same aggregate.
/// </summary>
public static class SessionEvents
{
    private sealed class Subscription(Action<OpenCodeEvent> observe) : IDisposable
    {
        internal void Notify(OpenCodeEvent value) => observe(value);
        public void Dispose() { lock (Sync) Observers.Remove(this); }
    }
    private sealed class Gate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }
    private static readonly Lock Sync = new();
    private static readonly HashSet<Subscription> Observers = [];
    private static readonly Dictionary<string, Gate> Gates = new(StringComparer.Ordinal);

    public static IDisposable Subscribe(Action<OpenCodeEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var subscription = new Subscription(observer);
        lock (Sync) Observers.Add(subscription);
        return subscription;
    }

    internal static async Task<T> SerializeAsync<T>(string aggregateId, Func<Task<T>> publish, CancellationToken ct)
    {
        Gate gate;
        lock (Sync)
        {
            if (!Gates.TryGetValue(aggregateId, out gate!)) Gates.Add(aggregateId, gate = new Gate());
            gate.Users++;
        }
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(true);
            try { return await publish().ConfigureAwait(true); }
            finally { gate.Semaphore.Release(); }
        }
        finally
        {
            lock (Sync)
                if (--gate.Users == 0) { Gates.Remove(aggregateId); gate.Semaphore.Dispose(); }
        }
    }

    internal static void Notify(OpenCodeEvent value, bool committed)
    {
        Subscription[] observers;
        lock (Sync) observers = Observers.ToArray();
        foreach (var observer in observers)
        {
            if (!committed) { observer.Notify(value); continue; }
            try { observer.Notify(value); }
            catch (Exception) { Trace.TraceError("A committed event observer failed."); }
        }
    }

    internal static void ContentDelta(SessionId sessionId, MessageId messageId, int ordinal, string delta, bool reasoning, TimeProvider clock) =>
        Notify(new OpenCodeEvent(EventId.Create(), reasoning ? "session.reasoning.delta" : "session.text.delta",
            clock.GetUtcNow().ToUnixTimeMilliseconds(), JsonSerializer.SerializeToElement(
                new SessionContentDeltaEventData(sessionId, messageId, ordinal, delta), OpenCodeJsonContext.Default.SessionContentDeltaEventData)), false);

    internal static void ToolInputDelta(SessionId sessionId, MessageId messageId, string id, string delta, TimeProvider clock) =>
        Notify(new OpenCodeEvent(EventId.Create(), "session.tool.input.delta", clock.GetUtcNow().ToUnixTimeMilliseconds(),
            JsonSerializer.SerializeToElement(new SessionToolInputDeltaEventData(sessionId, messageId, id, delta),
                OpenCodeJsonContext.Default.SessionToolInputDeltaEventData)), false);

    internal static void ToolProgress(SessionId sessionId, MessageId messageId, string id, System.Text.Json.Nodes.JsonObject metadata, TimeProvider clock) =>
        Notify(new OpenCodeEvent(EventId.Create(), "session.tool.progress", clock.GetUtcNow().ToUnixTimeMilliseconds(),
            JsonSerializer.SerializeToElement(new SessionToolProgressEventData(sessionId, messageId, id,
                metadata.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))),
                OpenCodeJsonContext.Default.SessionToolProgressEventData)), false);

    internal static void CompactionDelta(SessionId sessionId, string text, TimeProvider clock) =>
        Notify(SessionEventDefinitions.Compaction.Delta.Create(EventId.Create(), clock.GetUtcNow().ToUnixTimeMilliseconds(),
            new SessionCompactionDeltaEventData(sessionId, text)), false);
}
