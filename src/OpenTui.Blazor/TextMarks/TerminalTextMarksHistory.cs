namespace OpenTui.Blazor.TextMarks;

/// <summary>Source extmarks-history snapshot discipline, with caller-owned text history.</summary>
public sealed class TerminalTextMarksHistory
{
    private readonly Stack<TerminalTextMarksSnapshot> _undo = [];
    private readonly Stack<TerminalTextMarksSnapshot> _redo = [];
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Save(TerminalTextMarks marks) { _undo.Push(marks.Snapshot()); _redo.Clear(); }
    public void Save(TerminalTextMarksSnapshot snapshot) { _undo.Push(snapshot); _redo.Clear(); }
    public bool Undo(TerminalTextMarks marks) => Restore(_undo, _redo, marks);
    public bool Redo(TerminalTextMarks marks) => Restore(_redo, _undo, marks);
    public void Clear() { _undo.Clear(); _redo.Clear(); }
    private static bool Restore(Stack<TerminalTextMarksSnapshot> source, Stack<TerminalTextMarksSnapshot> destination, TerminalTextMarks marks)
    {
        if (!source.TryPop(out var snapshot)) return false;
        destination.Push(marks.Snapshot());
        marks.Restore(snapshot);
        return true;
    }
}
