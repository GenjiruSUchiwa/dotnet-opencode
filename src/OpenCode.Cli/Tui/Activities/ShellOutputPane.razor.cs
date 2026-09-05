namespace OpenCode.Cli.Tui.Activities;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Theme;
using OpenTui.Blazor;

/// <summary>Bounded byte-cursor output reader. Waiting consumes root state updates, not an invented wait API or another SSE connection.</summary>
public partial class ShellOutputPane : ComponentBase, IAsyncDisposable
{
    [Parameter, EditorRequired] public SessionHttpClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public LocatedShell Entry { get; set; } = null!;
    [Parameter, EditorRequired] public ThemeTokens Theme { get; set; } = null!;
    [Parameter] public int Height { get; set; } = 6;
    [Parameter] public EventCallback OnBack { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<LocatedShell> OnObserved { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _read = new(1);
    private readonly TerminalScrollState _scroll = new();
    private readonly List<(double Start, double End, string Text)> _pages = [];
    private readonly TaskCompletionSource<ShellInfo> _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (SessionHttpClient Client, ShellId Id, LocationRef Location)? _identity;
    private ShellInfo? _info;
    private double _cursor;
    private double _size;
    private double _displayStart;
    private bool _trimmed;
    private bool _loaded;
    private bool _busy;
    private bool _waiting;
    private bool _disposed;
    private string? _error;
    private string Display => _pages.Count > 0 ? string.Concat(_pages.Select(page => page.Text)) : _error is not null ? "Output unavailable." : !_loaded ? "Reading captured output…"
        : _size == 0 ? "(no output)" : "No bytes returned at this cursor.";

    protected override async Task OnParametersSetAsync()
    {
        var identity = (Client, Entry.Info.Id, Entry.Location);
        if (_identity is null)
        {
            _identity = identity;
            _info = Entry.Info;
            if (_info.Status != ShellStatus.Running) _terminal.TrySetResult(_info);
            await ReadAsync(false);
            return;
        }
        if (_identity != identity) throw new InvalidOperationException("Remount ShellOutputPane when its Client, Shell ID, or execution Location changes.");
        // Shell IDs do not restart. Never downgrade a terminal GET observation with an older feed snapshot.
        if (_info?.Status == ShellStatus.Running || Entry.Info.Status != ShellStatus.Running) _info = Entry.Info;
        if (_info is { Status: not ShellStatus.Running } terminal) _terminal.TrySetResult(terminal);
    }

    public Task RefreshAsync() => InvokeAsync(() => ReadAsync(false));

    private async Task ReadAsync(bool reset)
    {
        if (_disposed) return;
        var ct = _lifetime.Token;
        await _read.WaitAsync(ct);
        _busy = true;
        LocatedShell? observed = null;
        try
        {
            if (reset) { _cursor = _displayStart = 0; _pages.Clear(); _trimmed = false; _scroll.ScrollToStart(); }
            var status = await Client.GetShellAsync(Entry.Info.Id, Entry.Location.Directory, Entry.Location.WorkspaceId?.Value, ct);
            var requested = _cursor;
            var page = (await Client.ReadShellOutputAsync(Entry.Info.Id, new ShellOutputInput(requested, 65_536), Entry.Location.Directory, Entry.Location.WorkspaceId?.Value, ct)).Data;
            ct.ThrowIfCancellationRequested();
            if (page.Cursor > page.Size) throw new InvalidOperationException("The server returned an invalid shell byte cursor.");
            if (page.Cursor < requested)
            {
                _pages.Clear(); _trimmed = false; _displayStart = page.Cursor;
                _error = "Capture size changed. Reset to read from the beginning.";
            }
            else _error = null;
            if (page.Output.Length > 0) _pages.Add((requested, page.Cursor, page.Output));
            // Display retention is separate from protocol truncation/cursor. Never derive the next
            // byte cursor from decoded UTF-16 string length, including multi-byte characters.
            while (_pages.Count > 4) { _displayStart = _pages[0].End; _pages.RemoveAt(0); _trimmed = true; }
            _cursor = page.Cursor; _size = page.Size; _loaded = true; _info = status.Data;
            if (_info.Status != ShellStatus.Running) _terminal.TrySetResult(_info);
            observed = new LocatedShell(_info, Entry.Location);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            _error = ActivityProjection.Error(error);
            if (error is SessionApiException { StatusCode: System.Net.HttpStatusCode.NotFound })
            { _terminal.TrySetException(error); _ = _terminal.Task.Exception; }
        }
        finally { _busy = false; _read.Release(); if (!_disposed) StateHasChanged(); }
        if (observed is not null && !_disposed) await OnObserved.InvokeAsync(observed);
    }

    /// <summary>Root must keep supplying authoritative shell snapshots (or call RefreshAsync on its shared event feed).</summary>
    public async Task<ShellInfo> WaitForExitAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        await RefreshAsync().WaitAsync(linked.Token);
        if (_error is not null && !_terminal.Task.IsCompleted) throw new InvalidOperationException(_error);
        var terminal = await _terminal.Task.WaitAsync(linked.Token);
        await RefreshAsync().WaitAsync(linked.Token);
        return terminal;
    }

    private async Task BeginWaitAsync()
    {
        if (_waiting || _disposed) return;
        _waiting = true;
        try { await WaitForExitAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _error = ActivityProjection.Error(error); }
        finally { _waiting = false; if (!_disposed) await InvokeAsync(StateHasChanged); }
    }

    private async Task Key(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        if (args.Key.Modifiers.HasFlag(ConsoleModifiers.Control) && args.Key.Key == ConsoleKey.C)
        { await (OnClose.HasDelegate ? OnClose : OnBack).InvokeAsync(); return; }
        if (args.Key.Key == ConsoleKey.Escape) { await OnBack.InvokeAsync(); return; }
        if (args.Key.Key == ConsoleKey.UpArrow) { _scroll.ScrollBy(-1); return; }
        if (args.Key.Key == ConsoleKey.DownArrow) { _scroll.ScrollBy(1); return; }
        if (args.Key.Key == ConsoleKey.PageUp) { _scroll.ScrollBy(-Math.Max(1, Height)); return; }
        if (args.Key.Key == ConsoleKey.PageDown) { _scroll.ScrollBy(Math.Max(1, Height)); return; }
        if (_busy) return;
        if (args.Key.Key == ConsoleKey.Enter) await ReadAsync(false);
        if (args.Key.Key == ConsoleKey.Home) await ReadAsync(true);
        if (args.Key.Key == ConsoleKey.End) _scroll.ScrollToEnd();
        if (args.Key.Key == ConsoleKey.W) { _ = BeginWaitAsync(); }
    }
    private Task ReadClick(TerminalPointerEventArgs args) { args.Handled = true; return _busy ? Task.CompletedTask : ReadAsync(false); }
    private Task ResetClick(TerminalPointerEventArgs args) { args.Handled = true; return _busy ? Task.CompletedTask : ReadAsync(true); }
    private Task WaitClick(TerminalPointerEventArgs args) { args.Handled = true; _ = BeginWaitAsync(); return Task.CompletedTask; }
    private Task BackClick(TerminalPointerEventArgs args) { args.Handled = true; return OnBack.InvokeAsync(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        _terminal.TrySetCanceled(CancellationToken.None);
        _scroll.Detach();
        // Only local reads/waiters are cancelled. Closing output never kills the command.
    }
}
