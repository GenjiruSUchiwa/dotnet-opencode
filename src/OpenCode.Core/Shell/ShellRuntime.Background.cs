namespace OpenCode.Core.Shell;

using System.Diagnostics;

public sealed partial class ShellRuntime
{
    private readonly Lock _backgroundGate = new();
    private readonly HashSet<Task> _background = [];

    // Notification observers only; actual Job state and recovery markers remain host-owned.
    internal void OwnBackground(Func<CancellationToken, Task> work)
    {
        lock (_backgroundGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed), this);
            var task = ObserveBackgroundAsync(work);
            _background.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_backgroundGate) _background.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ObserveBackgroundAsync(Func<CancellationToken, Task> work)
    {
        await Task.Yield();
        try { await work(_shutdown.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Leave the host's durable marker intact if admission/notification failed.
            Trace.TraceWarning("Shell background completion was not acknowledged ({0}).", error.GetType().Name);
        }
    }
}
