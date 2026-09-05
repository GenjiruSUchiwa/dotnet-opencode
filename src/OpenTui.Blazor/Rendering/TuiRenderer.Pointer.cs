namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;

public sealed partial class TuiRenderer
{
    private TuiNode? _hovered;
    private TuiNode? _pressedTarget;
    private TuiNode? _pressedAction;
    private TerminalPointerInput _press;
    private bool _dragged;
    public double WheelScrollSpeed { get; set; } = 3;
    private TuiNode? _wheelTarget;
    private double _wheelRemainder;

    public void ResetPointerState(TuiLayoutEngine layout)
    {
        Dispatcher.AssertAccess();
        if (!_eventsStopped && _hovered is not null)
            foreach (var node in PointerPath(_hovered).ToArray())
                if (Attached(node)) InvokePointer(node, _press with { Kind = TerminalPointerKind.Leave }, layout);
        _pressedTarget?.Editor?.EndPointerSelection();
        _hovered = _pressedTarget = _pressedAction = null;
        _selectionDrag = null;
        _embeddedSelectionDragging = false;
        _dragged = false;
    }

    public bool DispatchPointer(TerminalPointerInput input, TuiLayoutEngine layout)
    {
        Dispatcher.AssertAccess();
        if (_eventsStopped) return false;
        EnsureFocus();
        var target = layout.HitTest(FocusScope, input.X, input.Y);
        var editorNode = input.Kind is TerminalPointerKind.Move or TerminalPointerKind.Up && _pressedTarget?.Editor is not null
            ? _pressedTarget : target?.Editor is not null ? target : null;
        if (editorNode?.Editor is { IsMounted: true } editor)
        {
            if (input.Kind == TerminalPointerKind.Down && input.Button == TerminalPointerButton.Left &&
                (!input.Modifiers.HasFlag(ConsoleModifiers.Control) || !ReferenceEquals(_textareaSelectionOwner, editorNode))) ClearSelection(false);
            if (input.Kind != TerminalPointerKind.Wheel) editor.Pointer(input, input.X - editorNode.X, input.Y - editorNode.Y);
            if (!Attached(editorNode) || editor.IsDisposed) { _pressedTarget = _pressedAction = null; return true; }
            RefreshTextareaSelection(editorNode);
        }
        var embeddedSelection = false;
        var terminalNode = input.Kind is TerminalPointerKind.Move or TerminalPointerKind.Up && _pressedTarget?.EmbeddedTerminal is not null
            ? _pressedTarget : target;
        if (terminalNode?.EmbeddedTerminal is { } terminal && input.Kind is TerminalPointerKind.Down or TerminalPointerKind.Up or TerminalPointerKind.Move or TerminalPointerKind.Wheel)
        {
            if (input.Kind == TerminalPointerKind.Down && input.Button == TerminalPointerButton.Left) SetFocus(terminalNode);
            if (terminal.Mouse(input, input.X - terminalNode.X, input.Y - terminalNode.Y, _pressedTarget?.EmbeddedTerminal is not null))
            {
                BubblePointer(terminalNode, input, layout);
                if (input.Kind == TerminalPointerKind.Down) _pressedTarget = terminalNode;
                if (input.Kind == TerminalPointerKind.Up) _pressedTarget = null;
                return true;
            }
            embeddedSelection = UpdateEmbeddedSelection(input, terminalNode);
        }
        if (!embeddedSelection && editorNode is null) UpdateSelection(input, target, layout);
        var path = PointerPath(target).ToArray();
        var previous = PointerPath(_hovered).ToArray();
        foreach (var node in previous.Except(path))
            if (Attached(node)) InvokePointer(node, input with { Kind = TerminalPointerKind.Leave }, layout);
        foreach (var node in path.Except(previous).Reverse())
            InvokePointer(node, input with { Kind = TerminalPointerKind.Enter }, layout);
        _hovered = target;

        if (_pressedTarget is not null && (!Attached(_pressedTarget) || !PointerPath(_pressedTarget).Contains(FocusScope)))
            _pressedTarget = _pressedAction = null;
        if (input.Kind == TerminalPointerKind.Down)
        {
            _press = input;
            _dragged = false;
            _pressedTarget = target;
            _pressedAction = path.FirstOrDefault(node => node.ClickHandlerId != 0);
        }
        if (input.Kind == TerminalPointerKind.Move && _pressedTarget is not null && (input.X != _press.X || input.Y != _press.Y))
            _dragged = true;
        var destination = input.Kind is TerminalPointerKind.Move or TerminalPointerKind.Up ? _pressedTarget ?? target : target;
        var handled = BubblePointer(destination, input, layout);
        if (input.Kind == TerminalPointerKind.Wheel && !handled && editorNode?.Editor is { CanFocus: true, IsMounted: true } scrolling)
        {
            scrolling.Pointer(input, input.X - editorNode.X, input.Y - editorNode.Y);
            RefreshTextareaSelection(editorNode); handled = true;
        }
        // Source dispatchMouseEvent runs handlers before its default autofocus.
        // A handled down event can prevent focus, and callbacks may remove a node.
        if (input.Kind == TerminalPointerKind.Down && input.Button == TerminalPointerButton.Left && !handled &&
            path.FirstOrDefault(node => node.TagName == "input" || node.Editor?.CanFocus == true) is { } focusTarget && Attached(focusTarget) &&
            PointerPath(focusTarget).Contains(FocusScope)) SetFocus(focusTarget);
        if (input.Kind == TerminalPointerKind.Wheel && !handled && input.DeltaY != 0)
        {
            foreach (var node in path)
            {
                if (node.ScrollState is not { } scroll) continue;
                if (!ReferenceEquals(_wheelTarget, node)) { _wheelTarget = node; _wheelRemainder = 0; }
                var amount = Math.Clamp(_wheelRemainder + input.DeltaY * WheelScrollSpeed, int.MinValue, int.MaxValue);
                var rows = (int)Math.Truncate(amount);
                _wheelRemainder = amount - rows;
                scroll.ScrollBy(rows);
                Dirty = true;
                handled = true;
                break;
            }
        }
        if (input.Kind == TerminalPointerKind.Up)
        {
            var action = _pressedAction;
            var click = !_dragged && Selection?.HasText != true && _press.Button == TerminalPointerButton.Left &&
                input.Button is TerminalPointerButton.Left or TerminalPointerButton.None &&
                action is not null && path.Contains(action) && Attached(action);
            _pressedTarget = _pressedAction = null;
            if (click) handled |= BubblePointer(action, input with { Kind = TerminalPointerKind.Click, Button = TerminalPointerButton.Left }, layout);
        }
        return handled || target is not null || _modalFocus.Count > 0;
    }

