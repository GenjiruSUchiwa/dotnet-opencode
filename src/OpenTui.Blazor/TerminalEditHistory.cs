namespace OpenTui.Blazor;

/// <summary>Bounded, in-memory editing history owned by one editor document.</summary>
public sealed class TerminalEditHistory
{
    public sealed record Snapshot(string Text, int Cursor, int? SelectionAnchor);
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(Snapshot before)
    {
        _undo.Add(before);
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public Snapshot? Undo(Snapshot current) => Move(_undo, _redo, current);
    public Snapshot? Redo(Snapshot current) => Move(_redo, _undo, current);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private static Snapshot? Move(List<Snapshot> source, List<Snapshot> destination, Snapshot current)
    {
        if (source.Count == 0) return null;
        var value = source[^1];
        source.RemoveAt(source.Count - 1);
        destination.Add(current);
        return value;
    }
}
