namespace OpenCode.Core.Session;

/// <summary>Tracks this engine's owned/joined calls for host teardown; SessionRunCoordinator remains the sole execution owner.</summary>
public sealed partial class SessionExecutionEngine
{
    private readonly Lock _hostGate = new();
    private readonly HashSet<Task> _hostDrains = [];

    public async Task AwaitOwnedDrainsAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task[] pending;
            lock (_hostGate) pending = _hostDrains.Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ct.ThrowIfCancellationRequested();
        }
    }

    private Task TrackDrain(Task task)
    {
        lock (_hostGate) _hostDrains.Add(task);
        _ = ObserveAsync();
        return task;
        async Task ObserveAsync()
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            lock (_hostGate) _hostDrains.Remove(task);
        }
    }
}
