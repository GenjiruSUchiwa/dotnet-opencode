namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;
using OpenTui.Native;

public sealed partial class TuiRenderer
{
    private sealed record SelectedView(string Source, byte WidthMethod, NativeTextSelectionRange? Range, string Text);
    private readonly Dictionary<TuiNode, SelectedView> _selectedViews = [];
    private TuiNode? _selectionOwner;
    private TuiNode? _selectionDrag;
    private TuiNode? _selectionScope;
    private TuiNode? _selectionContainer;
    private TuiNode? _selectionScroll;
    private int _anchorX;
    private int _anchorY;
    private int _focusX;
    private int _focusY;
    private TimeSpan _nextDragScroll;
    private (TuiNode Node, int X, int Y, int Count, long Time)? _lastSelectionClick;
    private NativeSelectionBehavior _selectionBehavior;
    public TerminalTextSelection? Selection { get; private set; }
    public event Action<TerminalTextSelection?>? SelectionChanged;

    public void ClearSelection() => ClearSelection(true);

    private void ClearSelection(bool resetClicks)
    {
        Dispatcher.AssertAccess();
        var textarea = _textareaSelectionOwner?.Editor;
        _textareaSelectionOwner = null;
        if (textarea is { IsMounted: true } && textarea.Snapshot.Selection is { } editorRange && editorRange.End > editorRange.Start)
            textarea.ClearSelection();
        var embedded = _embeddedSelectionOwner?.EmbeddedTerminal;
        _embeddedSelectionOwner = null;
        _embeddedSelectionDragging = false;
        if (embedded is { IsDisposed: false }) embedded.ClearSelection();
        if (resetClicks) _lastSelectionClick = null;
        foreach (var node in _selectedViews.Keys)
        {
            node.TextView?.ResetSelection();
            node.SelectionRange = null;
            node.NativeSelectionColorsSet = false;
        }
        _selectedViews.Clear();
        _selectionOwner = _selectionDrag = _selectionScope = _selectionContainer = _selectionScroll = null;
        Selection = null;
        Dirty = true;
        SelectionChanged?.Invoke(null);
    }

    private void EnsureSelection()
    {
        if (_textareaSelectionOwner is { } editor && (!Attached(editor) || editor.Editor?.IsMounted != true)) ClearSelection();
        if (_embeddedSelectionOwner is { } embedded && (!Attached(embedded) || embedded.EmbeddedTerminal?.IsDisposed != false)) ClearSelection();
        if (_selectedViews.Any(item => !Attached(item.Key) || !item.Key.Selectable || item.Key.LayoutWidth <= 0 || item.Key.LayoutHeight <= 0 ||
            item.Value.Source != item.Key.PlainText || item.Key.TextView is { } view && view.WidthMethod != item.Value.WidthMethod))
            ClearSelection();
    }

    private void UpdateSelection(TerminalPointerInput input, TuiNode? hit, TuiLayoutEngine layout)
    {
        if (input.Kind == TerminalPointerKind.Down && input.Button == TerminalPointerButton.Left)
        {
            ClearSelection(false);
            if (hit is not { Selectable: true, TagName: "text" }) { _lastSelectionClick = null; return; }
            var now = Clock.GetTimestampMilliseconds();
            var count = _lastSelectionClick is { } last && ReferenceEquals(last.Node, hit) && now - last.Time <= 500 &&
                Math.Max(Math.Abs(input.X - last.X), Math.Abs(input.Y - last.Y)) <= 1 ? Math.Min(last.Count + 1, 3) : 1;
            _lastSelectionClick = (hit, input.X, input.Y, count, now);
            _selectionBehavior = count == 2 ? NativeSelectionBehavior.Word : count == 3 ? NativeSelectionBehavior.Line : NativeSelectionBehavior.Cell;
            _selectionOwner = _selectionDrag = hit;
            _selectionScope = FocusScope;
            _selectionContainer = PointerPath(hit.Parent).FirstOrDefault() ?? FocusScope;
            _selectionScroll = PointerPath(hit).FirstOrDefault(node => node.ScrollState is not null);
            _anchorX = input.X - hit.X;
            _anchorY = input.Y - hit.Y;
        }
        if (_selectionDrag is null || input.Kind is not (TerminalPointerKind.Down or TerminalPointerKind.Move or TerminalPointerKind.Up)) return;
        _focusX = input.X;
        _focusY = input.Y;
        RefreshSelection(layout);
        if (input.Kind == TerminalPointerKind.Up) _selectionDrag = null;
    }

