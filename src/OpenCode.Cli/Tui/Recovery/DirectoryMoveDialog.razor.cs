namespace OpenCode.Cli.Tui.Recovery;

using System.Globalization;
using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenTui.Blazor;

public partial class DirectoryMoveDialog : IDisposable
{
    [Parameter, EditorRequired] public SessionId SessionId { get; set; }
    [Parameter, EditorRequired] public ProjectId ProjectId { get; set; }
    [Parameter, EditorRequired] public LocationRef Current { get; set; } = default!;
    [Parameter, EditorRequired] public RecoveryTheme Theme { get; set; } = default!;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public Func<ProjectId, LocationRef, CancellationToken, Task<RecoveryDirectoryPage>>? LoadDirectories { get; set; }
    [Parameter] public Func<LocationRef, CancellationToken, Task<RecoveryDirectoryPage>>? LoadChildren { get; set; }
    [Parameter] public Func<SessionId, LocationRef, InboxDeliveryMode?, CancellationToken, Task<SessionMoveSnapshot>>? SubmitMove { get; set; }
    [Parameter] public SessionMoveSnapshot? MoveState { get; set; }
    [Parameter] public EventCallback<SessionMoveSnapshot> OnAdmitted { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<RecoveryDirectoryOption>? _directories;
    private string _filter = "";
    private int _cursor;
    private int _selected;
    private bool _manual;
    private bool _loading;
    private bool _submitting;
    private bool _closed;
    private string? _error;
    private SessionMoveSnapshot? _submitted;
    private IReadOnlyList<RecoveryDirectoryOption> Rows => (_directories ?? []).Where(row => row.Directory.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    private bool CanSubmit => !_closed && !_submitting && SubmitMove is not null && Current.WorkspaceId is null
        && MoveState?.Pending != true && _submitted?.Pending != true;
    private int RowBudget => Math.Max(1, Math.Min(16, TerminalHeight - TerminalHeight / 4 - 10));

    protected override async Task OnInitializedAsync()
    {
        if (Current.WorkspaceId is null) await Load();
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Current and Theme are actual Blazor component parameters.")]
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(Current); ArgumentNullException.ThrowIfNull(Theme);
        if (MoveState?.Phase == SessionMovePhase.LocationChanged) _submitted = MoveState;
    }

    private IEnumerable<(RecoveryDirectoryOption Option, int Index)> VisibleRows() => Rows.Select((row, index) => (row, index))
        .Skip(Math.Max(0, _selected - RowBudget + 1)).Take(RowBudget);

    private async Task Load(LocationRef? directory = null)
    {
        if (_closed || _loading || _submitting) return;
        _loading = true;
        try
        {
            var page = directory is not null && LoadChildren is not null ? await LoadChildren(directory, _lifetime.Token)
                : LoadDirectories is not null ? await LoadDirectories(ProjectId, Current, _lifetime.Token)
                : throw new InvalidOperationException("Server directory discovery is unavailable. Enter a destination manually.");
            if (_closed) return;
            _directories = page.Directories;
            _filter = ""; _cursor = _selected = 0; _error = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) _error = $"Could not load server directories: {SessionClientAdapter.Describe(error)}"; }
        finally { _loading = false; }
    }

    private async Task Move(string directory)
    {
        if (!CanSubmit || string.IsNullOrWhiteSpace(directory)) return;
        _submitting = true; _error = null;
        try
        {
            var result = await SubmitMove!(SessionId, new LocationRef(directory), null, _lifetime.Token);
            if (_closed) return;
            if (result.SessionId != SessionId) throw new InvalidOperationException("Move admission returned a different Session.");
            _submitted = result;
            if (result.Phase is SessionMovePhase.Admitted or SessionMovePhase.LocationChanged)
            { await OnAdmitted.InvokeAsync(result); await Close(); return; }
            _error = result.Message + (result.Error is null ? "" : "\n" + result.Error);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) _error = SessionClientAdapter.Describe(error); }
        finally { _submitting = false; }
    }

    private async Task Key(TerminalKeyEventArgs args)
    {
        var key = args.Key;
        if (key.Key == ConsoleKey.Escape)
        {
            if (_manual && !_submitting) { _manual = false; _filter = ""; _cursor = 0; return; }
            await Close(); return;
        }
        if (_closed || _submitting) return;
        if (key.Key == ConsoleKey.Enter)
        { if (_manual) await Move(_filter); else if (Rows.ElementAtOrDefault(_selected) is { } row) await Move(row.Directory); return; }
        if (!_manual && key.Key == ConsoleKey.RightArrow && LoadChildren is not null)
        { await Browse(); return; }
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.R) { await Load(); return; }
        if (!_manual && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown)
        {
            var delta = key.Key switch { ConsoleKey.UpArrow => -1, ConsoleKey.DownArrow => 1, ConsoleKey.PageUp => -RowBudget, _ => RowBudget };
            _selected = Math.Clamp(_selected + delta, 0, Math.Max(0, Rows.Count - 1)); return;
        }
        var boundaries = StringInfo.ParseCombiningCharacters(_filter);
        var previous = boundaries.LastOrDefault(value => value < _cursor);
        var next = boundaries.FirstOrDefault(value => value > _cursor, _filter.Length);
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: _cursor = previous; break;
            case ConsoleKey.RightArrow: _cursor = next; break;
            case ConsoleKey.Home: _cursor = 0; break;
            case ConsoleKey.End: _cursor = _filter.Length; break;
            case ConsoleKey.Backspace when _cursor > 0: _filter = _filter.Remove(previous, _cursor - previous); _cursor = previous; break;
            case ConsoleKey.Delete when _cursor < _filter.Length: _filter = _filter.Remove(_cursor, next - _cursor); break;
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control): _filter = ""; _cursor = 0; break;
            default: args.Handled = false; break;
        }
        _selected = Math.Clamp(_selected, 0, Math.Max(0, Rows.Count - 1));
    }

    private void Insert(string value)
    {
        if (_closed || _submitting) return;
        var text = string.Concat(TerminalTextEditing.NormalizePaste(value).Where(character => !char.IsControl(character)));
        _filter = _filter.Insert(_cursor, text); _cursor += text.Length;
        _selected = 0;
    }
    private void Paste(TerminalPasteEventArgs args) => Insert(args.Text);
    private void Text(TerminalTextInputEventArgs args) => Insert(args.Text);
    private Task Browse() => Rows.ElementAtOrDefault(_selected) is { } row && LoadChildren is not null ? Load(new LocationRef(row.Directory)) : Task.CompletedTask;
    private Task Select(int index, TerminalPointerEventArgs args) { args.Handled = true; _selected = index; return Rows.ElementAtOrDefault(index) is { } row ? Move(row.Directory) : Task.CompletedTask; }
    private Task SubmitManual(TerminalPointerEventArgs args) { args.Handled = true; return Move(_filter); }
    private void ShowManual(TerminalPointerEventArgs args) { args.Handled = true; if (_submitting) return; _manual = true; _filter = Current.Directory; _cursor = _filter.Length; }
    private Task ShowWorktrees(TerminalPointerEventArgs args) { args.Handled = true; _manual = false; return Load(); }
    private Task RefreshClick(TerminalPointerEventArgs args) { args.Handled = true; return Load(); }
    private Task BrowseClick(TerminalPointerEventArgs args) { args.Handled = true; return Browse(); }
#pragma warning disable MA0042 // Cancellation and its synchronous callbacks precede the dialog-close notification.
    private async Task Close() { if (_closed) return; _closed = true; _lifetime.Cancel(); await OnClose.InvokeAsync(); }
#pragma warning restore MA0042
    public void Dispose() { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
