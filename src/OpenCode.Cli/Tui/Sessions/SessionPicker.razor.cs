namespace OpenCode.Cli.Tui.Sessions;

using System.Globalization;
using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Tabs;
using OpenTui.Blazor;
using OpenTui.Blazor.Components;

public partial class SessionPicker : IDisposable
{
    [Inject] public TimeProvider Clock { get; set; } = TimeProvider.System;
    [Parameter, EditorRequired] public Func<SessionPickerQuery, CancellationToken, Task<SessionPickerPage>>? LoadPage { get; set; }
    [Parameter] public Func<SessionInfo, CancellationToken, Task>? SelectSession { get; set; }
    [Parameter] public Func<CancellationToken, Task>? CreateSession { get; set; }
    [Parameter] public Func<SessionId, string, CancellationToken, Task>? RenameSession { get; set; }
    [Parameter] public SessionId? RenameTarget { get; set; }
    [Parameter] public string? InitialTitle { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task>? DeleteSession { get; set; }
    [Parameter] public EventCallback<SessionId> OnDeleted { get; set; }
    [Parameter] public SessionPickerStorage Preferences { get; set; } = new();
    [Parameter] public SessionTabsTheme Theme { get; set; } = SessionTabsTheme.Default;
    [Parameter] public IReadOnlyList<SessionInfo> CachedSessions { get; set; } = [];
    [Parameter] public bool TabsEnabled { get; set; } = true;
    [Parameter] public IReadOnlyList<SessionId> PinnedSessions { get; set; } = [];
    [Parameter] public IReadOnlyList<SessionId> QuickSwitchSlots { get; set; } = [];
    [Parameter] public Func<SessionId, CancellationToken, Task>? TogglePin { get; set; }
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public string RenameShortcut { get; set; } = "Ctrl+R";
    [Parameter] public string DeleteShortcut { get; set; } = "Ctrl+D";
    [Parameter] public string PinShortcut { get; set; } = "Ctrl+F";
    [Parameter] public string? QuickSwitchHint { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public SessionId? Current { get; set; }
    [Parameter] public IReadOnlySet<SessionId> ActiveSessionIds { get; set; } = new HashSet<SessionId>();
    [Parameter] public string? LocationLabel { get; set; }
    [Parameter] public bool AllProjects { get; set; } = true;
    [Parameter] public EventCallback<bool> AllProjectsChanged { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; } = ModalSize.Large;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<SessionInfo> _sessions = [];
    private IReadOnlyList<SessionPickerRow> _rows = [];
    private string _search = "";
    private int _cursor;
    private int _selected;
    private string? _nextCursor;
    private string? _error;
    private string? _preferenceError;
    private bool _loading;
    private bool _busy;
    private bool _closed;
    private bool _reload;
    private char? _surrogate;
    private SessionId? _toDelete;
    private SessionInfo? _renaming;
    private string _rename = "";
    private int _renameCursor;
    private CancellationTokenSource? _debounce;
    private readonly HashSet<SessionId> _deleted = [];
    private int _spinner;
    private int Limit => Math.Max(1, TerminalHeight / 2 - 6);

    protected override async Task OnInitializedAsync()
    {
        if (RenameTarget is not null) { _rename = InitialTitle ?? ""; _renameCursor = _rename.Length; return; }
        _ = AnimateSpinner();
        try { AllProjects = await Preferences.LoadAsync(AllProjects, _lifetime.Token); await AllProjectsChanged.InvokeAsync(AllProjects); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _preferenceError = $"Could not load session-list preference: {error.Message}"; }
        await Load(false);
    }
    protected override void OnParametersSet() => Project();

    private void Project()
    {
        var current = _rows.ElementAtOrDefault(_selected)?.Session.Id ?? Current;
        var now = Clock.GetLocalNow();
        _rows = _sessions.Where(session => session.ParentId is null && !_deleted.Contains(session.Id))
            .OrderBy(session => !TabsEnabled && PinnedSessions.ToList().IndexOf(session.Id) is var pin && pin >= 0 ? pin : int.MaxValue)
            .ThenByDescending(session => session.Time.Updated)
            .Select(session => SessionPickerRow.From(session, ActiveSessionIds.Contains(session.Id), now) with
            { Category = !TabsEnabled && PinnedSessions.Contains(session.Id) ? "Pinned" : SessionPickerRow.From(session, false, now).Category }).ToArray();
        _selected = Math.Max(0, _rows.ToList().FindIndex(row => row.Session.Id == current));
    }

    private IEnumerable<(string? Header, SessionPickerRow? Session, int Index)> VisibleRows()
    {
        var rows = new List<(string? Header, SessionPickerRow? Session, int Index)>();
        string? category = null;
        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows[index].Category != category) rows.Add((_rows[index].Category, null, index));
            category = _rows[index].Category;
            rows.Add((null, _rows[index], index));
        }
        var focus = rows.FindIndex(row => row.Session is not null && row.Index == _selected);
        return rows.Skip(Math.Max(0, focus - Limit + 1)).Take(Limit);
    }

