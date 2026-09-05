namespace OpenTui.Native;
using Transport;

using System.Text;

[Flags]
public enum NativeEmbeddedModifiers : ushort { None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8, CapsLock = 16, NumLock = 32 }
public enum NativeEmbeddedKeyAction : byte { Release, Press, Repeat }
public enum NativeEmbeddedMouseAction : byte { Press, Release, Motion }
public enum NativeEmbeddedMouseButton : sbyte { None = -1, Unknown, Left, Right, Middle, Four, Five, Six, Seven }
public enum NativeEmbeddedCursorStyle : byte { Bar, Block, Underline, HollowBlock }
public sealed record NativeEmbeddedKey(string PhysicalKey, string Text, NativeEmbeddedModifiers Modifiers = NativeEmbeddedModifiers.None,
    NativeEmbeddedKeyAction Action = NativeEmbeddedKeyAction.Press, NativeEmbeddedModifiers ConsumedModifiers = NativeEmbeddedModifiers.None,
    bool Composing = false, uint UnshiftedCodepoint = 0);
public readonly record struct NativeEmbeddedCursor(ushort X, ushort Y, bool HasValue, bool Visible, bool Blinking,
    bool WideTail, NativeEmbeddedCursorStyle Style, NativeRgba? Color);
public readonly record struct NativeCursorAppearance(byte Style, bool Blinking);

