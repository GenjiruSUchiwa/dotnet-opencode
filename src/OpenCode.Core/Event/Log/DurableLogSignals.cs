namespace OpenCode.Core.Event.Log;

using System.Threading.Channels;

/// <summary>Process-local coalesced wakeups only. Readers always fetch actual committed rows.</summary>
internal static class DurableLogSignals
{
    internal sealed class Subscription(string aggregate) : IDisposable
    {
        internal readonly Channel<bool> Wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest, AllowSynchronousContinuations = false });
        public void Dispose()
        {
            lock (Gate)
            {
                if (Subscriptions.TryGetValue(aggregate, out var items))
                {
                    items.Remove(this);
                    if (items.Count == 0) Subscriptions.Remove(aggregate);
                }
                Wake.Writer.TryComplete();
            }
        }
    }
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, HashSet<Subscription>> Subscriptions = new(StringComparer.Ordinal);
    internal static Subscription Subscribe(string aggregate)
    {
        lock (Gate)
        {
            var subscription = new Subscription(aggregate);
            if (!Subscriptions.TryGetValue(aggregate, out var items)) Subscriptions.Add(aggregate, items = []);
            items.Add(subscription);
            return subscription;
        }
    }
    internal static void Notify(string aggregate)
    {
        lock (Gate)
            if (Subscriptions.TryGetValue(aggregate, out var items))
                foreach (var item in items) item.Wake.Writer.TryWrite(true);
    }
}
