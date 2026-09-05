namespace OpenCode.Core.Event;

using OpenCode.Schema;

/// <summary>SessionInbox.serialized: process-local promotion/mutation ordering, not execution ownership.</summary>
internal static class InboxSerialization
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Users;
    }

    private static readonly Lock Sync = new();
    private static readonly Dictionary<SessionId, Entry> Entries = new();

    internal static async Task<T> RunAsync<T>(SessionId id, Func<Task<T>> action, CancellationToken ct)
    {
        Entry entry;
        lock (Sync)
        {
            if (!Entries.TryGetValue(id, out entry!)) Entries.Add(id, entry = new Entry());
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(true);
            try { return await action().ConfigureAwait(true); }
            finally { entry.Gate.Release(); }
        }
        finally
        {
            lock (Sync)
            {
                if (--entry.Users == 0)
                {
                    Entries.Remove(id);
                    entry.Gate.Dispose();
                }
            }
        }
    }
}
