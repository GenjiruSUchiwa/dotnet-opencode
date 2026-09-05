namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;

public sealed partial class TuiRenderer
{
    /// <summary>Terminal keys precede app bindings, except keys deferred by the caller's leader policy.</summary>
    internal bool DispatchEmbeddedKey(ConsoleKeyInfo key)
    {
        EnsureFocus();
        if (_eventsStopped || _focused?.EmbeddedTerminal is not { } terminal || terminal.DeferKey?.Invoke(key) == true) return false;
        terminal.SendKey(key);
        Dirty = true;
        return true;
    }

    internal bool DispatchEmbeddedKey(TerminalKeyInput key, Action<string> reportError)
    {
        EnsureFocus();
        if (_eventsStopped || _focused?.EmbeddedTerminal is not { } terminal) return false;
        if (terminal.DeferRichKey?.Invoke(key) == true) return false;
        var projection = key.ProjectConsoleKeys();
        if (terminal.DeferRichKey is null && projection.IsExact && projection.Keys.Count == 1 && terminal.DeferKey?.Invoke(projection.Keys[0]) == true) return false;
        try { terminal.SendKey(key); }
        catch (NotSupportedException exception) { reportError(exception.Message); }
        Dirty = true;
        return true;
    }

    internal bool DispatchTextInput(TerminalKeyInput key)
    {
        if (_eventsStopped || key.EventType == Keymap.KeyEventType.Release || key.Text.Length == 0 || key.Ctrl || key.Meta || key.Super || key.Hyper) return false;
        EnsureFocus();
        if (_focused is not { TagName: "input", TextInputHandlerId: > 0 } node) return false;
        var args = new TerminalTextInputEventArgs(key, _eventCancellation.Token);
        TrackCallback(DispatchEventAsync(node.TextInputHandlerId, null, args));
        return args.Handled;
    }

    internal bool DispatchLayoutEvents()
    {
        var changed = false;
        foreach (var node in LayoutEventNodes(RootNode).ToArray())
        {
            if (!Attached(node)) continue;
            if (node.ReportedWidth == node.LayoutWidth && node.ReportedHeight == node.LayoutHeight) continue;
            node.ReportedWidth = node.LayoutWidth;
            node.ReportedHeight = node.LayoutHeight;
            changed = true;
            TrackCallback(DispatchEventAsync(node.SizeHandlerId, null, new TerminalSizeEventArgs(node.LayoutWidth, node.LayoutHeight)));
        }
        return changed;
    }
    private static IEnumerable<TuiNode> LayoutEventNodes(TuiNode node)
    {
        if (node.SizeHandlerId != 0) yield return node;
        foreach (var child in node.LayoutChildren)
            foreach (var target in LayoutEventNodes(child)) yield return target;
    }
}
