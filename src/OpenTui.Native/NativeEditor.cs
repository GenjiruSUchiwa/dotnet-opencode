namespace OpenTui.Native;

/// <summary>Owns one EditBuffer and EditorView. All offsets are native display offsets including LF.</summary>
public sealed class NativeEditor : IDisposable
{
    private uint _buffer, _view, _textBuffer;
    private NativeTextView? _conversion;
    private NativeSyntaxStyle? _style;
    private NativeSyntaxStyle.Lease? _styleLease;
    private NativeSyntaxRule[] _rules = [];
    private string? _placeholder;
    private NativeRgba? _placeholderColor;
    private int _width = 1, _height = 1;
    private NativeTextWrapMode _wrapMode = NativeTextWrapMode.Word;
    private bool _disposed;
    private int _references = 1;
    public byte WidthMethod { get; }
    internal uint ViewHandle { get { Guard(); return _view; } }

    public NativeEditor(byte widthMethod)
    {
        if (widthMethod is not (0 or 1 or 3)) throw new ArgumentOutOfRangeException(nameof(widthMethod));
        WidthMethod = widthMethod;
        _buffer = OpenTuiNative.CreateEditorBuffer(widthMethod, 0);
        if (_buffer == 0) throw new InvalidOperationException("Native edit buffer creation failed.");
        try
        {
            _textBuffer = OpenTuiNative.EditorTextBuffer(_buffer);
            _view = OpenTuiNative.CreateEditorView(_buffer, 1, 1);
            if (_view == 0 || _textBuffer == 0) throw new InvalidOperationException("Native editor view creation failed.");
            OpenTuiNative.EditorWrap(_view, (byte)NativeTextWrapMode.Word);
            OpenTuiNative.EditorScrollMargin(_view, .2f);
        }
        catch { Dispose(); throw; }
    }
    public string Text { get { Guard(); return OpenTuiNative.EditorRead(_buffer, _textBuffer); } }
    public NativeLogicalCursor Cursor { get { Guard(); OpenTuiNative.EditorCursor(_buffer, out var cursor); return cursor; } }
    public NativeVisualCursor VisualCursor { get { Guard(); OpenTuiNative.EditorVisualCursor(_view, out var cursor); return cursor; } }
    public bool CanUndo { get { Guard(); return OpenTuiNative.EditorCanUndo(_buffer); } }
    public bool CanRedo { get { Guard(); return OpenTuiNative.EditorCanRedo(_buffer); } }
    public NativeTextSelectionRange? Selection
    {
        get { Guard(); var packed = OpenTuiNative.EditorSelection(_view); return packed == ulong.MaxValue ? null : new((uint)(packed >> 32), (uint)packed); }
    }
    public string TextRange(uint start, uint end) { Guard(); return OpenTuiNative.EditorRead(_buffer, _textBuffer, end, start); }
    public string SelectedText => Selection is { } range ? TextRange(range.Start, range.End) : "";
    public int Utf16AtOffset(uint offset) => TextRange(0, offset).Length;
    public uint OffsetAtUtf16(int index)
    {
        return OffsetForText(Text, index);
    }
    public uint OffsetForText(string text, int index)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, text.Length);
        var prefix = text.AsSpan(0, index);
        _conversion ??= new NativeTextView(WidthMethod);
        _conversion.SetWrapMode(NativeTextWrapMode.None);
        _conversion.SetText(prefix);
        var offset = _conversion.DisplayLength;
        _conversion.SetText(text);
        _conversion.SetSelection(new(0, offset));
        // The scratch native view measures/validates coordinates only; it has no
        // edit history or cursor and cannot disturb the authoritative EditorView.
        if (!_conversion.GetSelectedText().AsSpan().SequenceEqual(prefix))
            throw new ArgumentException("UTF-16 position is not a native text boundary.", nameof(index));
        return offset;
    }
    public void SetText(string text, bool preserveHistory = false) { Guard(); OpenTuiNative.EditorWrite(_buffer, text, preserveHistory ? 1 : 0); }
    public void Insert(string text) { Guard(); OpenTuiNative.EditorWrite(_buffer, text, 2); }
    public void SetCursor(uint offset) { Guard(); OpenTuiNative.EditorCursorOffset(_view, offset); }
    public void Select(uint anchor, uint focus) { Guard(); OpenTuiNative.EditorSetSelection(_view, Math.Min(anchor, focus), Math.Max(anchor, focus)); }
    public void SelectionToCell() { Guard(); OpenTuiNative.EditorSelectionToCell(_view); }
    public void ClearSelection() { Guard(); OpenTuiNative.EditorResetSelection(_view); }
    public void DeleteSelection() { Guard(); OpenTuiNative.EditorDeleteSelection(_view); }
    public void Backspace() { Guard(); OpenTuiNative.EditorBackspace(_buffer); }
    public void Delete() { Guard(); OpenTuiNative.EditorDelete(_buffer); }
    public void NewLine() { Guard(); OpenTuiNative.EditorNewLine(_buffer); }
    public void DeleteLine() { Guard(); OpenTuiNative.EditorDeleteLine(_buffer); }
    public void Undo() { Guard(); OpenTuiNative.EditorHistory(_buffer, false); }
    public void Redo() { Guard(); OpenTuiNative.EditorHistory(_buffer, true); }
    public void ClearHistory() { Guard(); OpenTuiNative.EditorClearHistory(_buffer); }
    public NativeLogicalCursor WordBoundary(bool forward)
    {
        Guard();
        NativeLogicalCursor result;
        if (forward) OpenTuiNative.EditorNextWord(_buffer, out result);
        else OpenTuiNative.EditorPreviousWord(_buffer, out result);
        return result;
    }
    public NativeLogicalCursor LineEnd { get { Guard(); OpenTuiNative.EditorLineEnd(_buffer, out var cursor); return cursor; } }
    public uint LineStart => OpenTuiNative.EditorLineStart(_buffer, Cursor.Row);
    public void Move(NativeEditorMove move)
    {
        Guard();
        switch (move)
        {
            case NativeEditorMove.Left: OpenTuiNative.EditorLeft(_buffer); break;
            case NativeEditorMove.Right: OpenTuiNative.EditorRight(_buffer); break;
            case NativeEditorMove.Up: OpenTuiNative.EditorUp(_view); break;
            case NativeEditorMove.Down: OpenTuiNative.EditorDown(_view); break;
            case NativeEditorMove.WordLeft: SetCursor(WordBoundary(false).Offset); break;
            case NativeEditorMove.WordRight: SetCursor(WordBoundary(true).Offset); break;
            case NativeEditorMove.BufferHome: OpenTuiNative.EditorCursorPosition(_buffer, 0, 0); break;
            case NativeEditorMove.BufferEnd: OpenTuiNative.EditorGotoLine(_buffer, 999999); break;
            case NativeEditorMove.VisualHome:
                OpenTuiNative.EditorVisualStart(_view, out var start);
                OpenTuiNative.EditorCursorPosition(_buffer, start.LogicalRow, start.LogicalColumn); break;
            case NativeEditorMove.VisualEnd: OpenTuiNative.EditorVisualEnd(_view); break;
            case NativeEditorMove.LineHome:
                var home = Cursor;
                OpenTuiNative.EditorCursorPosition(_buffer, home.Column == 0 && home.Row > 0 ? home.Row - 1 : home.Row, 0);
                if (home.Column == 0 && home.Row > 0) SetCursor(LineEnd.Offset);
                break;
            case NativeEditorMove.LineEnd:
                var current = Cursor; var end = LineEnd;
                if (current.Column == end.Column && current.Row < Text.Count(character => character == '\n'))
                    OpenTuiNative.EditorCursorPosition(_buffer, current.Row + 1, 0);
                else SetCursor(end.Offset);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(move));
        }
    }
    public bool Resize(int width, int height)
    {
        Guard(); width = Math.Max(1, width); height = Math.Max(1, height);
        if (_width == width && _height == height) return false;
        OpenTuiNative.EditorResize(_view, checked((uint)width), checked((uint)height)); _width = width; _height = height;
        return true;
    }
    public int ViewportHeight => _height;
    public bool Scroll(int rows, int columns = 0, bool moveCursor = true)
    {
        Guard();
        if (OpenTuiNative.EditorViewport(_view, out var x, out var y, out var width, out var height))
        {
            var top = (uint)Math.Clamp((long)y + rows, 0, Math.Max(0L, (long)OpenTuiNative.EditorVirtualLineCount(_view) - height));
            var left = _wrapMode == NativeTextWrapMode.None ? (uint)Math.Clamp((long)x + columns, 0, uint.MaxValue) : x;
            var changed = top != y || left != x;
            if (changed || moveCursor) OpenTuiNative.EditorViewport(_view, left, top, width, height, moveCursor);
            return changed;
        }
        return false;
    }
    public bool SetWrapMode(NativeTextWrapMode mode)
    {
        Guard();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_wrapMode == mode) return false;
        OpenTuiNative.EditorWrap(_view, (byte)mode); _wrapMode = mode; return true;
    }
    public void SetPlaceholder(string? text, NativeRgba color)
    {
        Guard();
        if (_placeholder == text && Nullable.Equals(_placeholderColor, color)) return;
        OpenTuiNative.EditorPlaceholder(_view, text, color); _placeholder = text; _placeholderColor = color;
    }
    public bool Pointer(int ax, int ay, int fx, int fy, bool update, NativeSelectionBehavior behavior,
        bool updateCursor = true, bool followCursor = false)
    { Guard(); return OpenTuiNative.EditorPointer(_view, ax, ay, fx, fy, update, behavior, updateCursor, followCursor); }
    public void SetStyle(NativeRgba foreground, NativeRgba background, NativeRgba? selectionBackground, NativeRgba? selectionForeground)
    {
        Guard(); OpenTuiNative.SetTextBufferStyle(_textBuffer, foreground, background, 0);
        OpenTuiNative.EditorColors(_view, selectionBackground, selectionForeground);
    }
    public void SetHighlights(IReadOnlyList<NativeSyntaxRule> rules, IEnumerable<(uint Start, uint End, string Scope, byte Priority)> marks)
    {
        Guard();
        if (!_rules.SequenceEqual(rules))
        {
            var style = new NativeSyntaxStyle();
            NativeSyntaxStyle.Lease? lease = null;
            try
            {
                foreach (var rule in rules) style.Register(rule);
                lease = style.Retain();
                if (!OpenTuiNative.TextBufferSetSyntaxStyle(_textBuffer, lease.Handle)) throw new InvalidOperationException("Could not attach editor styles.");
            }
            catch { lease?.Dispose(); style.Dispose(); throw; }
            _styleLease?.Dispose(); _style?.Dispose();
            _style = style; _styleLease = lease; _rules = rules.ToArray();
        }
        OpenTuiNative.ClearTextHighlights(_textBuffer);
        if (_style is null) return;
        foreach (var mark in marks)
            if (_style.Resolve(mark.Scope) is { } id)
            {
                var highlight = new NativeTextHighlight(mark.Start, mark.End, id, mark.Priority);
                OpenTuiNative.AddTextDisplayHighlight(_textBuffer, in highlight);
            }
    }
    public void Draw(uint buffer, int x, int y) { Guard(); OpenTuiNative.DrawEditor(buffer, _view, x, y); }
    internal IDisposable BorrowForMeasure() { Guard(); _references++; return new MeasureLease(this); }
    private sealed class MeasureLease(NativeEditor owner) : IDisposable
    {
        private NativeEditor? _owner = owner;
        public void Dispose() { var value = _owner; _owner = null; value?.Release(); }
    }
    private void Guard() => ObjectDisposedException.ThrowIf(_disposed || _buffer == 0, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Release();
    }
    private void Release()
    {
        if (--_references != 0) return;
        var view = _view; var buffer = _buffer; _view = _buffer = _textBuffer = 0;
        try { _conversion?.Dispose(); if (view != 0) OpenTuiNative.DestroyEditorView(view); }
        finally
        {
            try { if (buffer != 0) OpenTuiNative.DestroyEditorBuffer(buffer); }
            finally { _styleLease?.Dispose(); _styleLease = null; _style?.Dispose(); _style = null; }
        }
    }
}
