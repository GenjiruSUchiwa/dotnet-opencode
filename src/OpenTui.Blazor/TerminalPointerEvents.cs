namespace OpenTui.Blazor;

public enum TerminalPointerKind { Down, Up, Move, Wheel, Enter, Leave, Click }
public enum TerminalPointerButton { None = -1, Left = 0, Middle = 1, Right = 2, Back = 8, Forward = 9 }

/// <summary>Terminal cell coordinates are zero-based. Wheel deltas are signed steps.</summary>
public readonly record struct TerminalPointerInput(TerminalPointerKind Kind, int X, int Y,
    TerminalPointerButton Button = TerminalPointerButton.None, ConsoleModifiers Modifiers = ConsoleModifiers.None,
    int DeltaX = 0, int DeltaY = 0);

public sealed class TerminalPointerEventArgs(TerminalPointerInput input, int x, int y,
    int? textIndex, CancellationToken cancellationToken = default) : EventArgs
{
    public TerminalPointerKind Kind => input.Kind;
    public TerminalPointerButton Button => input.Button;
    public ConsoleModifiers Modifiers => input.Modifiers;
    public int ScreenX => input.X;
    public int ScreenY => input.Y;
    public int X { get; } = x;
    public int Y { get; } = y;
    public int DeltaX => input.DeltaX;
    public int DeltaY => input.DeltaY;
    /// <summary>For inputs, a UTF-16 grapheme boundary at the pointer; null for other nodes.</summary>
    public int? TextIndex { get; } = textIndex;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    /// <summary>Set before the first await to stop bubbling and wheel defaults.</summary>
    public bool Handled { get; set; }
}