    private async Task Load(bool more)
    {
        if (_closed || _busy) return;
        if (_loading)
        {
            if (!more) _reload = true;
            return;
        }
        if (LoadPage is null)
        {
            _error = "Session loading is not connected to the controller.";
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _reload = false;
                _error = null;
                var query = new SessionPickerQuery(_search.Trim(), more ? _nextCursor : null, AllProjects);
                try
                {
                    var page = await LoadPage(query, _lifetime.Token);
                    if (_closed) return;
                    // A changed query waits for the in-flight page, then loads once with the latest text.
                    if (_reload || query.Search != _search.Trim() || query.AllProjects != AllProjects) { _reload = true; more = false; continue; }
                    _sessions = (more ? _sessions.Concat(page.Sessions) : page.Sessions)
                        .Where(session => !_deleted.Contains(session.Id)).DistinctBy(session => session.Id).ToArray();
                    _nextCursor = page.NextCursor;
                    Project();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    if (_closed) return;
                    if (!_reload)
                    {
                        _error = $"Could not load sessions: {error.Message}";
                        _sessions = CachedSessions.Where(session => AllProjects || session.Location.Directory == CurrentDirectory)
                            .Where(session => SessionPickerRow.From(session, false, Clock.GetLocalNow()).Title.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                        Project();
                    }
                }
                more = false;
            } while (_reload && !_closed);
        }
        finally { _loading = false; }
    }

