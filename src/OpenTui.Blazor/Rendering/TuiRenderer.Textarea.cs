namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Components;
using OpenTui.Blazor.Nodes;

public sealed partial class TuiRenderer
{
    private TuiNode? _textareaSelectionOwner;

    private void BindTextarea(TuiNode node, Textarea component)
    {
        var editor = component.State;
        node.Editor = editor; node.EditorRules = component.Rules;
        node.CursorColor = component.CursorColor; node.EditorCursorStyle = component.CursorStyle;
        node.SelectionForeground = component.SelectionForeground; node.SelectionBackground = component.SelectionBackground;
        if (editor.IsDisposed) return;
        editor.Bind(component, Dispatcher, (notice, args) =>
        {
            if (_eventsStopped || editor.IsDisposed || !Attached(node) || !ReferenceEquals(node.Editor, editor)) return;
            if (notice is TextareaNotice.Cursor or TextareaNotice.Content) RefreshTextareaSelection(node);
            if (editor.IsDisposed || !Attached(node) || args is TextareaChangeEventArgs { IsCurrent: false }) return;
            var handler = notice switch
            {
                TextareaNotice.Content => node.EditorContentHandlerId, TextareaNotice.Cursor => node.EditorCursorHandlerId,
                TextareaNotice.Submit => node.EditorSubmitHandlerId, TextareaNotice.Ready => node.EditorReadyHandlerId,
                TextareaNotice.Focus => node.EditorFocusHandlerId, _ => 0UL
            };
            if (handler != 0) TrackCallback(DispatchEventAsync(handler, null, args));
        });
        editor.Configure(component.Disabled, component.Visible);
    }

    private void ObserveTextareas()
    {
        foreach (var node in Textareas(RootNode).ToArray())
            if (Attached(node) && node.Editor is { IsDisposed: false } state) state.Observe();
        EnsureFocus();
        RefreshTextareaSelection();
    }
    private static IEnumerable<TuiNode> Textareas(TuiNode node)
    {
        if (node.Editor is not null) yield return node;
        foreach (var child in node.LayoutChildren)
            foreach (var editor in Textareas(child)) yield return editor;
    }

    public bool DispatchTextareaKey(TerminalKeyInput input, bool preventDefault = false)
    {
        Dispatcher.AssertAccess();
        if (_eventsStopped) return false;
        EnsureFocus();
        if (_focused is not { Editor: { } editor } node) return false;
        var args = new TerminalRoutedKeyEventArgs(input, editor.CancellationToken);
        if (preventDefault) args.PreventDefault();
        foreach (var current in PointerPath(node).ToArray())
        {
            if (!Attached(current)) continue;
            if (current.RoutedKeyHandlerId != 0) TrackCallback(DispatchEventAsync(current.RoutedKeyHandlerId, null, args));
            if (args.PropagationStopped) break;
        }
        if (!args.DefaultPrevented && Attached(node) && ReferenceEquals(node.Editor, editor) && editor.CanFocus && editor.IsMounted &&
            !editor.HandleKey(input) && input.EventType == Keymap.KeyEventType.Press)
        {
            if (_modalFocus.Count > 0 && (input.Name == "escape" || input.Name == "c" && input.Ctrl && editor.Text.Length == 0))
            {
                var modal = _modalFocus[^1].Modal;
                if (modal.CloseHandlerId != 0) TrackCallback(DispatchEventAsync(modal.CloseHandlerId, null, EventArgs.Empty));
            }
            else if (input.Name == "tab" && !input.Ctrl && !input.Meta && !input.Super && !input.Hyper)
            {
                var inputs = Inputs(FocusScope).Where(Visible).ToList();
                if (inputs.Count > 1) SetFocus(inputs[(inputs.IndexOf(node) + (input.Shift ? -1 : 1) + inputs.Count) % inputs.Count]);
            }
        }
        RefreshTextareaSelection(node);
        Dirty = true;
        // This owner has handled routing, including releases/unbound control keys.
        // Never project the same event into legacy root editing afterward.
        return true;
    }

    private bool DispatchTextareaPaste(TuiNode node, TextareaState editor, string text)
    {
        var args = new TerminalRoutedPasteEventArgs(text, editor.CancellationToken);
        foreach (var current in PointerPath(node).ToArray())
        {
            if (!Attached(current)) continue;
            if (current.RoutedPasteHandlerId != 0) TrackCallback(DispatchEventAsync(current.RoutedPasteHandlerId, null, args));
            if (args.PropagationStopped) break;
        }
        if (!args.DefaultPrevented && Attached(node) && ReferenceEquals(node.Editor, editor) && editor.CanFocus && editor.IsMounted)
            editor.InsertText(TextareaPaste.Normalize(text));
        RefreshTextareaSelection(node); Dirty = true;
        return true;
    }

    private void RefreshTextareaSelection(TuiNode? candidate = null)
    {
        candidate ??= _textareaSelectionOwner ?? _focused;
        if (candidate is { Editor: { IsMounted: true } editor } node && Attached(node) && editor.Snapshot.Selection is { } range && range.End > range.Start)
        {
            if (!ReferenceEquals(_textareaSelectionOwner, node)) ClearSelection();
            if (!Attached(node) || !editor.IsMounted) return;
            _textareaSelectionOwner = node;
            var selected = editor.SelectedText;
            var currentRange = editor.Snapshot.Selection;
            if (currentRange is null || currentRange.Value.End <= currentRange.Value.Start) return;
            var part = new TerminalSelectedText(selected, currentRange, node.X, node.Y);
            if (Selection?.Parts.Count == 1 && Selection.Parts[0] == part) return;
            Selection = new TerminalTextSelection([part]);
            SelectionChanged?.Invoke(Selection);
        }
        else if (_textareaSelectionOwner is not null) ClearSelection();
    }
}