/// <summary>Owns one OpenTUI embedded emulator. All calls must be serialized with Dispose.</summary>
public sealed class NativeEmbeddedTerminal : IDisposable
{
    private uint _handle;
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public NativeEmbeddedTerminal(int columns = 80, int rows = 24, uint maxScrollback = 10000)
    {
        Check(OpenTuiNative.CreateEmbeddedTerminal(Dimension(columns), Dimension(rows), maxScrollback, out _handle), "creation");
        if (_handle == 0) throw new InvalidOperationException("Embedded terminal creation returned no handle.");
        Columns = columns; Rows = rows;
    }
    public void Write(ReadOnlySpan<byte> bytes) { Guard(); Check(OpenTuiNative.WriteEmbedded(_handle, bytes), "write"); }
    public void Resize(int columns, int rows)
    {
        Guard(); Check(OpenTuiNative.ResizeEmbeddedTerminal(_handle, Dimension(columns), Dimension(rows)), "resize");
        Columns = columns; Rows = rows;
    }
    public void Invalidate() { Guard(); Check(OpenTuiNative.InvalidateEmbeddedTerminal(_handle), "invalidation"); }
    public void Scroll(int rows) { Guard(); Check(OpenTuiNative.ScrollEmbeddedTerminal(_handle, rows), "scroll"); }
    public void Compose(uint targetBuffer, int x, int y)
    {
        Guard(); ArgumentOutOfRangeException.ThrowIfZero(targetBuffer);
        Check(OpenTuiNative.ComposeEmbeddedTerminal(_handle, targetBuffer, x, y), "composition");
    }
    public NativeEmbeddedCursor Cursor()
    {
        Guard(); Check(OpenTuiNative.GetEmbeddedCursor(_handle, out var cursor), "cursor query");
        return new(cursor.X, cursor.Y, cursor.HasValue != 0, cursor.Visible != 0, cursor.Blinking != 0,
            cursor.WideTail != 0, (NativeEmbeddedCursorStyle)cursor.Style,
            cursor.ColorHasValue == 0 ? null : new NativeRgba(cursor.R, cursor.G, cursor.B));
    }
    public byte[] EncodeKey(NativeEmbeddedKey key)
    {
        Guard();
        if (!Enum.IsDefined(key.Action) || ((ushort)key.Modifiers & ~63) != 0 || ((ushort)key.ConsumedModifiers & ~63) != 0 || key.UnshiftedCodepoint > 0x1fffff)
            throw new ArgumentException("Invalid embedded terminal key.", nameof(key));
        var physical = Encoding.UTF8.GetBytes(key.PhysicalKey);
        var text = Encoding.UTF8.GetBytes(key.Text);
        var options = new OpenTuiNative.EmbeddedKeyData { Action = (byte)key.Action, Composing = key.Composing ? (byte)1 : (byte)0,
            Mods = (ushort)key.Modifiers, ConsumedMods = (ushort)key.ConsumedModifiers, UnshiftedCodepoint = key.UnshiftedCodepoint };
        var output = new byte[Math.Max(64, text.Length)];
        var status = OpenTuiNative.EncodeEmbeddedKey(_handle, in options, physical, text, output, out var required);
        if (status == -4 && required > output.Length)
        {
            output = new byte[checked((int)required)];
            status = OpenTuiNative.EncodeEmbeddedKey(_handle, in options, physical, text, output, out _);
        }
        return Written(output, status, "key encoding");
    }
    public byte[] EncodeMouse(NativeEmbeddedMouseAction action, NativeEmbeddedMouseButton button, NativeEmbeddedModifiers modifiers,
        float x, float y, bool anyButtonPressed)
    {
        Guard();
        if (!Enum.IsDefined(action) || !Enum.IsDefined(button) || ((ushort)modifiers & ~63) != 0 || !float.IsFinite(x) || !float.IsFinite(y))
#pragma warning disable MA0015 // One existing diagnostic covers the compound native mouse packet, not a single parameter.
            throw new ArgumentException("Invalid embedded terminal mouse event.");
#pragma warning restore MA0015
        var output = new byte[128];
        return Written(output, OpenTuiNative.EncodeEmbeddedMouse(_handle, (byte)action, (sbyte)button, (ushort)modifiers, x, y, anyButtonPressed, output), "mouse encoding");
    }
    public byte[] EncodePaste(ReadOnlySpan<byte> bytes)
    {
        Guard(); var output = new byte[checked(bytes.Length + 16)];
        return Written(output, OpenTuiNative.EncodeEmbeddedPaste(_handle, bytes, output), "paste encoding");
    }
    public byte[] EncodeFocus(bool focused)
    {
        Guard(); var output = new byte[16];
        return Written(output, OpenTuiNative.EncodeEmbeddedFocus(_handle, focused, output), "focus encoding");
    }
    public byte[] DrainResponses()
    {
        Guard();
        using var result = new SequenceBuffer();
        const int chunkSize = 64 * 1024;
        while (true)
        {
            var status = OpenTuiNative.DrainEmbedded(_handle, result.GetMemory(chunkSize).Span[..chunkSize]);
            // The native response-overflow flag is cleared by a drain; retain
            // already buffered responses, as the upstream wrapper does.
            if (status == -4) continue;
            Check(status, "response drain");
            if (status > chunkSize) throw new InvalidOperationException("Invalid native response length.");
            result.Commit(status);
            if (status < chunkSize) return result.ToArray();
        }
    }
    public void Select(int x1, int y1, int x2, int y2)
    {
        Guard();
        Check(OpenTuiNative.SelectEmbeddedTerminal(_handle, Coordinate(x1, Columns), Coordinate(y1, Rows), Coordinate(x2, Columns), Coordinate(y2, Rows)), "selection");
    }
    public void ClearSelection() { Guard(); Check(OpenTuiNative.ClearEmbeddedSelection(_handle), "selection clear"); }
    public string SelectedText()
    {
        Guard(); var output = new byte[256];
        var status = OpenTuiNative.ReadEmbeddedSelection(_handle, output, out var required);
        if (status == -4 && required > output.Length)
        {
            output = new byte[checked((int)required)];
            status = OpenTuiNative.ReadEmbeddedSelection(_handle, output, out _);
        }
        return new UTF8Encoding(false, true).GetString(Written(output, status, "selected text"));
    }
    public void Dispose() { var handle = _handle; _handle = 0; if (handle != 0) OpenTuiNative.DestroyEmbeddedTerminal(handle); }
    private void Guard() => ObjectDisposedException.ThrowIf(_handle == 0, this);
    private static ushort Dimension(int value) => value is > 0 and <= ushort.MaxValue ? (ushort)value : throw new ArgumentOutOfRangeException(nameof(value));
    private static ushort Coordinate(int value, int size) => value >= 0 && value < size ? (ushort)value : throw new ArgumentOutOfRangeException(nameof(value));
    private static byte[] Written(byte[] output, int status, string operation)
    {
        Check(status, operation);
        if (status > output.Length) throw new InvalidOperationException("Native output exceeded its buffer.");
        return output.AsSpan(0, status).ToArray();
    }
    private static void Check(int status, string operation)
    {
        if (status >= 0) return;
        var reason = status switch { -1 => "invalid value or handle", -2 => "out of memory", -3 => "native embedded-terminal support is unavailable", -4 => "output buffer limit", _ => "processing failed" };
        throw new InvalidOperationException($"Embedded terminal {operation} failed: {reason}.");
    }
}
