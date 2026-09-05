namespace OpenCode.Cli.Tui.Dialogs;

using System.Globalization;
using Microsoft.AspNetCore.Components;
using OpenTui.Blazor;
using OpenTui.Blazor.Components;

public partial class DialogSelect<T> : IDisposable
{
    [Parameter] public string Title { get; set; } = "Select";
    [Parameter] public string Placeholder { get; set; } = "Search";
    [Parameter, EditorRequired] public DialogTheme Theme { get; set; } = null!;
    [Parameter] public IReadOnlyList<DialogSelectOption<T>> Options { get; set; } = [];
    [Parameter] public IReadOnlyList<DialogSelectAction<T>> Actions { get; set; } = [];
    [Parameter] public RenderFragment? Footer { get; set; }
    [Parameter] public T? Current { get; set; }
    [Parameter] public bool HasCurrent { get; set; }
    [Parameter] public bool FocusCurrent { get; set; } = true;
    [Parameter] public bool Flat { get; set; }
    [Parameter] public bool RenderFilter { get; set; } = true;
    [Parameter] public bool SkipFilter { get; set; }
    [Parameter] public bool PreserveSelection { get; set; }
    [Parameter] public bool SectionNavigation { get; set; }
    [Parameter] public bool Locked { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public double FilterThreshold { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback<T> OnSelect { get; set; }
    [Parameter] public EventCallback<string> OnFilter { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public Action<T>? OnMove { get; set; }
    [Parameter] public Func<ConsoleKeyInfo, Task<bool>>? BeforeKey { get; set; }
    [Parameter] public Func<Func<string, Task<bool>>, IDisposable>? RegisterActionDispatcher { get; set; }
    private Func<Func<string, Task<bool>>, IDisposable>? _registeredActionDispatcher;
    private IDisposable? _actionRegistration;
    private bool _disposed;
    private readonly TerminalScrollState _scroll = new() { AutoFollow = false };
    private IReadOnlyList<DialogSelectOption<T>> _filtered = [];
    private IReadOnlyList<IGrouping<string, DialogSelectOption<T>>> Groups = [];
    private string _query = "";
    private int _cursor;
    private int _selected;
    private bool _initialized;
    private bool _userSelection;
    private bool _sawCurrent;
    private T? _previousCurrent;
    private bool _busy;
    private char? _surrogate;
    private string? _action;
    private string? _error;
    private DialogSelectOption<T>? Selected => _filtered.ElementAtOrDefault(_selected);
    private int ListHeight => Math.Max(1, Math.Min(Groups.Select((group, index) => group.Count() + group.Sum(option => option.Details?.Count ?? 0)
        + (group.Key.Length == 0 ? 0 : index == 0 ? 1 : 2)).Sum(), TerminalHeight / 2 - 6));

    protected override void OnParametersSet()
    {
        var previous = Selected;
        Filter();
        var currentChanged = FocusCurrent && HasCurrent && (!_sawCurrent || !EqualityComparer<T>.Default.Equals(_previousCurrent, Current));
        _previousCurrent = Current;
        _sawCurrent = HasCurrent;
        if (FocusCurrent && HasCurrent && (currentChanged || previous is null && !_userSelection))
        {
            var current = _filtered.ToList().FindIndex(option => EqualityComparer<T>.Default.Equals(option.Value, Current));
            if (current >= 0) { _selected = current; Reveal(true); }
        }
        if (!_initialized)
        {
            _scroll.ScrollToStart();
            _initialized = true;
            if (FocusCurrent && HasCurrent) _selected = Math.Max(0, _filtered.ToList().FindIndex(option => EqualityComparer<T>.Default.Equals(option.Value, Current)));
            Reveal(true);
            return;
        }
        if (!currentChanged && (PreserveSelection || FocusCurrent && HasCurrent) && previous is not null)
        {
            var retained = _filtered.ToList().FindIndex(option => EqualityComparer<T>.Default.Equals(option.Value, previous.Value));
            if (retained >= 0 && retained != _selected) { _selected = retained; Reveal(false); }
        }
        if (FocusCurrent || _filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, Math.Max(0, _filtered.Count - 1));
    }

    private void Filter()
    {
        var filtered = Options.Where(option => !option.Disabled).ToArray();
        if (!SkipFilter && RenderFilter && _query.Length > 0)
            filtered = filtered.Select((option, index) => (Option: option, Index: index,
                    Score: DialogSearch.Score(_query, option.Title, option.Category, option.SearchText)))
                .Where(item => item.Score > 0 && item.Score >= FilterThreshold)
                .OrderByDescending(item => item.Score).ThenBy(item => item.Index).Select(item => item.Option).ToArray();
        Groups = filtered.GroupBy(option => Flat && _query.Length > 0 ? "" : option.Category ?? "").ToArray();
        _filtered = Groups.SelectMany(group => group).ToArray();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (_disposed || Equals(_registeredActionDispatcher, RegisterActionDispatcher)) return;
        _actionRegistration?.Dispose();
        _actionRegistration = null;
        _registeredActionDispatcher = RegisterActionDispatcher;
        _actionRegistration = RegisterActionDispatcher?.Invoke(DispatchActionAsync);
    }

    /// <summary>Invoke a mounted action against the focused row, not the Current marker.
    /// True means the command belongs to this dialog, including a disabled or busy action.</summary>
    public async Task<bool> DispatchActionAsync(string command)
    {
        if (_disposed) return false;
        var handled = false;
        await InvokeAsync(async () =>
        {
            if (_disposed || Actions.FirstOrDefault(action => action.Command == command) is not { } action) return;
            handled = true;
            await RunAction(action);
            if (!_disposed) StateHasChanged();
        });
        return handled;
    }

    public void Dispose()
    {
        _disposed = true;
        _actionRegistration?.Dispose();
        _actionRegistration = null;
    }

    private void Reveal(bool center)
    {
        if (Selected is { } option && option.Value is not null) _scroll.Reveal(option.Value, center);
    }
    private void Move(int index, bool center = true)
    {
        _userSelection = true;
        _action = null;
        if (_filtered.Count == 0) return;
        _selected = (index % _filtered.Count + _filtered.Count) % _filtered.Count;
        if (Selected is { } selected) OnMove?.Invoke(selected.Value);
        Reveal(center);
    }
    private async Task Select(DialogSelectOption<T>? option)
    {
        if (Locked || _busy || Loading || option is null || option.Disabled || !OnSelect.HasDelegate) return;
        _busy = true;
        _error = null;
        try { await OnSelect.InvokeAsync(option.Value); }
        catch (Exception exception) { _error = exception.Message; }
        finally { _busy = false; }
    }
    private async Task ChangeFilter()
    {
        Filter();
        // Without current focus, clearing the query keeps the cursor's position, as upstream does.
        if (_query.Length > 0 || FocusCurrent) _selected = 0;
        else if (_filtered.Count > 0) _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        _userSelection = _query.Length > 0;
        if (_query.Length == 0 && FocusCurrent && HasCurrent)
            _selected = Math.Max(0, _filtered.ToList().FindIndex(option => EqualityComparer<T>.Default.Equals(option.Value, Current)));
        _action = null;
        if (_query.Length > 0 || FocusCurrent)
        {
            if (Selected is { } selected) OnMove?.Invoke(selected.Value);
            _scroll.ScrollToStart();
            Reveal(true);
        }
        await OnFilter.InvokeAsync(_query);
    }

    private async Task Key(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        if (Locked || _busy) return;
        var key = args.Key;
        if (BeforeKey is not null && await BeforeKey(key)) return;
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var command = ResolveCommand?.Invoke(key);
        if (command is not null && Actions.FirstOrDefault(action => action.Command == command) is { } action)
        {
            await RunAction(action);
            return;
        }
        if (key.Key == ConsoleKey.Enter || command == "dialog.select.submit")
        {
            if (_action is { } id && Actions.FirstOrDefault(action => action.Command == id) is { } focused) await RunAction(focused);
            else await Select(Selected);
            return;
        }
        if (key.Key == ConsoleKey.Tab)
        {
            var actions = Actions.Where(Enabled).ToArray();
            var current = Array.FindIndex(actions, action => action.Command == _action);
            var next = current < 0 ? key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? actions.Length - 1 : 0
                : current + (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
            _action = next >= 0 && next < actions.Length ? actions[next].Command : null;
            return;
        }
        if (SectionNavigation && key.Modifiers.HasFlag(ConsoleModifiers.Alt) && key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            var section = Groups.ToList().FindIndex(group => group.Contains(Selected!));
            if (Groups.Count > 0) Move(_filtered.ToList().IndexOf(Groups[(section + (key.Key == ConsoleKey.UpArrow ? -1 : 1) + Groups.Count) % Groups.Count].First()));
            return;
        }
        if (command == "dialog.select.prev" || key.Key == ConsoleKey.UpArrow || control && key.Key == ConsoleKey.P) { Move(_selected - 1); return; }
        if (command == "dialog.select.next" || key.Key == ConsoleKey.DownArrow || control && key.Key == ConsoleKey.N) { Move(_selected + 1); return; }
        if (command == "dialog.select.page_up" || key.Key == ConsoleKey.PageUp) { Move(_selected - 10); return; }
        if (command == "dialog.select.page_down" || key.Key == ConsoleKey.PageDown) { Move(_selected + 10); return; }
        if (command == "dialog.select.home" || key.Key == ConsoleKey.Home) { Move(0, false); return; }
        if (command == "dialog.select.end" || key.Key == ConsoleKey.End) { Move(_filtered.Count - 1, false); return; }
        var before = _query;
        var boundaries = StringInfo.ParseCombiningCharacters(_query);
        var previous = boundaries.LastOrDefault(index => index < _cursor);
        var nextBoundary = boundaries.FirstOrDefault(index => index > _cursor, _query.Length);
        switch (key.Key)
        {
            case ConsoleKey.C when control:
            case ConsoleKey.U when control: _query = ""; _cursor = 0; break;
            case ConsoleKey.LeftArrow: _cursor = previous; break;
            case ConsoleKey.RightArrow: _cursor = nextBoundary; break;
            case ConsoleKey.Backspace when _cursor > 0: _query = _query.Remove(previous, _cursor - previous); _cursor = previous; break;
            case ConsoleKey.Delete when _cursor < _query.Length: _query = _query.Remove(_cursor, nextBoundary - _cursor); break;
            default:
                if (control || key.Modifiers.HasFlag(ConsoleModifiers.Alt) || char.IsControl(key.KeyChar)) return;
                if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; return; }
                var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is { } high ? new string([high, key.KeyChar]) : "" : key.KeyChar.ToString();
                _surrogate = null;
                _query = _query.Insert(_cursor, text);
                _cursor += text.Length;
                break;
        }
        if (before != _query) await ChangeFilter();
    }

    private async Task Paste(TerminalPasteEventArgs args)
    {
        args.Handled = true;
        if (_busy || Locked) return;
        var text = string.Concat(TerminalTextEditing.NormalizePaste(args.Text).Where(character => !char.IsControl(character)));
        _query = _query.Insert(_cursor, text);
        _cursor += text.Length;
        await ChangeFilter();
    }
    private void PositionCursor(TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (!Locked && args.Button == TerminalPointerButton.Left && args.TextIndex is { } index) _cursor = index;
    }
    private void Hover(DialogSelectOption<T> option, TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (!Locked && !_busy) { _userSelection = true; _selected = _filtered.ToList().IndexOf(option); _action = null; OnMove?.Invoke(option.Value); }
    }
    private void SelectPointer(DialogSelectOption<T> option, TerminalPointerEventArgs args)
    {
        if (args.Button == TerminalPointerButton.Left) Hover(option, args);
    }
    private async Task Click(DialogSelectOption<T> option, TerminalPointerEventArgs args) { args.Handled = true; await Select(option); }
    private async Task CloseClick(TerminalPointerEventArgs args) { args.Handled = true; await OnClose.InvokeAsync(); }
    private bool Enabled(DialogSelectAction<T> action) => !action.Disabled && (!action.RequiresSelection || Selected is not null);
    private async Task RunAction(DialogSelectAction<T> action, TerminalPointerEventArgs? args = null)
    {
        if (args is not null) args.Handled = true;
        if (_busy || Locked || !Enabled(action)) return;
        _busy = true;
        try { await action.Run(Selected); }
        catch (Exception exception) { _error = exception.Message; }
        finally { _busy = false; }
    }
}
