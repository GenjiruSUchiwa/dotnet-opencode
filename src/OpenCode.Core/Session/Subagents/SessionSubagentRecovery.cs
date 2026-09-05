namespace OpenCode.Core.Session.Subagents;

using System.Collections.Concurrent;
using OpenCode.Core.Event;
using OpenCode.Core.Jobs;
using OpenCode.Schema;

public sealed partial class SessionSubagents
{
    private const string Exhausted = "Execution was interrupted repeatedly and will not be resumed automatically.";
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    /// <summary>
    /// Managed-startup only: the caller must own the server registration and know the previous process is dead.
    /// Restores this host's subagent jobs first, then invokes the existing top-level Session recovery coordinator.
    /// Shell/unknown background domains and unsupported controls remain guarded, not faked as complete.
    /// </summary>
    public async Task<SessionRecoveryReport> RecoverSuspendedAsync(int maxAttempts = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxAttempts);
        ObjectDisposedException.ThrowIf(_closed, this);
        var token = _token;
        await _recoveryGate.WaitAsync(token);
        var suspended = new ConcurrentDictionary<SessionId, byte>();
        try
        {
            var pending = (await Jobs.PendingBackgroundAsync(token)).Where(item => item.Recovery is JobSubagentRecovery recovery && item.Id == recovery.ChildSessionId.Value)
                .Select(item =>
                {
                    var recovery = (JobSubagentRecovery)item.Recovery;
                    return new SubagentBackground(recovery.ChildSessionId, item.NotificationId,
                        new SubagentRecovery(recovery.ParentSessionId, recovery.ChildSessionId, recovery.Agent, recovery.Description),
                        item.Status switch { JobStatus.Running => "running", JobStatus.Completed => "completed", JobStatus.Error => "error", JobStatus.Cancelled => "cancelled", _ => throw new InvalidOperationException("Unknown job status.") },
                        item.Output, item.Error);
                }).ToArray();
            var active = _execution.ActiveSessionIds;
            var children = pending.Where(item => item.Status == "running").Select(item => item.Recovery.ChildSessionId).ToHashSet();
            foreach (var id in (await _sessions.ListSuspendedAsync(token)).Concat(children).Where(id => !active.Contains(id))) suspended.TryAdd(id, 0);
            await _restart.ReleaseChildClaimsAsync(children.Concat(active).ToHashSet(), token);

            var scheduled = new List<SessionId>();
            var exhausted = new List<SessionId>();
            var skipped = new List<SessionId>();
            var blocked = new Dictionary<SessionId, string>();
            var managed = new HashSet<SessionId>();
            var notifications = new HashSet<MessageId>();
            var remaining = new Queue<SubagentBackground>(pending);
            var withoutProgress = remaining.Count;
            while (remaining.TryDequeue(out var item))
            {
                token.ThrowIfCancellationRequested();
                var child = await _sessions.GetSessionAsync(item.Recovery.ChildSessionId, token);
                var parent = await _sessions.GetSessionAsync(item.Recovery.ParentSessionId, token);
                if (child is null || child.ParentId != item.Recovery.ParentSessionId || parent is null)
                {
                    await Jobs.CompleteBackgroundAsync(item.NotificationId, token);
                    skipped.Add(item.Recovery.ChildSessionId);
                    blocked.Remove(item.Recovery.ChildSessionId);
                    withoutProgress = remaining.Count;
                    continue;
                }
                var current = await Jobs.GetAsync(child.Id.Value, token);
                if (current?.Status == JobStatus.Running)
                {
                    managed.Add(child.Id); notifications.Add(item.NotificationId); skipped.Add(child.Id);
                    blocked.Remove(child.Id);
                    withoutProgress = remaining.Count;
                    continue;
                }
                if (item.Status != "running")
                {
                    await DeliverAsync(item.Recovery, item.NotificationId, new Completion(item.Status, item.Output, item.Error), suspended.ContainsKey);
                    notifications.Add(item.NotificationId);
                    blocked.Remove(child.Id);
                    withoutProgress = remaining.Count;
                    continue;
                }
                if (_execution.IsActive(child.Id))
                {
                    // Source recovery never replaces a process-local owner or increments its resume budget.
                    managed.Add(child.Id); notifications.Add(item.NotificationId); skipped.Add(child.Id);
                    blocked.Remove(child.Id);
                    withoutProgress = remaining.Count;
                    continue;
                }
                var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resumed = _execution.ResumeRecoveredChildAsync(child.Id, stop.Token, maxAttempts,
                    new RestartScope(managed.ToHashSet(), notifications.Append(item.NotificationId).ToHashSet()), () => registered.TrySetResult(), _token);
                try
                {
                    // Hand the SAME owned drain task to the job; calling Resume again here could force a duplicate step
                    // if a fast recovered child had already settled before the job acquired its observer.
                    await Task.WhenAny(registered.Task, resumed).WaitAsync(token);
                    if (!registered.Task.IsCompletedSuccessfully) await resumed;
                }
                catch (RecoveryNotScheduledException error)
                {
                    stop.Dispose();
                    if (error.Preparation == RestartPreparation.Exhausted) exhausted.Add(child.Id);
                    else skipped.Add(child.Id);
                    await DeliverAsync(item.Recovery, item.NotificationId, new Completion("error", Error: Exhausted), suspended.ContainsKey);
                    notifications.Add(item.NotificationId);
                    blocked.Remove(child.Id);
                    withoutProgress = remaining.Count;
                    continue;
                }
                catch (SessionAlreadyOwnedException)
                {
                    stop.Dispose();
                    managed.Add(child.Id); notifications.Add(item.NotificationId); skipped.Add(child.Id);
                    blocked.Remove(child.Id);
                    withoutProgress = remaining.Count;
                    continue;
                }
                catch (Exception error) when (error is NotSupportedException or SessionMutationInProgressException)
                {
                    stop.Dispose();
                    blocked[child.Id] = error.Message;
                    remaining.Enqueue(item);
                    // Retry only after another dependency progresses; never sleep or poll running work.
                    if (--withoutProgress == 0) break;
                    continue;
                }
                catch
                {
                    // On shutdown, observe the actual owner through settlement before releasing its token source.
                    await resumed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    stop.Dispose();
                    throw;
                }
                var attached = false;
                try
                {
                    var recovery = new JobSubagentRecovery(item.Recovery.ParentSessionId, child.Id, item.Recovery.Agent, item.Recovery.Description);
                    var info = await Jobs.StartAsync(new JobStartInput("subagent", async jobToken =>
                    {
                        try
                        {
                            using var cancellation = jobToken.Register(() => stop.Cancel());
                            return await ExecuteAsync(child.Id, jobToken, resumed);
                        }
                        finally { stop.Dispose(); }
                    }, child.Id.Value, recovery.Description, Recovery: recovery, NotificationId: item.NotificationId), token);
                    attached = true;
                    var promoted = await Jobs.BackgroundAsync(info.Id, token) ?? throw new InvalidOperationException("Recovered subagent job disappeared.");
                    await NotifyWhenDoneAsync(recovery, promoted, suspended.ContainsKey, token);
                }
                catch
                {
                    if (!attached)
                    {
                        await resumed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        stop.Dispose();
                    }
                    throw;
                }
                managed.Add(child.Id);
                notifications.Add(item.NotificationId);
                scheduled.Add(child.Id);
                blocked.Remove(child.Id);
                withoutProgress = remaining.Count;
            }

            var roots = await _execution.RecoverSuspendedAsync(token, maxAttempts, new RestartScope(managed, notifications));
            foreach (var pair in roots.Blocked) blocked[pair.Key] = pair.Value;
            return new SessionRecoveryReport(scheduled.Concat(roots.Scheduled).Distinct().ToArray(), exhausted.Concat(roots.Exhausted).Distinct().ToArray(),
                skipped.Concat(roots.Skipped).Distinct().ToArray(), blocked);
        }
        finally
        {
            // Later completions are new work and may wake a parent normally, including one that exhausted an older execution.
            suspended.Clear();
            _recoveryGate.Release();
        }
    }
}
