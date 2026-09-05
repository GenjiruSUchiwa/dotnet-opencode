namespace OpenCode.Core.Session;

using OpenCode.Schema;
using OpenCode.Core.Event;
using System.Collections.Frozen;

public sealed class SessionMutationInProgressException(SessionId sessionId)
    : InvalidOperationException("Session is reserved for an exclusive mutation.")
{
    public SessionId SessionId { get; } = sessionId;
}

public sealed class SessionBusyException(SessionId sessionId) : InvalidOperationException("Session is busy.")
{
    public SessionId SessionId { get; } = sessionId;
}

/// <summary>
/// Process-global session ownership. Run joins, prompt wakes coalesce, and distinct
/// sessions run concurrently. Work is owned by the initiating caller's token.
/// This is not clustered ownership or a daemon-scoped background execution service.
/// </summary>
internal static class SessionRunCoordinator
{
    private sealed class Execution(CancellationToken owner, string cancellationReason, InboxPromotable scope)
    {
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(owner);
        internal readonly HashSet<Action<string>> Listeners = [];
        internal Task Done = Task.CompletedTask;
        internal InboxPromotable Scope = scope;
        internal InboxPromotable? PendingWake;
        internal bool Settling;
        internal string CancellationReason = cancellationReason;
    }

    private static readonly Lock Sync = new();
    private static readonly Dictionary<SessionId, Execution> Active = [];
    private static readonly Dictionary<SessionId, HashSet<Task>> Scheduled = [];
    private sealed class Admissions
    {
        internal int Count;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static readonly Dictionary<SessionId, Admissions> Admitting = [];
    private static readonly HashSet<SessionId> Reserved = [];
    private static readonly AsyncLocal<SessionId?> Operation = new();

    private sealed class RemovalReservation(SessionId id) : IAsyncDisposable
    {
        private bool _disposed;
        public ValueTask DisposeAsync()
        {
            lock (Sync)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                Reserved.Remove(id);
            }
            return ValueTask.CompletedTask;
        }
    }

    internal static bool IsActive(SessionId id)
    {
        lock (Sync) return Active.ContainsKey(id);
    }

    internal static IReadOnlySet<SessionId> Snapshot()
    {
        // Ownership includes cancellation cleanup/settlement, but neither an
        // unconsumed inbox row nor a not-yet-registered scheduled wake is ownership.
        lock (Sync) return Active.Keys.ToFrozenSet();
    }

