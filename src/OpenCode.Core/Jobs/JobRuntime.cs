namespace OpenCode.Core.Jobs;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>One host-owned generic Job registry. Run delegates own domain work; only notification markers are durable.</summary>
public sealed class JobRuntime : IAsyncDisposable
{
    private sealed class Entry(JobInfo info, JobRecovery? recovery, CancellationToken lifetime)
    {
        public JobInfo Info = info;
        public JobRecovery? Recovery { get; } = recovery;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public TaskCompletionSource<JobInfo> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JobInfo> Backgrounded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<SessionId, int> Blocking { get; } = [];
        public bool Detached;
        public Exception? Failure;
        public Task Work = Task.CompletedTask;
    }

    private readonly IJobBackgroundStore _store;
    public TimeProvider Clock { get; }
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _owned = [];
    private volatile bool _closed;

    public JobRuntime(IJobBackgroundStore store, CancellationToken hostLifetime, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!hostLifetime.CanBeCanceled) throw new ArgumentException("Jobs require an owned cancellable host lifetime.", nameof(hostLifetime));
        _store = store;
        Clock = clock ?? TimeProvider.System;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime);
        _token = _lifetime.Token;
    }

    public bool IsClosed => _closed || _token.IsCancellationRequested;

    public async Task<JobInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { RequireOpen(); return _jobs.TryGetValue(id, out var entry) ? Snapshot(entry) : null; }
        finally { _gate.Release(); }
    }

    public async Task<JobInfo> StartAsync(JobStartInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input.Type);
        ArgumentNullException.ThrowIfNull(input.Run);
        input.Recovery?.Validate();
        if (input.NotificationId is { } notification) _ = MessageId.FromExisting(notification.Value);
        await _gate.WaitAsync(ct);
        try
        {
            RequireOpen();
            var id = input.Id ?? "job_" + Identifier.Ascending();
            if (_jobs.TryGetValue(id, out var existing) && existing.Info.Status == JobStatus.Running) return Snapshot(existing);
            var info = new JobInfo(id, input.Type, JobStatus.Running, Clock.GetUtcNow().ToUnixTimeMilliseconds(), input.Title,
                Metadata: input.Metadata is null ? null : new ReadOnlyDictionary<string, JsonElement>(
                    input.Metadata.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal)), NotificationId: input.NotificationId);
            var entry = new Entry(info, input.Recovery, _token);
            _jobs[id] = entry;
            entry.Work = RunAsync(entry, input.Run);
            _owned.Add(entry.Work);
            _ = ObserveAsync(entry.Work);
            return info;
        }
        finally { _gate.Release(); }
    }

    public async Task<JobWaitResult> WaitAsync(string id, int? timeoutMilliseconds = null, CancellationToken ct = default)
    {
        Entry entry;
        await _gate.WaitAsync(ct);
        try
        {
            RequireOpen();
            if (!_jobs.TryGetValue(id, out entry!)) return new(null, false);
            var info = Snapshot(entry);
            if (info.Status != JobStatus.Running) return new(info, false);
            if (timeoutMilliseconds is <= 0) return new(info, true);
        }
        finally { _gate.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
        if (timeoutMilliseconds is null) return new(await entry.Done.Task.WaitAsync(linked.Token), false);
        try { return new(await entry.Done.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds.Value), Clock, linked.Token), false); }
        catch (TimeoutException)
        {
            await _gate.WaitAsync(linked.Token);
            try { return new(Snapshot(entry), true); }
            finally { _gate.Release(); }
        }
    }

    public async Task<JobBlockResult?> BlockAsync(string id, SessionId sessionId, CancellationToken ct = default)
    {
        Entry entry;
        await _gate.WaitAsync(ct);
        try
        {
            RequireOpen();
            if (!_jobs.TryGetValue(id, out entry!)) return null;
            var info = Snapshot(entry);
            if (info.Status != JobStatus.Running) return new(info, false);
            if (entry.Detached) return new(info, true);
            entry.Blocking[sessionId] = entry.Blocking.GetValueOrDefault(sessionId) + 1;
        }
        finally { _gate.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
        try
        {
            var completed = await Task.WhenAny(entry.Done.Task, entry.Backgrounded.Task).WaitAsync(linked.Token);
            return new(await completed, ReferenceEquals(completed, entry.Backgrounded.Task));
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                // A reused terminal job ID owns a new entry; an old wait cannot decrement its dependencies.
                if (_jobs.GetValueOrDefault(id) == entry && entry.Info.Status == JobStatus.Running && !entry.Detached)
                {
                    var count = entry.Blocking.GetValueOrDefault(sessionId);
                    if (count <= 1) entry.Blocking.Remove(sessionId);
                    else entry.Blocking[sessionId] = count - 1;
                }
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<JobInfo?> BackgroundAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RequireOpen();
            if (!_jobs.TryGetValue(id, out var entry) || entry.Info.Status != JobStatus.Running && entry.Recovery is null) return null;
            if (entry.Detached) return Snapshot(entry);
            var info = await PrepareBackgroundAsync(entry);
            CommitBackground(entry, info);
            return info;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<JobInfo>> BackgroundAllAsync(SessionId sessionId, string? type = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RequireOpen();
            var prepared = new List<(Entry Entry, JobInfo Info)>();
            foreach (var entry in _jobs.Values)
            {
                if (entry.Info.Status != JobStatus.Running || entry.Detached || !entry.Blocking.ContainsKey(sessionId)
                    || type is not null && entry.Info.Type != type) continue;
                prepared.Add((entry, await PrepareBackgroundAsync(entry)));
            }
            foreach (var item in prepared) CommitBackground(item.Entry, item.Info);
            return prepared.Select(item => item.Info).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<JobInfo?> CancelAsync(string id, CancellationToken ct = default)
    {
        Entry entry;
        JobInfo info;
        await _gate.WaitAsync(ct);
        try
        {
            RequireOpen();
            if (!_jobs.TryGetValue(id, out entry!)) return null;
            if (entry.Info.Status != JobStatus.Running) return Snapshot(entry);
            info = entry.Info with { Status = JobStatus.Cancelled, CompletedAt = Clock.GetUtcNow().ToUnixTimeMilliseconds() };
            // Explicit cancellation is durable before completion is visible. Shutdown uses Run's
            // interruption path instead and deliberately leaves the preceding marker unchanged.
            await PersistAsync(entry, info);
            entry.Info = info;
            entry.Blocking.Clear();
            entry.Done.TrySetResult(info);
        }
        finally { _gate.Release(); }
        try { await entry.Stop.CancelAsync(); }
        catch (ObjectDisposedException) { /* Run has already completed and closed its cancellation scope. */ }
        await entry.Work;
        return info;
    }

    public async Task<IReadOnlyList<JobBackground>> PendingBackgroundAsync(CancellationToken ct = default)
    {
        RequireOpen();
        var result = await _store.ListAsync(ct);
        foreach (var marker in result) marker.Validate();
        return result;
    }

    /// <summary>Call only after stable completion admission. No success is manufactured when storage removal fails.</summary>
    public Task CompleteBackgroundAsync(MessageId notificationId, CancellationToken ct = default)
    {
        RequireOpen();
        _ = MessageId.FromExisting(notificationId.Value);
        return _store.RemoveAsync(notificationId, ct);
    }

    private async Task<JobInfo> PrepareBackgroundAsync(Entry entry)
    {
        _ = Snapshot(entry);
        var info = entry.Info with { NotificationId = entry.Recovery is null ? entry.Info.NotificationId : entry.Info.NotificationId ?? MessageId.Create() };
        await PersistAsync(entry, info);
        return info;
    }

    private static void CommitBackground(Entry entry, JobInfo info)
    {
        entry.Info = info;
        entry.Detached = true;
        entry.Blocking.Clear();
        entry.Backgrounded.TrySetResult(info);
    }

    private Task PersistAsync(Entry entry, JobInfo info)
    {
        if (entry.Recovery is null || info.NotificationId is not { } notification) return Task.CompletedTask;
        var marker = new JobBackground(info.Id, notification, entry.Recovery, info.Status, info.Output, info.Error);
        marker.Validate();
        // Cancellation can prevent acquiring the operation gate, but cannot turn an already
        // committed marker into a reported cancelled handoff before in-memory ownership changes.
        return _store.SaveAsync(marker, CancellationToken.None);
    }

    private async Task RunAsync(Entry entry, Func<CancellationToken, Task<string>> run)
    {
        await Task.Yield();
        var token = entry.Stop.Token;
        string? output = null;
        string? error = null;
        JobStatus status;
        try
        {
            output = await run(token) ?? throw new InvalidOperationException("Job run returned no string output.");
            token.ThrowIfCancellationRequested();
            status = JobStatus.Completed;
        }
        catch (Exception cause)
        {
            status = cause is OperationCanceledException || token.IsCancellationRequested ? JobStatus.Cancelled : JobStatus.Error;
            error = cause.Message;
            output = null;
        }
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_jobs.GetValueOrDefault(entry.Info.Id) != entry || entry.Info.Status != JobStatus.Running) return;
                var info = entry.Info with { Status = status, CompletedAt = Clock.GetUtcNow().ToUnixTimeMilliseconds(), Output = output, Error = error };
                if (status != JobStatus.Cancelled) await PersistAsync(entry, info);
                entry.Info = info;
                entry.Blocking.Clear();
                entry.Done.TrySetResult(info);
            }
            catch (Exception failure)
            {
                entry.Failure = failure;
                entry.Done.TrySetException(failure);
                _ = entry.Done.Task.Exception;
                throw;
            }
            finally { _gate.Release(); }
        }
        finally { entry.Stop.Dispose(); }
    }

    private async Task ObserveAsync(Task work)
    {
        try { await work; }
        catch (Exception error) { Trace.TraceWarning("Job settlement failed; recovery marker remains ({0}).", error.GetType().Name); }
        finally
        {
            await _gate.WaitAsync();
            try { _owned.Remove(work); }
            finally { _gate.Release(); }
        }
    }

    private static JobInfo Snapshot(Entry entry) => entry.Failure is null ? entry.Info
        : throw new IOException("Job completion could not be persisted; its recovery marker is retained.", entry.Failure);
    private void RequireOpen() { ObjectDisposedException.ThrowIf(_closed, this); _token.ThrowIfCancellationRequested(); }

    public async ValueTask DisposeAsync()
    {
        Task[] owned;
        await _gate.WaitAsync();
        try { if (_closed) return; _closed = true; owned = _owned.ToArray(); }
        finally { _gate.Release(); }
        await _lifetime.CancelAsync();
        await Task.WhenAll(owned).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _lifetime.Dispose();
    }
}