    /// <summary>Recompute an active drag after layout/scroll using native view coordinates.</summary>
    internal void RefreshSelection(TuiLayoutEngine layout)
    {
        EnsureSelection();
        if (_selectionDrag is null || _selectionOwner is null || _selectionScope is null) return;
        if (!ReferenceEquals(_selectionScope, FocusScope)) { _selectionDrag = null; return; }
        var hit = layout.HitTest(_selectionScope, _focusX, _focusY);
        while (_selectionContainer is { } container && !ReferenceEquals(container, _selectionScope) &&
            (hit is null || !PointerPath(hit).Contains(container)))
            _selectionContainer = PointerPath(container.Parent).FirstOrDefault() ?? _selectionScope;
        var anchorX = _selectionOwner.X + _anchorX;
        var anchorY = _selectionOwner.Y + _anchorY;
        var left = Math.Min(anchorX, _focusX);
        var right = Math.Max(anchorX, _focusX);
        var top = Math.Min(anchorY, _focusY);
        var bottom = Math.Max(anchorY, _focusY);
        var touched = SelectionNodes(_selectionContainer ?? _selectionScope, left, top, right, bottom).ToArray();
        foreach (var node in _selectedViews.Keys.Except(touched).ToArray())
        {
            node.TextView?.ResetSelection();
            node.SelectionRange = null;
            node.NativeSelectionColorsSet = false;
            _selectedViews.Remove(node);
        }
        foreach (var node in touched)
        {
            var view = layout.SelectionView(node);
            var foreground = node.SelectionForeground ?? Colors.SelectionForeground;
            var background = node.SelectionBackground ?? Colors.SelectionBackground;
            view.SetLocalSelection(anchorX - node.X, anchorY - node.Y, _focusX - node.X, _focusY - node.Y,
                background, foreground, update: _selectedViews.ContainsKey(node), behavior: _selectionBehavior);
            var range = view.GetSelection();
            var text = range is null ? "" : _selectedViews.TryGetValue(node, out var previous) && previous.Range == range
                ? previous.Text : view.GetSelectedText();
            node.SelectionRange = range;
            node.NativeSelectionForeground = foreground;
            node.NativeSelectionBackground = background;
            node.NativeSelectionColorsSet = true;
            _selectedViews[node] = new(node.PlainText, view.WidthMethod, range, text);
        }
        PublishSelection();
        Dirty = true;
    }

    // Like OpenTUI's walkSelectableRenderables: traverse actual laid-out scene
    // nodes intersecting the selection bounds, never session history or hidden
    // collapsed content. Offscreen nodes between the attached anchor and focus
    // remain eligible while the scroll container moves beneath the drag.
    private static IEnumerable<TuiNode> SelectionNodes(TuiNode node, int left, int top, int right, int bottom)
    {
        if (!node.PointerEvents || node.LayoutWidth <= 0 || node.LayoutHeight <= 0) yield break;
        if (node.Overflow != TuiOverflow.Visible &&
            (node.X > right || (long)node.X + node.LayoutWidth <= left || node.Y > bottom || (long)node.Y + node.LayoutHeight <= top)) yield break;
        if (node.Selectable && node.TagName == "text" && node.X <= right && (long)node.X + node.LayoutWidth > left &&
            node.Y <= bottom && (long)node.Y + node.LayoutHeight > top) yield return node;
        foreach (var child in node.PaintChildren)
            foreach (var text in SelectionNodes(child, left, top, right, bottom)) yield return text;
    }

    internal void ScrollSelection(TimeSpan now)
    {
        if (_selectionDrag is null || _selectionScroll is not { ScrollState: { } scroll } container || now < _nextDragScroll) return;
        var top = Math.Max(0, container.Y);
        var bottom = Math.Min(RootNode.LayoutHeight, container.Y + container.LayoutHeight);
        var direction = _focusY <= top ? -1 : _focusY >= bottom - 1 ? 1 : 0;
        if (direction == 0 || _focusX < container.X || _focusX >= container.X + container.LayoutWidth) return;
        _nextDragScroll = now + TimeSpan.FromMilliseconds(50);
        scroll.ScrollBy(direction);
        Dirty = true;
    }

    private void PublishSelection()
    {
        var parts = _selectedViews.Where(item => item.Value.Range is not null && item.Value.Text.Length > 0)
            .OrderBy(item => item.Key.Y).ThenBy(item => item.Key.X)
            .Select(item => new TerminalSelectedText(item.Value.Text, item.Value.Range!.Value, item.Key.X, item.Key.Y)).ToArray();
        if (Selection is not null && Selection.Parts.SequenceEqual(parts)) return;
        Selection = new(Array.AsReadOnly(parts));
        SelectionChanged?.Invoke(Selection);
    }

    public async Task<bool> CopySelectionAsync(ITextClipboard clipboard, CancellationToken cancellationToken = default)
    {
        Dispatcher.AssertAccess();
        EnsureSelection();
        RefreshEmbeddedSelection();
        if (Selection is not { HasText: true } selection) return false;
        await clipboard.WriteTextAsync(selection.Text, cancellationToken);
        if (ReferenceEquals(Selection, selection)) ClearSelection();
        return true;
    }

    internal void CopySelection(ITextClipboard clipboard, Action<string> reportError)
    {
        TrackCallback(Copy());
        async Task Copy()
        {
            try { await CopySelectionAsync(clipboard, _eventCancellation.Token); }
            catch (OperationCanceledException) when (_eventCancellation.IsCancellationRequested) { }
            catch (Exception exception) { reportError(exception.Message); }
        }
    }
}