    internal static async Task RunAsync(SessionId id, TimeProvider clock, bool wake, Action<string> output,
        Func<Action<string>, CancellationToken, Task> started,
        Func<Action<string>, InboxPromotable, CancellationToken, Task> drain,
        Func<Exception?, string, CancellationToken, Task> settled, CancellationToken ct, bool requireIdle = false,
        Func<CancellationToken, Task>? admission = null, string cancellationReason = "user", Action? registered = null,
        Func<CancellationToken, Task<bool>>? reconcile = null, bool onlyIfIdle = false,
        InboxPromotable scope = InboxPromotable.Input)
    {
        var admitted = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var selected = await AdmitAsync(id, () => InboxSerialization.RunAsync(id, async () =>
            {
                var existing = !admitted && reconcile is not null && await reconcile(ct);
                lock (Sync)
                {
                    if (Reserved.Contains(id)) throw new SessionMutationInProgressException(id);
                    if (onlyIfIdle && Active.ContainsKey(id)) throw new SessionAlreadyOwnedException();
                    if (!admitted && !existing && requireIdle && Active.ContainsKey(id))
                        throw new NotSupportedException("Model overrides require an idle session; no input was admitted.");
                }
                // All ownership registrations share this gate. No owner can appear
                // between the idle policy check, durable admission, and registration.
                if (!admitted && !existing && admission is not null) await admission(ct);
                admitted = true;
                lock (Sync)
                {
                    if (Reserved.Contains(id)) throw new SessionMutationInProgressException(id);
                    if (Active.TryGetValue(id, out var active))
                    {
                        var waiting = active.Settling || active.Stop.IsCancellationRequested;
                        // New admissions during cleanup survive interruption. Input
                        // wakes subsume steer-only continuation, never the reverse.
                        if (wake) active.PendingWake = active.PendingWake == InboxPromotable.Input ? InboxPromotable.Input : scope;
                        if (!waiting)
                        {
                            active.Listeners.Add(output);
                        }
                        return (Entry: active, Waiting: waiting, Owns: false);
                    }
                    var entry = new Execution(ct, cancellationReason, scope);
                    entry.Listeners.Add(output);
                    Active.Add(id, entry);
                    entry.Done = OwnAsync(id, entry, started, drain, settled, clock);
                    return (Entry: entry, Waiting: false, Owns: true);
                }
            }, ct), ct);
            registered?.Invoke();
            if (selected.Waiting)
            {
                // Stopping owners refuse new work. The new caller remains the owner
                // of its eventual successor; no CancellationToken.None background task.
                try { await selected.Entry.Done.WaitAsync(ct); }
                catch when (!ct.IsCancellationRequested) { }
                if (wake)
                    lock (Sync) scope = selected.Entry.PendingWake ?? scope;
                continue;
            }
            try
            {
                if (selected.Owns) await selected.Entry.Done;
                else await selected.Entry.Done.WaitAsync(ct);
            }
            finally { lock (Sync) selected.Entry.Listeners.Remove(output); }
            return;
        }
    }

    private static async Task OwnAsync(SessionId id, Execution entry,
        Func<Action<string>, CancellationToken, Task> started,
        Func<Action<string>, InboxPromotable, CancellationToken, Task> drain,
        Func<Exception?, string, CancellationToken, Task> settled, TimeProvider clock)
    {
        await Task.Yield();
        var previousOperation = Operation.Value;
        Operation.Value = id;
        void Output(string value)
        {
            Action<string>[] listeners;
            lock (Sync) listeners = entry.Listeners.ToArray();
            foreach (var listener in listeners) listener(value);
        }
        Exception? failure = null;
        try
        {
            await started(Output, entry.Stop.Token);
            while (true)
            {
                await drain(Output, entry.Scope, entry.Stop.Token);
                lock (Sync)
                {
                    if (entry.PendingWake is { } scope && !entry.Stop.IsCancellationRequested)
                    {
                        entry.Scope = scope;
                        entry.PendingWake = null;
                        continue;
                    }
                    entry.Settling = true;
                    break;
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            lock (Sync) entry.Settling = true;
            throw;
        }
        finally
        {
            try
            {
                // Bounded, owned settlement outlives cancellation of provider work.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
                await settled(failure, entry.CancellationReason, cleanup.Token);
            }
            finally
            {
                lock (Sync) Active.Remove(id);
                entry.Stop.Dispose();
                Operation.Value = previousOperation;
            }
        }
    }

    internal static bool Interrupt(SessionId id)
    {
        lock (Sync)
        {
            if (!Active.TryGetValue(id, out var entry)) return false;
            if (entry.Stop.IsCancellationRequested) return false;
            entry.PendingWake = null;
            if (entry.Settling) return false;
            entry.CancellationReason = "user";
            entry.Stop.Cancel();
            return true;
        }
    }

    internal static Task ScheduleAsync(SessionId id, Func<Action, Task> start, CancellationToken lifetime)
    {
        if (!lifetime.CanBeCanceled) throw new ArgumentException("Advisory execution requires a host-owned cancellation lifetime.", nameof(lifetime));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = Task.CompletedTask;
        lock (Sync)
        {
            if (Reserved.Contains(id)) throw new SessionMutationInProgressException(id);
            owned = ObserveAsync();
            if (!Scheduled.TryGetValue(id, out var tasks)) Scheduled.Add(id, tasks = []);
            tasks.Add(owned);
        }
        return accepted.Task;

        async Task ObserveAsync()
        {
            await Task.Yield();
            try { await start(() => accepted.TrySetResult()); }
            catch (OperationCanceledException error) { accepted.TrySetCanceled(error.CancellationToken); }
            catch (Exception error)
            {
                if (!accepted.TrySetException(error)) System.Diagnostics.Trace.TraceError("A scheduled session execution failed.");
            }
            finally
            {
                lock (Sync)
                {
                    Scheduled[id].Remove(owned);
                    if (Scheduled[id].Count == 0) Scheduled.Remove(id);
                }
            }
        }
    }

    internal static async Task<T> AdmitAsync<T>(SessionId id, Func<Task<T>> admission, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Admissions lease;
        lock (Sync)
        {
            if (Reserved.Contains(id)) throw new SessionMutationInProgressException(id);
            if (!Admitting.TryGetValue(id, out lease!)) Admitting.Add(id, lease = new Admissions());
            lease.Count++;
        }
        var previousOperation = Operation.Value;
        Operation.Value = id;
        try { return await admission(); }
        finally
        {
            Operation.Value = previousOperation;
            lock (Sync)
                if (--lease.Count == 0) { Admitting.Remove(id); lease.Done.TrySetResult(); }
        }
    }

    /// <summary>
    /// Protects the source remove sequence (interrupt, awaitIdle, delete/purge)
    /// against same-process new admissions and ownership registrations.
    /// No SQLite/publication/inbox semaphore is held while waiting for cleanup.
    /// </summary>
    internal static async Task WithIdleMutationAsync(SessionId id, Func<CancellationToken, Task> mutation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await using var reservation = await ReserveRemovalAsync(id, ct);
        ct.ThrowIfCancellationRequested();
        await mutation(ct);
    }

    /// <summary>Revert uses Busy, not interruption. Share ownership registration's gate while doing idle work.</summary>
    internal static Task<T> WithIdleOperationAsync<T>(SessionId id, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        AdmitAsync(id, () => InboxSerialization.RunAsync(id, async () =>
        {
            if (IsActive(id)) throw new SessionBusyException(id);
            return await operation(ct);
        }, ct), ct);

    internal static async Task<IAsyncDisposable> ReserveRemovalAsync(SessionId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Operation.Value == id)
            throw new InvalidOperationException("A Session execution or admission cannot wait for its own removal settlement.");
        Task admissions;
        lock (Sync)
        {
            if (!Reserved.Add(id)) throw new SessionMutationInProgressException(id);
            admissions = Admitting.TryGetValue(id, out var pending) ? pending.Done.Task : Task.CompletedTask;
        }
        var reservation = new RemovalReservation(id);
        try
        {
            Interrupt(id);
            await admissions.WaitAsync(ct);
            await AwaitIdleAsync(id, ct);
            ct.ThrowIfCancellationRequested();
            return reservation;
        }
        catch
        {
            await reservation.DisposeAsync();
            throw;
        }
    }

    internal static async Task AwaitIdleAsync(SessionId id, CancellationToken ct)
    {
        while (true)
        {
            Task[] tasks;
            lock (Sync)
                tasks = (Scheduled.TryGetValue(id, out var pending) ? pending : Enumerable.Empty<Task>())
                    .Concat(Active.TryGetValue(id, out var active) ? [active.Done] : Array.Empty<Task>()).Distinct().ToArray();
            if (tasks.Length == 0) return;
            try { await Task.WhenAll(tasks).WaitAsync(ct); }
            catch when (!ct.IsCancellationRequested) { }
        }
    }
}
