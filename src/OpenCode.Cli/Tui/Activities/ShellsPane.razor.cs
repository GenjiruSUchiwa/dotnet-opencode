namespace OpenCode.Cli.Tui.Activities;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Theme;
using OpenTui.Blazor;

public partial class ShellsPane : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public SessionHttpClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public SessionId SessionId { get; set; }
    [Parameter, EditorRequired] public ThemeTokens Theme { get; set; } = null!;
    [Parameter] public IReadOnlyList<LocatedShell>? Shells { get; set; }
    [Parameter] public IReadOnlyList<ActivityJobProjection>? Jobs { get; set; }
    [Parameter] public bool Active { get; set; } = true;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public string? Error { get; set; }
    [Parameter] public string KillShortcut { get; set; } = "ctrl+d";
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public Func<ActivityJobProjection, CancellationToken, Task>? CancelJob { get; set; }
    [Parameter] public EventCallback<string> OnFocusRequested { get; set; }
    [Parameter] public EventCallback<LocatedShell> OnFocusShell { get; set; }
    [Parameter] public EventCallback<LocatedShell> OnOpenShell { get; set; }
    [Parameter] public EventCallback<LocatedShell> OnObserved { get; set; }
    [Parameter] public EventCallback<LocatedShell> OnRemoved { get; set; }
    [Parameter] public EventCallback OnRefresh { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<int> OnSwitchTab { get; set; }
    private readonly TerminalScrollState _scroll = new() { AutoFollow = false };
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<LocatedShell> _rows = [];
    private LocatedShell? _inspected;
    private ShellOutputPane? _output;
    private int _selected;
    private bool _runningOnly = true;
    private bool _busy;
    private bool _disposed;
    private bool _wasActive;
    private bool _focusPending;
    private SessionId? _session;
    private string? _error;
    private LocatedShell? Selected => _rows.ElementAtOrDefault(_selected);
    private int ListHeight => Math.Max(1, Math.Min(Math.Min(5, TerminalHeight - 6), _rows.Count));

    protected override void OnParametersSet()
    {
        if (Active && !_wasActive) _focusPending = true;
        var previous = Selected;
        if (_session != SessionId) { _session = SessionId; _selected = 0; _inspected = null; _runningOnly = true; }
        _rows = Shells?.Where(row => ActivityProjection.BelongsTo(row.Info, SessionId) && (row.Info.Status == ShellStatus.Running) == _runningOnly).ToArray() ?? [];
        if (previous is not null && _rows.ToList().FindIndex(row => row.Info.Id == previous.Info.Id && row.Location == previous.Location) is >= 0 and var found) _selected = found;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        if (_inspected is { } inspected && Shells?.FirstOrDefault(row => row.Info.Id == inspected.Info.Id && row.Location == inspected.Location) is { } current)
            _inspected = current;
        if (Selected is { } selected) _scroll.Reveal((selected.Info.Id, selected.Location));
        _wasActive = Active;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!Active || !_focusPending) return;
        _focusPending = false;
        await OnFocusRequested.InvokeAsync(_inspected is null ? "activity-shells" : "activity-shell-output");
    }

    /// <summary>Call after merging the root's shell event. Catalog ownership stays with the root; only an open output reader fetches.</summary>
    public Task RefreshOutputAsync() => _output?.RefreshAsync() ?? Task.CompletedTask;

    private string StatusColor(ShellInfo info) => info.Status == ShellStatus.Timeout ? Theme.Color("text.feedback.warning.default").Hex
        : info.Status == ShellStatus.Exited && info.Exit is not null and not 0 ? Theme.Color("text.feedback.error.default").Hex : Theme.Subdued.Hex;

    private async Task Move(int index, bool center = true)
    {
        if (_rows.Count == 0) return;
        _selected = (index + _rows.Count) % _rows.Count;
        if (Selected is { } row)
        {
            if (center) _scroll.Reveal((row.Info.Id, row.Location), _selected < _scroll.Offset || _selected >= _scroll.Offset + _scroll.ViewportHeight);
            await OnFocusShell.InvokeAsync(row);
        }
    }
    private async Task Hover(int index, TerminalPointerEventArgs args) { args.Handled = true; if (!_busy) await Move(index, false); }
    private async Task OpenClick(int index, TerminalPointerEventArgs args) { args.Handled = true; await Move(index, false); await OpenAsync(); }
    private async Task OpenAsync()
    {
        if (Selected is not { } row) return;
        _inspected = row;
        _focusPending = true;
        await OnOpenShell.InvokeAsync(row);
    }
    private Task BackAsync() { _inspected = null; _output = null; _focusPending = true; return Task.CompletedTask; }
    private async Task ObservedAsync(LocatedShell shell) { _inspected = shell; await OnObserved.InvokeAsync(shell); }
    private void Toggle() { _runningOnly = !_runningOnly; _selected = 0; OnParametersSet(); _scroll.ScrollToStart(); }

    private async Task KillAsync()
    {
        if (_busy || Selected is not { } row || row.Info.Status != ShellStatus.Running) return;
        _busy = true; _error = null;
        try
        {
            await Client.RemoveShellAsync(row.Info.Id, row.Location.Directory, row.Location.WorkspaceId?.Value, _lifetime.Token);
            await OnRemoved.InvokeAsync(row);
            // Removal deletes the retained capture too; do not manufacture a terminal ShellInfo.
            await OnRefresh.InvokeAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _error = ActivityProjection.Error(error); }
        finally { _busy = false; }
    }

    private async Task CancelJobAsync()
    {
        if (_busy || CancelJob is null || Selected is not { } row || ActivityProjection.ShellJob(row, SessionId, Jobs) is not { State: ActivityJobState.Running } job) return;
        _busy = true;
        try { await CancelJob(job, _lifetime.Token); await OnRefresh.InvokeAsync(); }
        catch (Exception error) { _error = ActivityProjection.Error(error); }
        finally { _busy = false; }
    }
    private Task KillClick(TerminalPointerEventArgs args) { args.Handled = true; return KillAsync(); }
    private Task OutputClick(TerminalPointerEventArgs args) { args.Handled = true; return OpenAsync(); }
    private Task ToggleClick(TerminalPointerEventArgs args) { args.Handled = true; Toggle(); return Task.CompletedTask; }
    private Task CancelJobClick(TerminalPointerEventArgs args) { args.Handled = true; return CancelJobAsync(); }

    private async Task Key(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        if (!Active) return;
        var control = args.Key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (args.Key.Key == ConsoleKey.Escape || control && args.Key.Key == ConsoleKey.C) { await OnClose.InvokeAsync(); return; }
        if (args.Key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow) { await OnSwitchTab.InvokeAsync(args.Key.Key == ConsoleKey.LeftArrow ? -1 : 1); return; }
        if (_busy) return;
        var command = ResolveCommand is null ? args.Key.Key switch
        {
            ConsoleKey.UpArrow => "composer.shell.up", ConsoleKey.DownArrow => "composer.shell.down", ConsoleKey.D when control => "composer.shell.kill", _ => null
        } : ResolveCommand(args.Key);
        switch (command)
        {
            case "composer.shell.up": if (_selected == 0) await OnClose.InvokeAsync(); else await Move(_selected - 1); return;
            case "composer.shell.down": await Move(_selected + 1); return;
            case "composer.shell.kill": await KillAsync(); return;
        }
        if (args.Key.Key == ConsoleKey.Enter) await OpenAsync();
        if (control && args.Key.Key == ConsoleKey.A) Toggle();
        if (control && args.Key.Key == ConsoleKey.R) await OnRefresh.InvokeAsync();
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _scroll.Detach(); }
}
