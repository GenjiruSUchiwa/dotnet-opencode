namespace OpenTui.Blazor.Forms;

using System.Globalization;

/// <summary>A form-local editor. The owning form consumes navigation and submit keys before calling this editor.</summary>
public sealed class TerminalFormEditor
{
    public string Text { get; private set; } = "";
    public int Cursor { get; private set; }
    public int? SelectionAnchor { get; private set; }
    private readonly TerminalEditHistory _history = new();
    private char? _surrogate;
    private int? _column;
    private TerminalEditHistory.Snapshot Snapshot => new(Text, Cursor, SelectionAnchor);
    private bool Selected => SelectionAnchor is { } anchor && anchor != Cursor;

    public void Reset(string text)
    {
        Text = text;
        Cursor = text.Length;
        SelectionAnchor = _column = null;
        _surrogate = null;
        _history.Clear();
    }

    public void Clear()
    {
        _history.Record(Snapshot);
        Text = "";
        Cursor = 0;
        SelectionAnchor = _column = null;
        _surrogate = null;
    }

    public void Paste(string text)
    {
        var normalized = TerminalTextEditing.NormalizePaste(text);
        if (normalized.Length == 0) return;
        Insert(normalized);
        _surrogate = null;
    }

    public bool Handle(ConsoleKeyInfo key, TerminalTextLayout layout)
    {
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (control && key.Key is ConsoleKey.Z or ConsoleKey.Y)
        {
            var restored = key.Key == ConsoleKey.Y || shift ? _history.Redo(Snapshot) : _history.Undo(Snapshot);
            if (restored is null) return true;
            Text = restored.Text;
            Cursor = restored.Cursor;
            SelectionAnchor = restored.SelectionAnchor;
            _column = null;
            _surrogate = null;
            return true;
        }
        if (control && key.Key == ConsoleKey.A) { SelectionAnchor = 0; Move(Text.Length, true); return true; }
        var boundaries = StringInfo.ParseCombiningCharacters(Text).Append(Text.Length).ToArray();
        var previous = boundaries.LastOrDefault(index => index < Cursor);
        var next = boundaries.FirstOrDefault(index => index > Cursor, Text.Length);
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            _column ??= layout.Position(Cursor).Column;
            if (layout.MoveVertical(Cursor, key.Key == ConsoleKey.UpArrow ? -1 : 1, _column.Value) is { } target) Move(target, shift);
            return true;
        }
        _column = null;
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                Move(control ? TerminalTextEditing.WordBoundary(Text, Cursor, -1) : !shift && Selected ? Math.Min(Cursor, SelectionAnchor!.Value) : previous, shift);
                return true;
            case ConsoleKey.RightArrow:
                Move(control ? TerminalTextEditing.WordBoundary(Text, Cursor, 1) : !shift && Selected ? Math.Max(Cursor, SelectionAnchor!.Value) : next, shift);
                return true;
            case ConsoleKey.Home: Move(control ? 0 : TerminalTextEditing.LineStart(Text, Cursor), shift); return true;
            case ConsoleKey.End: Move(control ? Text.Length : TerminalTextEditing.LineEnd(Text, Cursor), shift); return true;
            case ConsoleKey.Backspace:
            case ConsoleKey.Delete:
                var start = Selected ? Math.Min(Cursor, SelectionAnchor!.Value) : key.Key == ConsoleKey.Backspace
                    ? control ? TerminalTextEditing.WordBoundary(Text, Cursor, -1) : previous : Cursor;
                var end = Selected ? Math.Max(Cursor, SelectionAnchor!.Value) : key.Key == ConsoleKey.Delete
                    ? control ? TerminalTextEditing.WordBoundary(Text, Cursor, 1) : next : Cursor;
                if (end > start) { _history.Record(Snapshot); Text = Text.Remove(start, end - start); }
                Move(start, false);
                _surrogate = null;
                return true;
        }
        if (control || key.Modifiers.HasFlag(ConsoleModifiers.Alt) || char.IsControl(key.KeyChar)) return false;
        if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; return true; }
        var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is char high ? new string([high, key.KeyChar]) : "" : key.KeyChar.ToString();
        _surrogate = null;
        if (text.Length > 0) Insert(text);
        return true;
    }

    private void Move(int target, bool select)
    {
        if (select) SelectionAnchor ??= Cursor;
        else SelectionAnchor = null;
        Cursor = target;
    }

    private void Insert(string text)
    {
        _history.Record(Snapshot);
        if (Selected)
        {
            var start = Math.Min(Cursor, SelectionAnchor!.Value);
            Text = Text.Remove(start, Math.Abs(Cursor - SelectionAnchor.Value));
            Cursor = start;
        }
        Text = Text.Insert(Cursor, text);
        Cursor += text.Length;
        SelectionAnchor = _column = null;
    }
}
