namespace OpenTui.Blazor;

using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using OpenTui.Blazor.Keymap;
using OpenTui.Blazor.TextMarks;
using OpenTui.Native;
using Runtime.Time;

public enum TextareaCommand
{
    Left, Right, Up, Down, WordLeft, WordRight, LineHome, LineEnd, VisualHome, VisualEnd, BufferHome, BufferEnd,
    SelectAll, Backspace, Delete, DeleteWordLeft, DeleteWordRight, DeleteLine, DeleteToLineStart, DeleteToLineEnd,
    NewLine, Undo, Redo, Submit
}
public sealed record TextareaBinding(TextareaCommand Command, bool Select = false);
public sealed record TextareaSnapshot(string Text, int CursorUtf16, NativeLogicalCursor Cursor, NativeVisualCursor VisualCursor,
    NativeTextSelectionRange? Selection, long Revision);
public sealed record TextareaMarkPosition(int Id, int StartUtf16, int EndUtf16);
public sealed record TextareaSelectionRange(int StartUtf16, int EndUtf16);
public sealed record TextareaDocument(string Text, int CursorUtf16, int? AnchorUtf16 = null, TerminalTextMarksSnapshot? Marks = null)
{
    /// <summary>Opaque immutable application payloads keyed by virtual mark ID; never serialized or interpreted by the editor.</summary>
    public IReadOnlyDictionary<int, object?> MarkData { get; init; } = ImmutableDictionary<int, object?>.Empty;
    /// <summary>Portable positions for restoring native display marks under a new renderer width method.</summary>
    public IReadOnlyList<TextareaMarkPosition>? MarkPositions { get; init; }
    public TextareaSelectionRange? Selection { get; init; }
}
public sealed class TextareaChangeEventArgs(TextareaSnapshot before, TextareaSnapshot after, TextareaDocument document, CancellationToken cancellationToken, Func<bool> current) : EventArgs
{
    public TextareaSnapshot Before { get; } = before;
    public TextareaSnapshot After { get; } = after;
    public TextareaDocument Document { get; } = document;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public bool IsCurrent => current();
}
public sealed class TextareaSubmitEventArgs(TextareaSnapshot snapshot, TextareaDocument document, CancellationToken cancellationToken) : EventArgs
{
    public TextareaSnapshot Snapshot { get; } = snapshot;
    public TextareaDocument Document { get; } = document;
    public CancellationToken CancellationToken { get; } = cancellationToken;
}
public sealed class TerminalFocusEventArgs(bool focused) : EventArgs { public bool Focused { get; } = focused; }
internal enum TextareaNotice { Ready, Cursor, Content, Submit, Focus }

