# Porting Ruleset 02: Concurrency and Effects

This rulebook defines how to translate Effect v4 reactive and functional concurrency patterns into idiomatic C# (.NET 10).

---

## 1. The Core Mapping Philosophy

In TypeScript, Effect models computation through `Effect<A, E, R>`:
- `A` = Success type
- `E` = Error type
- `R` = Contextual requirements (services)

In modern C#:
- Do **not** import an unnatural monad library (e.g. `LanguageExt` with nested `Eff<RT, A>`). It causes friction with .NET standard libraries, ASP.NET Core, and diagnostics.
- `R` (Requirements) maps directly to **Dependency Injection** via constructor parameters (`IServiceProvider`, scoped/singleton services).
- `A` (Success) maps to `Task<A>` or `ValueTask<A>`.
- `E` (Domain Errors) maps to:
  1. **Domain Exceptions** for non-happy-path failures that bubble up to middleware/session error boundaries (e.g., `SessionNotFoundException`, `UnauthorizedException`).
  2. **Discriminated Union / Result Types** for frequent in-band operational branches (e.g., `InboxAdmissionResult.Enqueued | InboxAdmissionResult.IgnoredDuplicate`).
- **Cancellation & Interruption** maps directly to `CancellationToken`.
- **Resource Scopes (`Scope.Scope`)** map to `IAsyncDisposable` and `await using`.

---

## 2. Concurrency Primitive Equivalents

| Effect v4 Primitive | .NET 10 Equivalent | Usage Pattern |
| :--- | :--- | :--- |
| `Deferred<A, E>` | `TaskCompletionSource<A>` | Asynchronously awaiting a single future resolution or failure |
| `Queue.Queue<T>` | `System.Threading.Channels.Channel<T>` | High-throughput async message passing with backpressure |
| `Hub<T>` / `PubSub<T>` | `Channel<T>` fan-out or `Subject<T>` | Event distribution across multiple listeners |
| `Fiber<A>` | `Task<A>` / Background Task | Long-running or detached execution units |
| `FiberSet` | `TaskGroup` or `List<Task>` | Managing collections of concurrent worker fibers |
| `Ref<T>` | `Interlocked` / `Volatile` / atomic class | Thread-safe in-memory state updates |
| `Semaphore` / `Mutex` | `SemaphoreSlim` | Async lock and concurrency bounding |
| `Scope.addFinalizer` | `IAsyncDisposable.DisposeAsync` | Safe cleanup when leaving a boundary or on shutdown |

---

## 3. Case Study: Session Run Coordinator

The `SessionRunCoordinator` is one of the most critical concurrency controllers in OpenCode V2. It ensures:
1. Only one drain execution runs per session at a time.
2. Concurrent wake requests coalesce into a follow-up drain.
3. Callers can join active runs or wait for idle transitions.
4. Interruption cooperatively cancels the active session drain.

### TypeScript Source Summary (`packages/core/src/session/run-coordinator.ts`)
```ts
type Execution<E, Reason> = {
  readonly done: Deferred.Deferred<void, E>
  owner?: Fiber.Fiber<void>
  scope: Promotable
  pendingWake?: Promotable
  stopping: boolean
}
```

### Modern C# Translation
```csharp
namespace OpenCode.Core.Session;

using System.Collections.Concurrent;

public sealed class SessionRunCoordinator<TKey, TReason> : IAsyncDisposable
    where TKey : notnull
{
    private readonly Func<TKey, bool, CancellationToken, Task> _drain;
    private readonly Func<TKey, Task>? _started;
    private readonly Func<TKey, Exception?, TReason?, Task>? _settled;

    private readonly ConcurrentDictionary<TKey, ExecutionState> _executions = new();
    private readonly CancellationTokenSource _disposalCts = new();

    public SessionRunCoordinator(
        Func<TKey, bool, CancellationToken, Task> drain,
        Func<TKey, Task>? started = null,
        Func<TKey, Exception?, TReason?, Task>? settled = null)
    {
        _drain = drain;
        _started = started;
        _settled = settled;
    }

    private sealed class ExecutionState
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Cts = new();
        public bool HasPendingWake;
        public bool Stopping;
        public TReason? InterruptionReason;
        public Task? RunningTask;
    }

    public bool IsActive(TKey key) => _executions.ContainsKey(key);

    public IReadOnlyCollection<TKey> ActiveKeys => _executions.Keys.ToArray();

    public void Wake(TKey key)
    {
        while (true)
        {
            if (_executions.TryGetValue(key, out var active))
            {
                lock (active)
                {
                    if (!active.Stopping)
                    {
                        active.HasPendingWake = true;
                        return;
                    }
                }
            }

            var created = new ExecutionState();
            if (_executions.TryAdd(key, created))
            {
                created.RunningTask = Task.Run(() => RunExecutionLoopAsync(key, created));
                return;
            }
        }
    }

    private async Task RunExecutionLoopAsync(TKey key, ExecutionState execution)
    {
        Exception? caughtException = null;
        try
        {
            if (_started is not null)
            {
                await _started(key);
            }

            bool force = false;
            while (!execution.Cts.IsCancellationRequested)
            {
                lock (execution)
                {
                    execution.HasPendingWake = false;
                }

                await _drain(key, force, execution.Cts.Token);

                lock (execution)
                {
                    if (!execution.HasPendingWake || execution.Stopping)
                    {
                        break;
                    }
                    force = false;
                }
            }
        }
        catch (OperationCanceledException) when (execution.Cts.IsCancellationRequested)
        {
            // Expected interruption
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }
        finally
        {
            _executions.TryRemove(key, out _);

            if (_settled is not null)
            {
                await _settled(key, caughtException, execution.InterruptionReason);
            }

            if (caughtException is not null)
            {
                execution.Done.TrySetException(caughtException);
            }
            else
            {
                execution.Done.TrySetResult();
            }

            execution.Cts.Dispose();
        }
    }

    public async Task<bool> InterruptAsync(TKey key, TReason? reason = default)
    {
        if (!_executions.TryGetValue(key, out var execution))
        {
            return false;
        }

        lock (execution)
        {
            execution.Stopping = true;
            execution.InterruptionReason = reason;
            execution.HasPendingWake = false;
            execution.Cts.Cancel();
        }

        return true;
    }

    public async Task AwaitIdleAsync(TKey key)
    {
        if (_executions.TryGetValue(key, out var execution))
        {
            await execution.Done.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposalCts.Cancel();
        var pending = _executions.Values.Select(e => e.Done.Task).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _disposalCts.Dispose();
    }
}
```

---

## 4. Channels and Backpressure (`EventFeed`)

In TypeScript, `EventFeed` uses bounded queues (`Queue.dropping`) with 4,096 items to ensure one slow subscriber does not impact publication.

In .NET 10, use `System.Threading.Channels`:

```csharp
namespace OpenCode.Server;

public sealed class EventFeedSubscriber
{
    private readonly Channel<string> _channel;

    public EventFeedSubscriber(int capacity = 4096)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite, // Dropping behavior on lag
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
```