    private bool BubblePointer(TuiNode? target, TerminalPointerInput input, TuiLayoutEngine layout)
    {
        var prevented = false;
        // Snapshot the path because a synchronous callback may close a component.
        foreach (var node in PointerPath(target).ToArray())
        {
            if (!Attached(node)) continue;
            var args = InvokePointer(node, input, layout, prevented);
            prevented |= args?.DefaultPrevented == true;
            if (args?.PropagationStopped == true) break;
            if (ReferenceEquals(node, FocusScope)) break;
        }
        return prevented;
    }

    private TerminalPointerEventArgs? InvokePointer(TuiNode node, TerminalPointerInput input, TuiLayoutEngine layout, bool prevented = false)
    {
        var handler = input.Kind switch
        {
            TerminalPointerKind.Down => node.PointerDownHandlerId,
            TerminalPointerKind.Up => node.PointerUpHandlerId,
            TerminalPointerKind.Move => node.PointerMoveHandlerId,
            TerminalPointerKind.Enter => node.PointerEnterHandlerId,
            TerminalPointerKind.Leave => node.PointerLeaveHandlerId,
            TerminalPointerKind.Click => node.ClickHandlerId,
            TerminalPointerKind.Wheel => node.WheelHandlerId,
            _ => 0UL
        };
        if (handler == 0) return null;
        int? index = node.Editor?.Snapshot.CursorUtf16;
        if (node.TagName == "input")
        {
            var text = layout.MeasureInput(node.Content, Math.Max(1, node.LayoutWidth));
            var row = Math.Clamp(input.Y - node.Y + node.InputTop, 0, text.Lines.Count - 1);
            var column = Math.Max(0, input.X - node.X);
            index = text.Positions.Where(position => position.Row == row && position.Column <= column)
                .Select(position => position.Index).DefaultIfEmpty(text.Lines[row].Start).Last();
        }
        var args = new TerminalPointerEventArgs(input, input.X - node.X, input.Y - node.Y, index, _eventCancellation.Token);
        if (prevented) args.PreventDefault();
        TrackCallback(DispatchEventAsync(handler, null, args));
        Dirty = true;
        return args;
    }

    private bool Attached(TuiNode node) => PointerPath(node).Contains(RootNode);

    private static IEnumerable<TuiNode> PointerPath(TuiNode? node)
    {
        for (; node is not null; node = node.Parent)
            if (node.TagName != "#component") yield return node;
    }
}
