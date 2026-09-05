namespace OpenTui.Native;

using System.Runtime.InteropServices;
using System.Text;

public static unsafe partial class OpenTuiNative
{
    // Verified against @opentui/core 0.5.9 zig.ts and native text-buffer-view.zig
    // at df2fc1594bb7a1274fc490155305e3d9f61f1b01. Handles remain u32;
    // optional colors point to the existing packed four-u16 NativeRgba.
    [LibraryImport(LibName, EntryPoint = "textBufferViewSetLocalSelection")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SetLocalSelectionCore(uint view, int anchorX, int anchorY, int focusX, int focusY,
        NativeRgba* background, NativeRgba* foreground, byte behavior);

    [LibraryImport(LibName, EntryPoint = "textBufferViewUpdateLocalSelection")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool UpdateLocalSelectionCore(uint view, int anchorX, int anchorY, int focusX, int focusY,
        NativeRgba* background, NativeRgba* foreground, byte behavior);

    [LibraryImport(LibName, EntryPoint = "textBufferViewSetSelection")]
    private static partial void SetSelectionCore(uint view, uint start, uint end, NativeRgba* background, NativeRgba* foreground);

    [LibraryImport(LibName, EntryPoint = "textBufferViewResetSelection")]
    internal static partial void ResetTextSelection(uint view);

    [LibraryImport(LibName, EntryPoint = "textBufferViewGetSelectionInfo")]
    internal static partial ulong GetTextSelectionInfo(uint view);

    [LibraryImport(LibName, EntryPoint = "textBufferGetByteSize")]
    private static partial uint TextByteSize(uint buffer);

    [LibraryImport(LibName, EntryPoint = "textBufferViewGetSelectedText")]
    private static partial uint GetSelectedTextCore(uint view, byte* output, uint capacity);

    internal static bool SetLocalTextSelection(uint view, int anchorX, int anchorY, int focusX, int focusY,
        NativeRgba? background, NativeRgba? foreground, bool update, byte behavior)
    {
        var bg = background.GetValueOrDefault();
        var fg = foreground.GetValueOrDefault();
        return update
            ? UpdateLocalSelectionCore(view, anchorX, anchorY, focusX, focusY, background.HasValue ? &bg : null, foreground.HasValue ? &fg : null, behavior)
            : SetLocalSelectionCore(view, anchorX, anchorY, focusX, focusY, background.HasValue ? &bg : null, foreground.HasValue ? &fg : null, behavior);
    }

    internal static void SetTextSelection(uint view, uint start, uint end, NativeRgba? background, NativeRgba? foreground)
    {
        var bg = background.GetValueOrDefault();
        var fg = foreground.GetValueOrDefault();
        SetSelectionCore(view, start, end, background.HasValue ? &bg : null, foreground.HasValue ? &fg : null);
    }

    internal static string GetSelectedText(uint buffer, uint view)
    {
        var size = checked((int)TextByteSize(buffer));
        if (size == 0) return "";
        var bytes = new byte[size];
        fixed (byte* output = bytes)
        {
            var written = GetSelectedTextCore(view, output, (uint)bytes.Length);
            if (written > bytes.Length) throw new InvalidOperationException("Native selected-text length exceeds its source buffer bound.");
            return new UTF8Encoding(false, true).GetString(bytes.AsSpan(0, checked((int)written)));
        }
    }
}