/// <summary>One dispatcher-owned native editor. The mounted Textarea owns disposal of this state.</summary>
public sealed class TextareaState : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalTextMarksHistory _history = new();
    private Dictionary<int, object?> _markData = [];
    private readonly Stack<Dictionary<int, object?>> _undoData = [];
    private readonly Stack<Dictionary<int, object?>> _redoData = [];
    private NativeEditor? _editor;
    private Dispatcher? _dispatcher;
    private object? _owner;
    private Action<TextareaNotice, EventArgs>? _notify;
    private bool _disposed, _pendingReady, _needsObserve;
    private uint? _anchor;
    private (int X, int Y)? _pointerAnchor;
    private uint? _pointerExtension;
    private (int X, int Y) _dragFocus;
    private int _autoScrollVelocity;
    private double _autoScrollAccumulator;
    private long _dragTick;
    private NativeSelectionBehavior _behavior;
    private (int X, int Y, int Count, long Time)? _lastClick;
    private TextareaSnapshot _snapshot;
    public TextareaState(string initialText = "", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(initialText);
        _clock = clock ?? TimeProvider.System;
        _snapshot = new(initialText, 0, default, default, null, 0);
        Bindings = TextareaBindings.Create();
    }
    public Task Ready => _ready.Task;
    public TextareaSnapshot Snapshot => _snapshot;
    public string Text => _snapshot.Text;
    public TerminalTextMarks Marks { get; } = new();
    public IReadOnlyDictionary<int, object?> MarkData => _markData.ToImmutableDictionary();
    public IDictionary<KeyStroke, TextareaBinding> Bindings { get; }
    public bool Disabled { get; private set; }
    public bool Visible { get; private set; } = true;
    public bool Focused { get; private set; }
    public bool IsDisposed => _disposed;
    public bool IsMounted => _editor is not null && !_disposed;
    internal bool CanFocus => !_disposed && Visible && !Disabled;
    internal bool AutoFocusAllowed { get; private set; } = true;
    internal bool FocusRequested { get; set; }
    internal bool BlurRequested { get; set; }
    internal NativeEditor Editor => _editor ?? throw new InvalidOperationException("Await TextareaState.Ready before editing.");
    internal CancellationToken CancellationToken => _lifetime.Token;
    public event Action? Changed;
    public bool CanUndo { get { Guard(); return Editor.CanUndo; } }
    public bool CanRedo { get { Guard(); return Editor.CanRedo; } }
    public string SelectedText { get { Guard(); return Editor.SelectedText; } }

    internal void Bind(object owner, Dispatcher dispatcher, Action<TextareaNotice, EventArgs> notify)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owner is not null && !ReferenceEquals(_owner, owner)) throw new InvalidOperationException("A textarea state cannot be mounted by two components.");
        _owner = owner; _dispatcher = dispatcher; _notify = notify;
    }
    internal void EnsureNative(byte widthMethod)
    {
        Guard();
        if (_editor is not null)
        {
            if (_editor.WidthMethod != widthMethod) throw new InvalidOperationException("Remount the textarea after a terminal width-method change; native undo history must not be silently replaced.");
            return;
        }
        NativeEditor? editor = null;
        try { editor = new NativeEditor(widthMethod); editor.SetText(_snapshot.Text); }
        catch (Exception error) { editor?.Dispose(); _ready.TrySetException(error); throw; }
        _editor = editor;
        _pendingReady = true;
        _ready.TrySetResult();
    }
    internal void Configure(bool disabled, bool visible)
    {
        Guard();
        if (Disabled == disabled && Visible == visible) return;
        Disabled = disabled; Visible = visible;
        if (!CanFocus) { BlurRequested = true; EndPointerSelection(); }
    }
    public void Focus() { Guard(); if (!CanFocus) return; AutoFocusAllowed = true; FocusRequested = true; BlurRequested = false; Changed?.Invoke(); }
    public void Blur() { Guard(); AutoFocusAllowed = false; BlurRequested = true; FocusRequested = false; Changed?.Invoke(); }
    internal void SetFocused(bool focused)
    {
        if (_disposed || Focused == focused) return;
        Focused = focused;
        if (!focused) EndPointerSelection();
        if (focused) AutoFocusAllowed = true;
        _notify?.Invoke(TextareaNotice.Focus, new TerminalFocusEventArgs(focused));
        Changed?.Invoke();
    }
    public void SetText(string text)
    {
        Guard(); ArgumentNullException.ThrowIfNull(text);
        Editor.ClearSelection(); Editor.SetText(text);
        Marks.Clear(); ClearHistory(); _markData.Clear(); _anchor = null;
        _behavior = NativeSelectionBehavior.Cell; EndPointerSelection();
        Synchronize(force: true);
    }
    public void Clear() => SetText("");
    public void SetDocument(TextareaDocument document)
    {
        Guard(); ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Text);
        if (document.CursorUtf16 < 0 || document.CursorUtf16 > document.Text.Length ||
            document.AnchorUtf16 is { } anchorIndex && (anchorIndex < 0 || anchorIndex > document.Text.Length))
            throw new ArgumentOutOfRangeException(nameof(document));
        if (document.Selection is { } selectionRange && selectionRange.EndUtf16 < selectionRange.StartUtf16)
            throw new ArgumentException("Document selection must be ordered.", nameof(document));
        if (document.MarkData.Keys.Any(id => document.Marks is null || !document.Marks.Marks.Any(mark => mark.Id == id)))
            throw new ArgumentException("Mark payloads require matching document marks.", nameof(document));
        var cursor = Editor.OffsetForText(document.Text, document.CursorUtf16);
        var anchor = document.AnchorUtf16 is { } position ? Editor.OffsetForText(document.Text, position) : (uint?)null;
        var selection = document.Selection is { } selectedRange
            ? new NativeTextSelectionRange(Editor.OffsetForText(document.Text, selectedRange.StartUtf16), Editor.OffsetForText(document.Text, selectedRange.EndUtf16))
            : (NativeTextSelectionRange?)null;
        var length = Editor.OffsetForText(document.Text, document.Text.Length);
        var restoredMarks = document.Marks;
        if (document.MarkPositions is { } positions)
        {
            var byId = positions.ToDictionary(item => item.Id);
            if (restoredMarks is null || byId.Count != restoredMarks.Marks.Length || restoredMarks.Marks.Any(mark => !byId.ContainsKey(mark.Id)))
                throw new ArgumentException("Portable mark positions must match all document mark IDs.", nameof(document));
            restoredMarks = new(restoredMarks.Marks.Select(mark => mark with
            {
                Start = checked((int)Editor.OffsetForText(document.Text, byId[mark.Id].StartUtf16)),
                End = checked((int)Editor.OffsetForText(document.Text, byId[mark.Id].EndUtf16))
            }).ToImmutableArray(), restoredMarks.NextId);
        }
        if (restoredMarks?.Marks.Any(mark => mark.Start < 0 || mark.End < mark.Start || (uint)mark.End > length) == true)
            throw new ArgumentException("Document mark range exceeds its native text coordinates.", nameof(document));
        Editor.ClearSelection(); Editor.SetText(document.Text);
        ClearHistory(); Marks.Clear(); _markData = document.MarkData.ToDictionary();
        if (restoredMarks is { } marks) Marks.Restore(marks);
        Editor.SetCursor(cursor);
        _anchor = anchor;
        _behavior = NativeSelectionBehavior.Cell; EndPointerSelection();
        if (selection is { } range) Editor.Select(range.Start, range.End);
        else if (_anchor is { } selected) Editor.Select(selected, cursor);
        Synchronize(force: true);
    }
    public TextareaDocument CaptureDocument()
    {
        Guard();
        var selection = Editor.Selection;
        var anchor = _anchor ?? (selection is { } range && range.End > range.Start
            ? Editor.Cursor.Offset <= range.Start ? range.End : range.Start : (uint?)null);
        return new(Text, Snapshot.CursorUtf16, anchor is { } start ? Editor.Utf16AtOffset(start) : null, Marks.Snapshot())
        {
            MarkData = _markData.Where(pair => Marks.Get(pair.Key) is not null).ToImmutableDictionary(),
            MarkPositions = Marks.All.Select(mark => new TextareaMarkPosition(mark.Id,
                Editor.Utf16AtOffset(checked((uint)mark.Start)), Editor.Utf16AtOffset(checked((uint)mark.End)))).ToImmutableArray(),
            Selection = selection is { } selected && selected.End > selected.Start
                ? new(Editor.Utf16AtOffset(selected.Start), Editor.Utf16AtOffset(selected.End)) : null
        };
    }
    public void SetCursorUtf16(int index)
    {
        Guard(); var offset = Editor.OffsetAtUtf16(index);
        Editor.ClearSelection(); _anchor = null; EndPointerSelection(); Editor.SetCursor(offset); Synchronize();
    }
    public void SelectUtf16(int anchor, int focus)
    {
        Guard(); var start = Editor.OffsetAtUtf16(anchor); var end = Editor.OffsetAtUtf16(focus);
        _anchor = start; _behavior = NativeSelectionBehavior.Cell; EndPointerSelection();
        Editor.SetCursor(end); Editor.Select(start, end); Synchronize();
    }
    public void ClearSelection() { Guard(); Editor.ClearSelection(); _anchor = null; EndPointerSelection(); Synchronize(); }
    public int CreateMark(int startUtf16, int endUtf16, bool virtualText = false, int typeId = 0, string? styleKey = null, int priority = 0, object? data = null)
    {
        Guard(); var id = Marks.Create(checked((int)Editor.OffsetAtUtf16(startUtf16)), checked((int)Editor.OffsetAtUtf16(endUtf16)), virtualText, typeId, styleKey, priority);
        if (data is not null) _markData[id] = data;
        MarksChanged(); return id;
    }
    public bool DeleteMark(int id)
    {
        Guard(); var deleted = Marks.Delete(id); var payload = _markData.Remove(id);
        if (deleted || payload) MarksChanged();
        return deleted;
    }
    public void InvalidateMarks()
    {
        Guard();
        foreach (var id in _markData.Keys.Where(id => Marks.Get(id) is null).ToArray()) _markData.Remove(id);
        MarksChanged();
    }
    private void MarksChanged() { _snapshot = _snapshot with { Revision = checked(_snapshot.Revision + 1) }; Changed?.Invoke(); }
    public void ClearHistory() { Guard(); Editor.ClearHistory(); _history.Clear(); _undoData.Clear(); _redoData.Clear(); }
    public void InsertText(string text)
    {
        Guard(); ArgumentNullException.ThrowIfNull(text);
        _pointerAnchor = null;
        if (HasSelection()) Edit(Editor.DeleteSelection, false);
        if (text.Length > 0) Edit(() => Editor.Insert(text), true);
        _anchor = null; Synchronize();
    }
    public bool Execute(TextareaCommand command, bool select = false)
    {
        Guard();
        if (Disabled || !Visible) return false;
        EndPointerSelection();
        if (command == TextareaCommand.Submit)
        {
            Synchronize(); _notify?.Invoke(TextareaNotice.Submit, new TextareaSubmitEventArgs(Snapshot, CaptureDocument(), _lifetime.Token)); return true;
        }
        if (command <= TextareaCommand.BufferEnd)
        {
            Move((NativeEditorMove)command, select); Synchronize(); return true;
        }
        if (command == TextareaCommand.SelectAll)
        {
            Editor.ClearSelection(); Editor.Move(NativeEditorMove.BufferHome); _anchor = 0;
            Editor.Move(NativeEditorMove.BufferEnd); Editor.Select(0, Editor.Cursor.Offset); Synchronize(); return true;
        }
        if (command is TextareaCommand.Undo or TextareaCommand.Redo)
        {
            var redo = command == TextareaCommand.Redo;
            if (!(redo ? Editor.CanRedo : Editor.CanUndo)) return true;
            Editor.ClearSelection(); _anchor = null;
            if (redo)
            {
                if (_history.Redo(Marks) && _redoData.TryPop(out var data)) { _undoData.Push(_markData); _markData = data; }
                Editor.Redo();
            }
            else
            {
                if (_history.Undo(Marks) && _undoData.TryPop(out var data)) { _redoData.Push(_markData); _markData = data; }
                Editor.Undo();
            }
            Synchronize(); return true;
        }
        if (HasSelection() && command is TextareaCommand.Backspace or TextareaCommand.Delete or TextareaCommand.DeleteWordLeft or TextareaCommand.DeleteWordRight)
            Edit(Editor.DeleteSelection, false);
        else if (command is TextareaCommand.Backspace or TextareaCommand.Delete &&
            Marks.AtomicDeletion(checked((int)Editor.Cursor.Offset), command == TextareaCommand.Backspace, false) is { } atomic)
            DeleteRange((uint)atomic.Start, checked((uint)(atomic.Start + atomic.Length)));
        else switch (command)
        {
            case TextareaCommand.Backspace: Edit(Editor.Backspace, false); break;
            case TextareaCommand.Delete: Edit(Editor.Delete, false); break;
            case TextareaCommand.DeleteLine: Editor.ClearSelection(); Edit(Editor.DeleteLine, false); break;
            case TextareaCommand.NewLine: Editor.ClearSelection(); Edit(Editor.NewLine, true); break;
            case TextareaCommand.DeleteWordLeft: DeleteRange(Editor.WordBoundary(false).Offset, Editor.Cursor.Offset); break;
            case TextareaCommand.DeleteWordRight: DeleteRange(Editor.Cursor.Offset, Editor.WordBoundary(true).Offset); break;
            case TextareaCommand.DeleteToLineEnd: DeleteRange(Editor.Cursor.Offset, Editor.LineEnd.Offset); break;
            case TextareaCommand.DeleteToLineStart:
                if (Editor.Cursor.Column > 0) DeleteRange(Editor.LineStart, Editor.Cursor.Offset);
                else if (Editor.Cursor.Row > 0) Edit(Editor.Backspace, false);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        _anchor = null; Synchronize(); return true;
    }
    private void DeleteRange(uint start, uint end)
    {
        if (start == end) return;
        Editor.Select(start, end); Edit(Editor.DeleteSelection, false);
    }
    private void Move(NativeEditorMove move, bool select)
    {
        _pointerAnchor = null;
        var cursor = Editor.Cursor.Offset;
        if (!select && HasSelection() && move is not (NativeEditorMove.Up or NativeEditorMove.Down))
        {
            var range = Editor.Selection!.Value;
            Editor.SetCursor(move is NativeEditorMove.Left or NativeEditorMove.WordLeft or NativeEditorMove.LineHome or NativeEditorMove.VisualHome or NativeEditorMove.BufferHome ? range.Start : range.End);
            Editor.ClearSelection(); _anchor = null; return;
        }
        if (select)
        {
            if (_behavior != NativeSelectionBehavior.Cell) { Editor.SelectionToCell(); _behavior = NativeSelectionBehavior.Cell; }
            _anchor ??= Editor.Selection is { } range && range.Start != range.End ? cursor == range.Start ? range.End : range.Start : cursor;
        }
        else { Editor.ClearSelection(); _anchor = null; }
        // A keyboard selection keeps its managed anchor, but native cursor motion
        // must follow the viewport even after a mouse selection disabled following.
        if (select) Editor.ClearSelection();
        Editor.Move(move);
        var target = Editor.Cursor.Offset;
        if (!select)
        {
            var motion = move switch { NativeEditorMove.Left => TerminalMarkMotion.Left, NativeEditorMove.Right => TerminalMarkMotion.Right,
                NativeEditorMove.Up => TerminalMarkMotion.Up, NativeEditorMove.Down => TerminalMarkMotion.Down, _ => TerminalMarkMotion.Set };
            var adjusted = Marks.MoveCursor(checked((int)cursor), checked((int)target), motion, false);
            if (adjusted != target) Editor.SetCursor(checked((uint)adjusted));
        }
        else Editor.Select(_anchor!.Value, target);
    }
    private bool HasSelection() => Editor.Selection is { } range && range.End > range.Start;
    private void Edit(Action operation, bool insertion)
    {
        var before = Editor.Text;
        var length = Editor.OffsetAtUtf16(before.Length);
        var cursor = Editor.Cursor.Offset;
        var marks = Marks.Snapshot();
        operation();
        var after = Editor.Text;
        if (before == after) return;
        _history.Save(marks);
        _undoData.Push(_markData.ToDictionary()); _redoData.Clear();
        var nextLength = Editor.OffsetAtUtf16(after.Length);
        var delta = (long)nextLength - length;
        // Inserting a combining/joining sequence can change adjacent native
        // display widths. Do not assume an insertion always increases cell count.
        if (delta > 0) Marks.AdjustInsertion(checked((int)(insertion ? cursor : Math.Min(cursor, Editor.Cursor.Offset))), checked((int)delta));
        if (delta < 0) Marks.AdjustDeletion(checked((int)Math.Min(cursor, Editor.Cursor.Offset)), checked((int)-delta));
        foreach (var id in _markData.Keys.Where(id => Marks.Get(id) is null).ToArray()) _markData.Remove(id);
    }
    internal bool HandleKey(TerminalKeyInput key)
    {
        if (!CanFocus || key.EventType != KeyEventType.Press) return false;
        if (key.TryGetKeymapEvent(out var mapped) && Bindings.TryGetValue(mapped.Stroke, out var binding)) return Execute(binding.Command, binding.Select);
        if (key.Ctrl || key.Meta || key.Super || key.Hyper) return false;
        if (key.Name == "space") { InsertText(" "); return true; }
        if (key.Text.Length == 0 || key.Text[0] < 32 || key.Text[0] == 127) return false;
        InsertText(key.Text); return true;
    }
    internal void Pointer(TerminalPointerInput input, int x, int y)
    {
        if (!CanFocus || _editor is null) return;
        var changed = false;
        if (input.Kind == TerminalPointerKind.Down && input.Button == TerminalPointerButton.Left)
        {
            var extension = input.Modifiers.HasFlag(ConsoleModifiers.Control) && HasSelection()
                ? _anchor ?? Editor.Selection!.Value.Start : (uint?)null;
            var now = _clock.GetTimestampMilliseconds();
            var count = _lastClick is { } last && now - last.Time <= 500 && Math.Max(Math.Abs(x - last.X), Math.Abs(y - last.Y)) <= 1 ? Math.Min(3, last.Count + 1) : 1;
            _lastClick = (x, y, count, now);
            _dragTick = now; _dragFocus = (x, y);
            _behavior = (NativeSelectionBehavior)(count - 1); _pointerAnchor = (x, y);
            changed = Editor.Pointer(x, y, x, y, false, _behavior); _anchor = null; _pointerExtension = extension;
            if (extension is { } fixedAnchor) { _anchor = fixedAnchor; Editor.Select(fixedAnchor, Editor.Cursor.Offset); }
        }
        else if (input.Kind is TerminalPointerKind.Move or TerminalPointerKind.Up && _pointerAnchor is { } anchor)
        {
            changed = Editor.Pointer(anchor.X, anchor.Y, x, y, true, _behavior); _dragFocus = (x, y);
            if (_pointerExtension is { } fixedAnchor) Editor.Select(fixedAnchor, Editor.Cursor.Offset);
            if (input.Kind == TerminalPointerKind.Up) EndPointerSelection();
        }
        else if (input.Kind == TerminalPointerKind.Wheel) Editor.Scroll(input.DeltaY, input.DeltaX);
        if (changed && _pointerAnchor is not null)
        {
            var margin = Math.Max(1, (int)Math.Floor(Editor.ViewportHeight * .2));
            _autoScrollVelocity = y < margin ? -16 : y >= Editor.ViewportHeight - margin ? 16 : 0;
        }
        else if (input.Kind is TerminalPointerKind.Down or TerminalPointerKind.Move or TerminalPointerKind.Up)
        {
            _autoScrollVelocity = 0; _autoScrollAccumulator = 0;
        }
        Synchronize();
    }
    internal void EndPointerSelection()
    {
        _pointerAnchor = null; _pointerExtension = null; _autoScrollVelocity = 0; _autoScrollAccumulator = 0;
    }
    internal void Observe()
    {
        if (_disposed || _editor is null) return;
        if (_pointerAnchor is { } anchor)
        {
            var now = _clock.GetTimestampMilliseconds();
            var seconds = Math.Max(0, now - _dragTick) / 1000d;
            _dragTick = now;
            if (_autoScrollVelocity != 0 && HasSelection())
            {
                _autoScrollAccumulator += _autoScrollVelocity * seconds;
                var lines = (int)Math.Min(int.MaxValue, Math.Floor(Math.Abs(_autoScrollAccumulator)));
                if (lines > 0)
                {
                    var direction = Math.Sign(_autoScrollVelocity);
                    if (Editor.Scroll(direction * lines, moveCursor: false))
                    {
                        Editor.Pointer(anchor.X, anchor.Y, _dragFocus.X, _dragFocus.Y, false, _behavior, updateCursor: false);
                        _needsObserve = true;
                    }
                    _autoScrollAccumulator -= direction * (double)lines;
                }
            }
        }
        if (!_needsObserve && !_pendingReady) return;
        _needsObserve = false;
        Synchronize();
        if (_pendingReady) { _pendingReady = false; _notify?.Invoke(TextareaNotice.Ready, EventArgs.Empty); }
    }
    internal void Resize(int width, int height) { if (Editor.Resize(width, height)) _needsObserve = true; }
    internal void SetWrapMode(NativeTextWrapMode mode) { if (Editor.SetWrapMode(mode)) _needsObserve = true; }
    private void Synchronize(bool force = false)
    {
        if (_disposed || _editor is null) return;
        var cursor = Editor.Cursor; var visual = Editor.VisualCursor; var text = Editor.Text; var selection = Editor.Selection;
        var before = _snapshot;
        var contentChanged = before.Text != text;
        var cursorChanged = before.Cursor != cursor || before.VisualCursor != visual || before.Selection != selection;
        if (!force && !contentChanged && !cursorChanged) return;
        _snapshot = new(text, Editor.Utf16AtOffset(cursor.Offset), cursor, visual, selection, checked(before.Revision + 1));
        var revision = _snapshot.Revision;
        var args = new TextareaChangeEventArgs(before, _snapshot, CaptureDocument(), _lifetime.Token, () => !_disposed && _snapshot.Revision == revision);
        Changed?.Invoke();
        // Native mutations emit cursor then content. Notify only after native
        // calls and mark adjustments return, never from a C callback frame.
        if (args.IsCurrent && cursorChanged) _notify?.Invoke(TextareaNotice.Cursor, args);
        if (args.IsCurrent && contentChanged) _notify?.Invoke(TextareaNotice.Content, args);
    }
    private void Guard() { ObjectDisposedException.ThrowIf(_disposed, this); _dispatcher?.AssertAccess(); }
    public void Dispose()
    {
        if (_disposed) return;
        Guard(); _disposed = true; Focused = false; _notify = null;
        try { _lifetime.Cancel(); }
        finally
        {
            try { _ready.TrySetCanceled(_lifetime.Token); _editor?.Dispose(); }
            finally
            {
                _editor = null; _lifetime.Dispose(); _history.Clear(); _undoData.Clear(); _redoData.Clear(); _markData.Clear(); Marks.Clear();
                _owner = null; _dispatcher = null; Changed = null;
            }
        }
    }
}
