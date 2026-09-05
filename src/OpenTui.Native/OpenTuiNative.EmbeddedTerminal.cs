namespace OpenTui.Native;

using System.Runtime.InteropServices;

public static unsafe partial class OpenTuiNative
{
    [StructLayout(LayoutKind.Sequential, Size = 14)]
    internal struct EmbeddedCursorData
    {
        public ushort X, Y;
        public byte HasValue, Visible, Blinking, WideTail, Style, ColorHasValue, R, G, B, Padding;
    }
    [StructLayout(LayoutKind.Sequential, Size = 12)]
    internal struct EmbeddedKeyData
    {
        public byte Action, Composing;
        public ushort Mods, ConsumedMods, Padding;
        public uint UnshiftedCodepoint;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct EmbeddedCursorStyleData
    {
        public byte Style, Blinking;
        public IntPtr Color;
        public byte MousePointer;
    }
    // lib.zig ExternalCursorState: u32 x/y, three u8 fields, aligned f32 RGBA.
    [StructLayout(LayoutKind.Sequential)]
    private struct HostCursorData
    {
        public uint X, Y;
        public byte Visible, Style, Blinking;
        public float R, G, B, A;
    }

    [LibraryImport(LibName, EntryPoint = "createEmbeddedTerminal")]
    internal static partial int CreateEmbeddedTerminal(ushort columns, ushort rows, uint scrollback, out uint handle);
    [LibraryImport(LibName, EntryPoint = "destroyEmbeddedTerminal")]
    internal static partial void DestroyEmbeddedTerminal(uint handle);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalResize")]
    internal static partial int ResizeEmbeddedTerminal(uint handle, ushort columns, ushort rows);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalInvalidate")]
    internal static partial int InvalidateEmbeddedTerminal(uint handle);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalScroll")]
    internal static partial int ScrollEmbeddedTerminal(uint handle, int delta);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalCompose")]
    internal static partial int ComposeEmbeddedTerminal(uint handle, uint buffer, int x, int y);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalCursor")]
    internal static partial int GetEmbeddedCursor(uint handle, out EmbeddedCursorData cursor);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalSetSelection")]
    internal static partial int SelectEmbeddedTerminal(uint handle, ushort x1, ushort y1, ushort x2, ushort y2);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalClearSelection")]
    internal static partial int ClearEmbeddedSelection(uint handle);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalWrite")]
    private static partial int WriteEmbeddedCore(uint handle, byte* bytes, uint length);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalEncodeKey")]
    private static partial int EncodeEmbeddedKeyCore(uint handle, in EmbeddedKeyData options, byte* key, uint keyLength,
        byte* text, uint textLength, byte* output, uint capacity, out uint required);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalEncodeMouse")]
    private static partial int EncodeEmbeddedMouseCore(uint handle, byte action, sbyte button, ushort mods, float x, float y,
        byte pressed, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalEncodePaste")]
    private static partial int EncodeEmbeddedPasteCore(uint handle, byte* input, uint length, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalEncodeFocus")]
    private static partial int EncodeEmbeddedFocusCore(uint handle, byte focused, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalDrainResponses")]
    private static partial int DrainEmbeddedCore(uint handle, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "embeddedTerminalGetSelectedText")]
    private static partial int ReadEmbeddedSelectionCore(uint handle, byte* output, uint capacity, out uint required);
    [LibraryImport(LibName, EntryPoint = "setCursorStyleOptions")]
    private static partial void SetEmbeddedCursorStyleCore(uint renderer, in EmbeddedCursorStyleData options);
    [LibraryImport(LibName, EntryPoint = "getCursorState")]
    private static partial void GetHostCursorCore(uint renderer, out HostCursorData cursor);

    internal static int WriteEmbedded(uint handle, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* input = bytes) return WriteEmbeddedCore(handle, input, (uint)bytes.Length);
    }
    internal static int EncodeEmbeddedKey(uint handle, in EmbeddedKeyData options, ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> text, Span<byte> output, out uint required)
    {
        fixed (byte* keyData = key)
        fixed (byte* textData = text)
        fixed (byte* destination = output)
            return EncodeEmbeddedKeyCore(handle, in options, keyData, (uint)key.Length, textData, (uint)text.Length, destination, (uint)output.Length, out required);
    }
    internal static int EncodeEmbeddedMouse(uint handle, byte action, sbyte button, ushort mods, float x, float y, bool pressed, Span<byte> output)
    {
        fixed (byte* destination = output) return EncodeEmbeddedMouseCore(handle, action, button, mods, x, y, pressed ? (byte)1 : (byte)0, destination, (uint)output.Length);
    }
    internal static int EncodeEmbeddedPaste(uint handle, ReadOnlySpan<byte> input, Span<byte> output)
    {
        fixed (byte* data = input)
        fixed (byte* destination = output) return EncodeEmbeddedPasteCore(handle, data, (uint)input.Length, destination, (uint)output.Length);
    }
    internal static int EncodeEmbeddedFocus(uint handle, bool focused, Span<byte> output)
    {
        fixed (byte* destination = output) return EncodeEmbeddedFocusCore(handle, focused ? (byte)1 : (byte)0, destination, (uint)output.Length);
    }
    internal static int DrainEmbedded(uint handle, Span<byte> output)
    {
        fixed (byte* destination = output) return DrainEmbeddedCore(handle, destination, (uint)output.Length);
    }
    internal static int ReadEmbeddedSelection(uint handle, Span<byte> output, out uint required)
    {
        fixed (byte* destination = output) return ReadEmbeddedSelectionCore(handle, destination, (uint)output.Length, out required);
    }
    public static void ApplyEmbeddedCursorStyle(uint renderer, NativeEmbeddedCursor cursor)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderer);
        var options = new EmbeddedCursorStyleData
        {
            Style = cursor.Style switch { NativeEmbeddedCursorStyle.Bar => 1, NativeEmbeddedCursorStyle.Underline => 2, _ => 0 },
            Blinking = cursor.Blinking ? (byte)1 : (byte)0, MousePointer = 255
        };
        SetEmbeddedCursorStyleCore(renderer, in options);
        if (cursor.Color is { } color) SetCursorColor(renderer, color);
    }
    public static NativeCursorAppearance GetCursorAppearance(uint renderer)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderer);
        GetHostCursorCore(renderer, out var cursor);
        return new(cursor.Style, cursor.Blinking != 0);
    }
    public static void SetCursorAppearance(uint renderer, NativeCursorAppearance cursor)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderer);
        if (cursor.Style > 3) throw new ArgumentOutOfRangeException(nameof(cursor));
        var options = new EmbeddedCursorStyleData { Style = cursor.Style, Blinking = cursor.Blinking ? (byte)1 : (byte)0, MousePointer = 255 };
        SetEmbeddedCursorStyleCore(renderer, in options);
    }
}
