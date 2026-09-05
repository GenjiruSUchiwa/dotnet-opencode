namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;

public sealed partial class TuiRenderer
{
    private TuiNode? _embeddedSelectionOwner;
    private int _embeddedAnchorX;
    private int _embeddedAnchorY;
    private bool _embeddedSelectionDragging;

    private bool UpdateEmbeddedSelection(TerminalPointerInput input, TuiNode node)
    {
        if (node.EmbeddedTerminal is not { } terminal) return false;
        if (input.Kind == TerminalPointerKind.Down && input.Button == TerminalPointerButton.Left)
        {
            ClearSelection();
            _embeddedSelectionOwner = node;
            _embeddedSelectionDragging = true;
            _embeddedAnchorX = Math.Clamp(input.X - node.X, 0, terminal.Columns - 1);
            _embeddedAnchorY = Math.Clamp(input.Y - node.Y, 0, terminal.Rows - 1);
            return true;
        }
        if (!_embeddedSelectionDragging || !ReferenceEquals(node, _embeddedSelectionOwner)) return true;
        var x = Math.Clamp(input.X - node.X, 0, terminal.Columns - 1);
        var y = Math.Clamp(input.Y - node.Y, 0, terminal.Rows - 1);
        if (x != _embeddedAnchorX || y != _embeddedAnchorY)
        {
            terminal.Select(_embeddedAnchorX, _embeddedAnchorY, x, y);
            RefreshEmbeddedSelection();
        }
        if (input.Kind == TerminalPointerKind.Up) _embeddedSelectionDragging = false;
        return true;
    }

    private void RefreshEmbeddedSelection()
    {
        if (_embeddedSelectionOwner is not { EmbeddedTerminal: { IsDisposed: false } terminal } node) return;
        var text = terminal.SelectedText();
        Selection = new(Array.AsReadOnly(new[] { new TerminalSelectedText(text, null, node.X, node.Y) }));
        SelectionChanged?.Invoke(Selection);
        Dirty = true;
    }
}
