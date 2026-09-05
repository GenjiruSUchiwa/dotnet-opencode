namespace OpenTui.Blazor;

public sealed class TerminalSizeEventArgs(int width, int height) : EventArgs
{
    public int Width { get; } = width;
    public int Height { get; } = height;
}

public sealed class TerminalKeyEventArgs(ConsoleKeyInfo key, Func<string, int?, TerminalTextLayout> measure, CancellationToken cancellationToken = default) : EventArgs
{
    public ConsoleKeyInfo Key { get; } = key;
    public TerminalTextLayout Measure(string text, int? width = null) => measure(text, width);
    public CancellationToken CancellationToken { get; } = cancellationToken;
    /// <summary>Set false before the handler's first await to route this event to the root fallback.</summary>
    public bool Handled { get; set; } = true;
}

public sealed class TerminalPasteEventArgs(string text, CancellationToken cancellationToken = default) : EventArgs
{
    public string Text { get; } = text;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    /// <summary>Set false before the handler's first await to route this event to the root fallback.</summary>
    public bool Handled { get; set; } = true;
}

/// <summary>One committed text value from a press, retaining all input metadata. Not a paste or a release.</summary>
public sealed class TerminalTextInputEventArgs(TerminalKeyInput input, CancellationToken cancellationToken = default) : EventArgs
{
    public TerminalKeyInput Input { get; } = input;
    public string Text => Input.Text;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public bool Handled { get; set; } = true;
}

public readonly record struct TerminalTextPosition(int Index, int Row, int Column);
public readonly record struct TerminalTextLine(int Start, int End);

/// <summary>Native-width visual lines and UTF-16 cursor boundaries for an input value.</summary>
public sealed class TerminalTextLayout(string text, IReadOnlyList<TerminalTextLine> lines, IReadOnlyList<TerminalTextPosition> positions)
{
    public string Text { get; } = text;
    public IReadOnlyList<TerminalTextLine> Lines { get; } = lines;
    public IReadOnlyList<TerminalTextPosition> Positions { get; } = positions;
    public int ContentLineCount => Lines.Count > 1 && Lines[^1].Start == Text.Length && Lines[^2].End == Text.Length ? Lines.Count - 1 : Lines.Count;

    public TerminalTextPosition Position(int index)
    {
        for (var i = Positions.Count - 1; i >= 0; i--)
            if (Positions[i].Index <= index) return Positions[i];
        return Positions[0];
    }

    public int? MoveVertical(int index, int direction, int column)
    {
        var row = Position(index).Row + direction;
        if (row < 0 || row >= Lines.Count) return null;
        var result = Lines[row].Start;
        foreach (var position in Positions)
        {
            if (position.Row != row || position.Column > column) continue;
            result = position.Index;
        }
        return result;
    }
}
