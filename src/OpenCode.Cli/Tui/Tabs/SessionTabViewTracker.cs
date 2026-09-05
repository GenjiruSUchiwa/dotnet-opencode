namespace OpenCode.Cli.Tui.Tabs;

using OpenCode.Schema;

/// <summary>Root calls Update for route/focus/session-watermark changes, including when tabs are disabled.
/// The callback binds SessionHttpClient.ViewAsync. Acknowledgements never use the wall clock.</summary>
public sealed class SessionTabViewTracker(Func<SessionId, double, CancellationToken, Task> view, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, long> _acknowledged = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pending;
    private Task _worker = Task.CompletedTask;
    private (SessionId Id, long Idle)? _selected;

    public void Update(SessionInfo? root, bool focused)
    {
        var selected = focused && root?.Time.Idle is { } idle && (root.Time.Viewed is null || idle > root.Time.Viewed)
            ? ((SessionId Id, long Idle)?)(root.Id, idle.ToUnixTimeMilliseconds()) : null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            if (selected == _selected) return;
            _selected = selected;
            _pending?.Cancel();
            _pending?.Dispose();
            if (selected is not { } target || _acknowledged.GetValueOrDefault(target.Id, -1) >= target.Idle) return;
            var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pending = pending;
            var previous = _worker;
            _worker = Acknowledge(previous, target, pending.Token);
        }
    }

    private async Task Acknowledge(Task previous, (SessionId Id, long Idle) target, CancellationToken ct)
    {
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await view(target.Id, target.Idle, ct);
                    lock (_gate) _acknowledged[target.Id] = target.Idle;
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    // Source retries failed view reporting while this root remains visibly focused.
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250 * (1 << Math.Min(attempt++, 5)), 5000)), _clock, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate) { _lifetime.Cancel(); _pending?.Cancel(); worker = _worker; }
        await worker.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _pending?.Dispose(); _lifetime.Dispose();
    }
}
