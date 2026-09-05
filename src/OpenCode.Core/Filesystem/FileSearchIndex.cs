namespace OpenCode.Core.Filesystem;

using OpenCode.Schema;

/// <summary>One Location's ripgrep-backed index, with the source's ten-second refresh policy.
/// Requests cancel their wait, not the host-owned scan. Later refreshes serve the previous snapshot.</summary>
public sealed class FileSearchIndex(LocationInfo location, RipgrepFileScanner scanner, CancellationToken lifetime, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private FuzzyPathSearch.Target[]? _index;
    private Task? _refresh;
    private long _settledAt = long.MinValue;

    public async Task<IReadOnlyList<FileSystemEntry>> FindAsync(string query, FileSystemEntryType? type = null,
        int limit = 50, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (type is not (null or FileSystemEntryType.File or FileSystemEntryType.Directory)) throw new ArgumentException("Invalid filesystem entry type.");
        lifetime.ThrowIfCancellationRequested();
        FuzzyPathSearch.Target[]? index;
        Task? refresh;
        lock (_gate)
        {
            if ((_refresh is null || _refresh.IsCompleted) && (_index is null || _clock.GetTimestampMilliseconds() >= _settledAt + 10_000))
            {
                _refresh = RefreshAsync();
                // Background refresh errors are observed; initial callers still receive the failure.
                _ = _refresh.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            index = _index;
            refresh = _refresh;
        }
        if (index is null)
        {
            await refresh!.WaitAsync(ct);
            lock (_gate) index = _index!;
        }
        return FuzzyPathSearch.Find(index, query, type, limit, ct);
    }

    public Task AwaitRefreshAsync()
    {
        lock (_gate) return _refresh ?? Task.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        await Task.Yield();
        try
        {
            var entries = await scanner.ScanAsync(location, lifetime);
            var index = entries.Select(FuzzyPathSearch.Prepare).ToArray();
            lock (_gate) _index = index;
        }
        finally
        {
            lock (_gate) _settledAt = _clock.GetTimestampMilliseconds();
        }
    }
}
