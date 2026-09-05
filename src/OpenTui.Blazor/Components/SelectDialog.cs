namespace OpenTui.Blazor.Components;

using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

public sealed record SelectOption<T>(T Value, string Title, string? Description = null, bool Disabled = false);

/// <summary>Keyboard-filtered modal selection over caller-supplied options.</summary>
public sealed class SelectDialog<T> : ComponentBase
{
    [Parameter] public string Title { get; set; } = "Select";
    [Parameter] public string Placeholder { get; set; } = "Search";
    [Parameter] public IReadOnlyList<SelectOption<T>> Options { get; set; } = [];
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public EventCallback<T> OnSelect { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private string _query = "";
    private int _cursor;
    private int _selected;
    private int _offset;
    private IReadOnlyList<SelectOption<T>> _filtered = [];
    private char? _surrogate;
    private bool _selecting;
    private int Limit => Math.Max(1, TerminalHeight / 2 - 6);

    protected override void OnParametersSet() => Filter();

    private void Filter()
    {
        var previous = _selected < _filtered.Count ? _filtered[_selected] : null;
        _filtered = Options.Where(option => !option.Disabled)
            .Select((option, index) => (Option: option, Index: index, Score: Score(option.Title, _query) * 2 + Score(option.Description ?? "", _query)))
            .Where(item => _query.Length == 0 || item.Score > 0)
            .OrderByDescending(item => item.Score).ThenBy(item => item.Index).Select(item => item.Option).ToArray();
        var retained = previous is null ? -1 : _filtered.ToList().FindIndex(option => EqualityComparer<T>.Default.Equals(option.Value, previous.Value));
        _selected = retained >= 0 ? retained : 0;
        Reveal();
    }

    private static int Score(string value, string query)
    {
        if (query.Length == 0) return 0;
        var position = 0;
        var score = 0;
        foreach (var c in query)
        {
            var found = value.IndexOf(c.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return 0;
            score += found == position ? 4 : 1;
            position = found + 1;
        }
        return score + (value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 8 : 0);
    }

    private void Reveal()
    {
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _filtered.Count - 1));
        _offset = Math.Clamp(_offset, Math.Max(0, _selected - Limit + 1), _selected);
        _offset = Math.Min(_offset, Math.Max(0, _filtered.Count - Limit));
    }

    private async Task Key(TerminalKeyEventArgs args)
    {
        var key = args.Key;
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (_selecting && !(control && key.Key == ConsoleKey.C)) return;
        if (key.Key == ConsoleKey.Enter)
        {
            if (_filtered.Count == 0) return;
            var value = _filtered[_selected].Value;
            _selecting = true;
            try { await OnSelect.InvokeAsync(value); }
            finally { _selecting = false; }
            return;
        }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End
            || control && key.Key is ConsoleKey.P or ConsoleKey.N)
        {
            _selected = key.Key switch
            {
                ConsoleKey.UpArrow or ConsoleKey.P => _selected - 1,
                ConsoleKey.DownArrow or ConsoleKey.N => _selected + 1,
                ConsoleKey.PageUp => _selected - 10,
                ConsoleKey.PageDown => _selected + 10,
                ConsoleKey.Home => 0,
                _ => _filtered.Count - 1
            };
            if (key.Key is not (ConsoleKey.Home or ConsoleKey.End) && _filtered.Count > 0)
            {
                if (_selected < 0) _selected = _filtered.Count - 1;
                if (_selected >= _filtered.Count) _selected = 0;
            }
            Reveal();
            return;
        }
        var before = _query;
        var boundaries = StringInfo.ParseCombiningCharacters(_query);
        var previous = boundaries.LastOrDefault(index => index < _cursor);
        var next = boundaries.FirstOrDefault(index => index > _cursor, _query.Length);
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: _cursor = previous; break;
            case ConsoleKey.RightArrow: _cursor = next; break;
            case ConsoleKey.C when control:
            case ConsoleKey.U when control:
                _query = "";
                _cursor = 0;
                break;
            case ConsoleKey.Backspace when _cursor > 0:
                _query = _query.Remove(previous, _cursor - previous);
                _cursor = previous;
                break;
            case ConsoleKey.Delete when _cursor < _query.Length:
                _query = _query.Remove(_cursor, next - _cursor);
                break;
            default:
                if (!control && !char.IsControl(key.KeyChar))
                {
                    if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; break; }
                    var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is char high ? new string([high, key.KeyChar]) : "" : key.KeyChar.ToString();
                    _surrogate = null;
                    _query = _query.Insert(_cursor, text);
                    _cursor += text.Length;
                }
                break;
        }
        if (_query != before) { _selected = 0; _filtered = []; Filter(); }
    }

    private void Paste(TerminalPasteEventArgs args)
    {
        if (_selecting) return;
        var text = string.Concat(args.Text.Where(c => !char.IsControl(c)));
        _query = _query.Insert(_cursor, text);
        _cursor += text.Length;
        _filtered = [];
        Filter();
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Modal>(0);
        builder.AddAttribute(1, "Size", Size);
        builder.AddAttribute(2, "OnClose", OnClose);
        builder.AddAttribute(3, "ChildContent", (RenderFragment)(panel =>
        {
            panel.OpenComponent<Box>(0);
            panel.AddAttribute(1, "PaddingX", 2);
            panel.AddAttribute(2, "ChildContent", (RenderFragment)(body =>
            {
                body.OpenComponent<TuiText>(0);
                body.AddAttribute(1, "Value", Title);
                body.AddAttribute(2, "Bold", true);
                body.AddAttribute(3, "Height", 1);
                body.CloseComponent();
                body.OpenComponent<Input>(10);
                body.AddAttribute(11, "Value", _query);
                body.AddAttribute(12, "Cursor", _cursor);
                body.AddAttribute(13, "Placeholder", Placeholder);
                body.AddAttribute(14, "OnKeyDown", EventCallback.Factory.Create<TerminalKeyEventArgs>(this, Key));
                body.AddAttribute(15, "OnPaste", EventCallback.Factory.Create<TerminalPasteEventArgs>(this, Paste));
                body.CloseComponent();
                body.OpenComponent<Box>(20);
                body.AddAttribute(21, "Height", 1);
                body.CloseComponent();
                if (_filtered.Count == 0)
                {
                    body.OpenComponent<TuiText>(30);
                    body.AddAttribute(31, "Value", Options.Count == 0 ? "No options available" : "No matching options");
                    body.AddAttribute(32, "Height", 1);
                    body.CloseComponent();
                }
                for (var index = _offset; index < Math.Min(_filtered.Count, _offset + Limit); index++)
                {
                    var option = _filtered[index];
                    body.OpenComponent<TuiText>(40);
                    body.SetKey(option.Value);
                    body.AddAttribute(41, "Value", (index == _selected ? "> " : "  ") + option.Title + (option.Description is null ? "" : "  " + option.Description));
                    body.AddAttribute(42, "Height", 1);
                    body.AddAttribute(43, "Bg", index == _selected ? "#344052" : "#202020");
                    body.CloseComponent();
                }
                body.OpenComponent<TuiText>(50);
                body.AddAttribute(51, "Value", _selecting ? "Selecting...  Esc close"
                    : $"{(_filtered.Count == 0 ? 0 : _selected + 1)}/{_filtered.Count}  Enter select  Esc close  PgUp/PgDn page");
                body.AddAttribute(52, "Height", 1);
                body.AddAttribute(53, "Dim", true);
                body.CloseComponent();
            }));
            panel.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