    private async Task Apply(bool create)
    {
        if (_busy || _closed || _loading) return;
        var session = _rows.ElementAtOrDefault(_selected)?.Session;
        if (create ? CreateSession is null : session is null) return;
        if (!create && SelectSession is null)
        {
            _error = "Session selection is not connected to the controller.";
            return;
        }
        _busy = true;
        _error = null;
        try
        {
            if (create) await CreateSession!(_lifetime.Token);
            else await SelectSession!(session!, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            if (!_closed) _error = $"Could not {(create ? "create" : "open")} session: {error.Message}";
            return;
        }
        finally { _busy = false; }
        await Close();
    }

    private async Task Close()
    {
        if (_closed) return;
        _closed = true;
#pragma warning disable MA0042 // Stop the picker and its callbacks before notifying the parent that it closed.
        _lifetime.Cancel();
#pragma warning restore MA0042
        await OnClose.InvokeAsync();
    }

    private async Task Key(TerminalKeyEventArgs args)
    {
        if (_renaming is not null || RenameTarget is not null) { await RenameKey(args); return; }
        var key = args.Key;
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Escape || control && key.Key == ConsoleKey.C && _search.Length == 0)
        {
            await Close();
            return;
        }
        if (_busy || _closed) return;
        if (key.Key == ConsoleKey.Enter) { await Apply(false); return; }
        if (control && key.Key == ConsoleKey.N) { await Apply(true); return; }
        var command = ResolveCommand is not null ? ResolveCommand(key) : control ? key.Key switch
        { ConsoleKey.R => "session.rename", ConsoleKey.D => "session.delete", ConsoleKey.F => "session.pin.toggle", _ => null } : null;
        if (command == "session.rename" && RenameSession is not null) { BeginRename(); return; }
        if (command == "session.delete" && DeleteSession is not null) { await Delete(); return; }
        if (command == "session.pin.toggle" && !TabsEnabled && TogglePin is not null && _rows.ElementAtOrDefault(_selected) is { } pin)
        {
            try { await TogglePin(pin.Session.Id, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { if (!_closed) _error = $"Could not update session pin: {error.Message}"; }
            return;
        }
        if (control && key.Key == ConsoleKey.R && RenameSession is null || control && key.Key == ConsoleKey.G) { await Load(false); return; }
        if (control && key.Key == ConsoleKey.L) { if (_nextCursor is not null) await Load(true); return; }
        if (control && key.Key == ConsoleKey.A)
        {
            AllProjects = !AllProjects;
            _toDelete = null;
            try { await Preferences.SaveAsync(AllProjects, _lifetime.Token, Clock); _preferenceError = null; }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { _preferenceError = $"Could not save session-list preference: {error.Message}"; }
            await AllProjectsChanged.InvokeAsync(AllProjects);
            _sessions = [];
            _nextCursor = null;
            Project();
            await Load(false);
            return;
        }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.PageUp or ConsoleKey.PageDown)
        {
            if (_rows.Count == 0) return;
            _toDelete = null;
            _selected = key.Key switch
            {
                ConsoleKey.Home => 0,
                ConsoleKey.End => _rows.Count - 1,
                ConsoleKey.UpArrow => (_selected + _rows.Count - 1) % _rows.Count,
                ConsoleKey.DownArrow => (_selected + 1) % _rows.Count,
                ConsoleKey.PageUp => Math.Max(0, _selected - 10),
                _ => Math.Min(_rows.Count - 1, _selected + 10)
            };
            return;
        }
        var before = _search;
        var boundaries = StringInfo.ParseCombiningCharacters(_search);
        var previous = boundaries.LastOrDefault(index => index < _cursor);
        var next = boundaries.FirstOrDefault(index => index > _cursor, _search.Length);
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: _cursor = previous; break;
            case ConsoleKey.RightArrow: _cursor = next; break;
            case ConsoleKey.C when control:
            case ConsoleKey.U when control: _search = ""; _cursor = 0; break;
            case ConsoleKey.Backspace when _cursor > 0: _search = _search.Remove(previous, _cursor - previous); _cursor = previous; break;
            case ConsoleKey.Delete when _cursor < _search.Length: _search = _search.Remove(_cursor, next - _cursor); break;
            default:
                if (control || char.IsControl(key.KeyChar)) break;
                if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; break; }
                var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is char high ? new string([high, key.KeyChar]) : "" : key.KeyChar.ToString();
                _surrogate = null;
                _search = _search.Insert(_cursor, text);
                _cursor += text.Length;
                break;
        }
        if (before != _search) await Search();
    }

    private async Task Search()
    {
        _toDelete = null;
        _nextCursor = null;
#pragma warning disable MA0042 // Cancel/dispose the old debounce synchronously before installing the new search generation.
        _debounce?.Cancel();
#pragma warning restore MA0042
        _debounce?.Dispose();
        var debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _debounce = debounce;
        try { await Task.Delay(TimeSpan.FromMilliseconds(150), Clock, debounce.Token); await Load(false); }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested) { }
    }

    private async Task Paste(TerminalPasteEventArgs args)
    {
        if (_busy || _closed) return;
        var text = string.Concat(args.Text.Where(character => !char.IsControl(character)));
        if (_renaming is not null || RenameTarget is not null) { _rename = _rename.Insert(_renameCursor, text); _renameCursor += text.Length; return; }
        _search = _search.Insert(_cursor, text);
        _cursor += text.Length;
        await Search();
    }

    public void Dispose()
    {
        _closed = true;
        _lifetime.Cancel();
        _debounce?.Cancel();
        _debounce?.Dispose();
        _lifetime.Dispose();
    }

    [Parameter] public string? CurrentDirectory { get; set; }

    private string RowTitle(SessionPickerRow row) => _toDelete == row.Session.Id ? $"Press {DeleteShortcut} again to confirm" : row.Title;
    private string Gutter(SessionPickerRow row) => row.Active ? new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" }[_spinner % 10]
        : !TabsEnabled && QuickSwitchSlots.ToList().IndexOf(row.Session.Id) is var slot && slot >= 0 ? (slot + 1).ToString(CultureInfo.CurrentCulture) : " ";
    private void HoverRow(int index) { if (_selected != index) _toDelete = null; _selected = index; }
    private Task ClickRow(int index, TerminalPointerEventArgs args) { args.Handled = true; HoverRow(index); return Apply(false); }
    private void BeginRename()
    {
        _renaming = _rows.ElementAtOrDefault(_selected)?.Session;
        if (_renaming is null) return;
        _rename = _renaming.Title ?? _rows[_selected].Title;
        _renameCursor = _rename.Length;
        _toDelete = null;
    }

    private async Task Delete()
    {
        var session = _rows.ElementAtOrDefault(_selected)?.Session;
        if (session is null || DeleteSession is null || _busy) return;
        if (_toDelete != session.Id) { _toDelete = session.Id; return; }
        _busy = true;
        try
        {
            await DeleteSession(session.Id, _lifetime.Token);
            if (_closed) return;
            _deleted.Add(session.Id);
            _sessions = _sessions.Where(item => item.Id != session.Id).ToArray();
            _toDelete = null;
            Project();
            await OnDeleted.InvokeAsync(session.Id);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) { _toDelete = null; _error = $"Failed to delete session: {error.Message}"; } }
        finally { _busy = false; }
    }

    private async Task RenameKey(TerminalKeyEventArgs args)
    {
        var key = args.Key;
        if (key.Key == ConsoleKey.Escape) { await Close(); return; }
        if (_busy) return;
        if (key.Key == ConsoleKey.Enter)
        {
            var id = _renaming?.Id ?? RenameTarget;
            if (_rename.Trim().Length == 0 || RenameSession is null || id is null) return;
            _busy = true;
            try { await RenameSession(id.Value, _rename.Trim(), _lifetime.Token); await Close(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { if (!_closed) _error = $"Failed to rename session: {error.Message}"; }
            finally { _busy = false; }
            return;
        }
        var boundaries = StringInfo.ParseCombiningCharacters(_rename);
        var previous = boundaries.LastOrDefault(index => index < _renameCursor);
        var next = boundaries.FirstOrDefault(index => index > _renameCursor, _rename.Length);
        switch (key.Key)
        {
            case ConsoleKey.Home: _renameCursor = 0; break;
            case ConsoleKey.End: _renameCursor = _rename.Length; break;
            case ConsoleKey.LeftArrow: _renameCursor = previous; break;
            case ConsoleKey.RightArrow: _renameCursor = next; break;
            case ConsoleKey.Backspace when _renameCursor > 0: _rename = _rename.Remove(previous, _renameCursor - previous); _renameCursor = previous; break;
            case ConsoleKey.Delete when _renameCursor < _rename.Length: _rename = _rename.Remove(_renameCursor, next - _renameCursor); break;
            case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control): _rename = ""; _renameCursor = 0; break;
            default:
                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) || char.IsControl(key.KeyChar)) break;
                if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; break; }
                var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is char high ? new string([high, key.KeyChar]) : "" : key.KeyChar.ToString();
                _surrogate = null;
                _rename = _rename.Insert(_renameCursor, text); _renameCursor += text.Length;
                break;
        }
    }

    private async Task AnimateSpinner()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(80), Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                if (ActiveSessionIds.Count > 0) await InvokeAsync(() => { if (_closed) return; _spinner = (_spinner + 1) % 10; StateHasChanged(); });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
}
