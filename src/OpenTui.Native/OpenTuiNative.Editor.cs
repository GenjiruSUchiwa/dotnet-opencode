namespace OpenTui.Native;

using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeLogicalCursor(uint Row, uint Column, uint Offset);
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NativeVisualCursor(uint Row, uint Column, uint LogicalRow, uint LogicalColumn, uint Offset);
public enum NativeEditorMove { Left, Right, Up, Down, WordLeft, WordRight, LineHome, LineEnd, VisualHome, VisualEnd, BufferHome, BufferEnd }

public static unsafe partial class OpenTuiNative
{
    [LibraryImport(LibName, EntryPoint = "textBufferGetLength")]
    internal static partial uint TextDisplayWidth(uint buffer);
    [LibraryImport(LibName, EntryPoint = "textBufferGetLineCount")]
    internal static partial uint TextLineCount(uint buffer);
    [LibraryImport(LibName, EntryPoint = "createEditBuffer")]
    internal static partial uint CreateEditorBuffer(byte widthMethod, uint eventSink);
    [LibraryImport(LibName, EntryPoint = "destroyEditBuffer")]
    internal static partial void DestroyEditorBuffer(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferGetTextBuffer")]
    internal static partial uint EditorTextBuffer(uint buffer);
    [LibraryImport(LibName, EntryPoint = "createEditorView")]
    internal static partial uint CreateEditorView(uint buffer, uint width, uint height);
    [LibraryImport(LibName, EntryPoint = "destroyEditorView")]
    internal static partial void DestroyEditorView(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewGetTextBufferView")]
    internal static partial uint EditorTextView(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewSetWrapMode")]
    internal static partial void EditorWrap(uint view, byte mode);
    [LibraryImport(LibName, EntryPoint = "editorViewSetScrollMargin")]
    internal static partial void EditorScrollMargin(uint view, float margin);
    [LibraryImport(LibName, EntryPoint = "editorViewSetViewportSize")]
    internal static partial void EditorResize(uint view, uint width, uint height);
    [LibraryImport(LibName, EntryPoint = "editorViewGetTotalVirtualLineCount")]
    internal static partial uint EditorVirtualLineCount(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewSetViewport")]
    internal static partial void EditorViewport(uint view, uint x, uint y, uint width, uint height, [MarshalAs(UnmanagedType.I1)] bool moveCursor);
    [LibraryImport(LibName, EntryPoint = "editorViewGetViewport")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool EditorViewport(uint view, out uint x, out uint y, out uint width, out uint height);
    [LibraryImport(LibName, EntryPoint = "editBufferGetCursorPosition")]
    internal static partial void EditorCursor(uint buffer, out NativeLogicalCursor cursor);
    [LibraryImport(LibName, EntryPoint = "editorViewGetVisualCursor")]
    internal static partial void EditorVisualCursor(uint view, out NativeVisualCursor cursor);
    [LibraryImport(LibName, EntryPoint = "editorViewSetCursorByOffset")]
    internal static partial void EditorCursorOffset(uint view, uint offset);
    [LibraryImport(LibName, EntryPoint = "editBufferSetCursor")]
    internal static partial void EditorCursorPosition(uint buffer, uint row, uint column);
    [LibraryImport(LibName, EntryPoint = "editBufferGetLineStartOffset")]
    internal static partial uint EditorLineStart(uint buffer, uint row);
    [LibraryImport(LibName, EntryPoint = "editBufferGotoLine")]
    internal static partial void EditorGotoLine(uint buffer, uint row);
    [LibraryImport(LibName, EntryPoint = "editBufferGetEOL")]
    internal static partial void EditorLineEnd(uint buffer, out NativeLogicalCursor cursor);
    [LibraryImport(LibName, EntryPoint = "editorViewGetVisualSOL")]
    internal static partial void EditorVisualStart(uint view, out NativeVisualCursor cursor);
    [LibraryImport(LibName, EntryPoint = "editorViewGotoVisualLineEnd")]
    internal static partial void EditorVisualEnd(uint view);
    [LibraryImport(LibName, EntryPoint = "editBufferMoveCursorLeft")]
    internal static partial void EditorLeft(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferMoveCursorRight")]
    internal static partial void EditorRight(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editorViewMoveUpVisual")]
    internal static partial void EditorUp(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewMoveDownVisual")]
    internal static partial void EditorDown(uint view);
    [LibraryImport(LibName, EntryPoint = "editBufferGetNextWordBoundary")]
    internal static partial void EditorNextWord(uint buffer, out NativeLogicalCursor cursor);
    [LibraryImport(LibName, EntryPoint = "editBufferGetPrevWordBoundary")]
    internal static partial void EditorPreviousWord(uint buffer, out NativeLogicalCursor cursor);
    [LibraryImport(LibName, EntryPoint = "editBufferDeleteChar")]
    internal static partial void EditorDelete(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferDeleteCharBackward")]
    internal static partial void EditorBackspace(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferDeleteLine")]
    internal static partial void EditorDeleteLine(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferNewLine")]
    internal static partial void EditorNewLine(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editorViewDeleteSelectedText")]
    internal static partial void EditorDeleteSelection(uint view);
    [LibraryImport(LibName, EntryPoint = "editBufferSetText")]
    private static partial void EditorSetTextCore(uint buffer, byte* bytes, uint length);
    [LibraryImport(LibName, EntryPoint = "editBufferReplaceText")]
    private static partial void EditorReplaceTextCore(uint buffer, byte* bytes, uint length);
    [LibraryImport(LibName, EntryPoint = "editBufferInsertText")]
    private static partial void EditorInsertTextCore(uint buffer, byte* bytes, uint length);
    [LibraryImport(LibName, EntryPoint = "editBufferGetTextRange")]
    private static partial uint EditorReadRangeCore(uint buffer, uint start, uint end, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "editBufferGetText")]
    private static partial uint EditorReadTextCore(uint buffer, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "editBufferUndo")]
    private static partial uint EditorUndoCore(uint buffer, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "editBufferRedo")]
    private static partial uint EditorRedoCore(uint buffer, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "editBufferCanUndo")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool EditorCanUndo(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferCanRedo")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool EditorCanRedo(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editBufferClearHistory")]
    internal static partial void EditorClearHistory(uint buffer);
    [LibraryImport(LibName, EntryPoint = "editorViewGetSelection")]
    internal static partial ulong EditorSelection(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewResetSelection")]
    internal static partial void EditorResetSelection(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewConvertSelectionToCell")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool EditorSelectionToCell(uint view);
    [LibraryImport(LibName, EntryPoint = "editorViewSetSelection")]
    private static partial void EditorSelectionCore(uint view, uint start, uint end, NativeRgba* background, NativeRgba* foreground);
    [LibraryImport(LibName, EntryPoint = "editorViewSetSelectionColors")]
    private static partial void EditorSelectionColorsCore(uint view, NativeRgba* background, NativeRgba* foreground);
    [LibraryImport(LibName, EntryPoint = "editorViewSetLocalSelection")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool EditorLocalSelectionCore(uint view, int ax, int ay, int fx, int fy, NativeRgba* background, NativeRgba* foreground, byte flags);
    [LibraryImport(LibName, EntryPoint = "editorViewUpdateLocalSelection")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool EditorUpdateLocalSelectionCore(uint view, int ax, int ay, int fx, int fy, NativeRgba* background, NativeRgba* foreground, byte flags);
    [LibraryImport(LibName, EntryPoint = "bufferDrawEditorView")]
    internal static partial void DrawEditor(uint target, uint view, int x, int y);
    [LibraryImport(LibName, EntryPoint = "editorViewSetPlaceholderStyledText")]
    private static partial void EditorPlaceholderCore(uint view, StyledChunkData* chunks, uint count);

    internal static void EditorPlaceholder(uint view, string? text, NativeRgba color)
    {
        if (text is null) { EditorPlaceholderCore(view, null, 0); return; }
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = bytes)
        {
            var chunk = new StyledChunkData { Text = data, TextLength = (nuint)bytes.Length, Foreground = &color };
            EditorPlaceholderCore(view, &chunk, 1);
        }
    }

    internal static void EditorWrite(uint buffer, string text, int operation)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = bytes)
        {
            if (operation == 0) EditorSetTextCore(buffer, data, (uint)bytes.Length);
            else if (operation == 1) EditorReplaceTextCore(buffer, data, (uint)bytes.Length);
            else EditorInsertTextCore(buffer, data, (uint)bytes.Length);
        }
    }
    internal static string EditorRead(uint buffer, uint textBuffer, uint? end = null, uint start = 0)
    {
        var capacity = TextByteSize(textBuffer);
        if (capacity == 0) return "";
        var bytes = new byte[checked((int)capacity)];
        fixed (byte* output = bytes)
        {
            var count = end is { } finish ? EditorReadRangeCore(buffer, start, finish, output, capacity) : EditorReadTextCore(buffer, output, capacity);
            if (count > capacity) throw new InvalidOperationException("Native editor returned an invalid text length.");
            return Encoding.UTF8.GetString(bytes.AsSpan(0, (int)count));
        }
    }
    internal static void EditorHistory(uint buffer, bool redo)
    {
        // A zero output capacity skips the undo/redo operation in the native ABI.
        Span<byte> metadata = stackalloc byte[256];
        fixed (byte* output = metadata)
        {
            if (redo) EditorRedoCore(buffer, output, 256);
            else EditorUndoCore(buffer, output, 256);
        }
    }
    internal static void EditorSetSelection(uint view, uint start, uint end) => EditorSelectionCore(view, start, end, null, null);
    internal static void EditorColors(uint view, NativeRgba? background, NativeRgba? foreground)
    {
        var bg = background.GetValueOrDefault(); var fg = foreground.GetValueOrDefault();
        EditorSelectionColorsCore(view, background.HasValue ? &bg : null, foreground.HasValue ? &fg : null);
    }
    internal static bool EditorPointer(uint view, int ax, int ay, int fx, int fy, bool update, NativeSelectionBehavior behavior, bool updateCursor, bool followCursor)
    {
        var flags = checked((byte)((updateCursor ? 1 : 0) | (followCursor ? 2 : 0) | ((byte)behavior << 2)));
        return update ? EditorUpdateLocalSelectionCore(view, ax, ay, fx, fy, null, null, flags)
            : EditorLocalSelectionCore(view, ax, ay, fx, fy, null, null, flags);
    }
}
