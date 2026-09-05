namespace OpenTui.Native;

using System.Runtime.InteropServices;
using System.Text;
using System.Buffers;

[StructLayout(LayoutKind.Sequential)]
public struct NativeRgba
{
    // OpenTUI 0.5.9 packs RGBA8 plus color intent into four uint16 lanes.
    // RGB intent is zero; the low byte of each lane is the channel value.
    public ushort R;
    public ushort G;
    public ushort B;
    public ushort A;

    public NativeRgba(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public static NativeRgba White => new(255, 255, 255);
    public static NativeRgba Black => new(0, 0, 0);
    public static NativeRgba Gray => new(128, 128, 128);
    public static NativeRgba Cyan => new(0, 255, 255);
    public static NativeRgba Green => new(0, 255, 0);
}

/// <summary>Low-level OpenTUI 0.5.9 ABI. Renderer and buffer IDs are 32-bit handles, not pointers.</summary>
/// <remarks>
/// Prefer NativeRenderer for ownership. Raw calls require live handles, valid pointer lengths,
/// and host-serialized access. Renderer buffers are borrowed and must not outlive their renderer.
/// </remarks>
public static unsafe partial class OpenTuiNative
{
    private const string LibName = "opentui";

    static OpenTuiNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenTuiNative).Assembly, (name, assembly, path) =>
        {
            if (name != LibName) return IntPtr.Zero;

            // An explicit override is authoritative: do not silently load a different ABI.
            var configured = Environment.GetEnvironmentVariable("OPENTUI_LIBRARY_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathFullyQualified(configured))
                    throw new DllNotFoundException("OPENTUI_LIBRARY_PATH must be an absolute path to an OpenTUI 0.5.9 native library.");
                return NativeLibrary.Load(configured);
            }

            var fileName = OperatingSystem.IsWindows() ? "opentui.dll"
                : OperatingSystem.IsMacOS() ? "libopentui.dylib" : "libopentui.so";
            var platform = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsMacOS() ? "osx"
                : RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl", StringComparison.Ordinal) ? "linux-musl" : "linux";
            var rid = $"{platform}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
            var directories = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(assembly.Location) }
                .Where(directory => !string.IsNullOrEmpty(directory)).Distinct();
            foreach (var directory in directories)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(directory!, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName),
                    Path.Combine(directory!, "runtimes", rid, "native", fileName),
                    Path.Combine(directory!, fileName)
                }.Distinct())
                    if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
            }

            // Let .NET resolve NuGet runtime assets and normal native search paths.
            return IntPtr.Zero;
        });
    }

    [LibraryImport(LibName, EntryPoint = "createRenderer")]
    public static partial uint CreateRenderer(uint width, uint height, byte bufferedOutputKind, byte remoteMode, IntPtr feedPtr);

    [LibraryImport(LibName, EntryPoint = "destroyRenderer")]
    public static partial void DestroyRenderer(uint renderer, [MarshalAs(UnmanagedType.I1)] bool flushInput);

    [LibraryImport(LibName, EntryPoint = "render")]
    public static partial byte Render(uint renderer, [MarshalAs(UnmanagedType.I1)] bool force);

    [LibraryImport(LibName, EntryPoint = "clearTerminal")]
    public static partial void ClearTerminal(uint renderer);

    [LibraryImport(LibName, EntryPoint = "setTerminalTitle")]
    public static partial void SetTerminalTitle(uint renderer, byte* titlePtr, uint titleLen);

    public static void SetTitle(uint renderer, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        SetTitle(renderer, title.AsSpan());
    }

    public static void SetTitle(uint renderer, ReadOnlySpan<char> title)
    {
        Span<byte> scratch = stackalloc byte[512];
        byte[]? rented = null;
        try
        {
            SetTitle(renderer, EncodeUtf8(title, scratch, out rented));
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void SetTitle(uint renderer, ReadOnlySpan<byte> utf8)
    {
        fixed (byte* ptr = utf8) SetTerminalTitle(renderer, ptr, (uint)utf8.Length);
    }

    [LibraryImport(LibName, EntryPoint = "resizeRenderer")]
    public static partial void ResizeRenderer(uint renderer, uint width, uint height);

    [LibraryImport(LibName, EntryPoint = "setupTerminal")]
    public static partial void SetupTerminal(uint renderer, [MarshalAs(UnmanagedType.I1)] bool alternateScreen);

    [LibraryImport(LibName, EntryPoint = "restoreTerminalModes")]
    public static partial void RestoreTerminalModes(uint renderer);

    [LibraryImport(LibName, EntryPoint = "setUseThread")]
    public static partial void SetUseThread(uint renderer, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [LibraryImport(LibName, EntryPoint = "setKittyKeyboardFlags")]
    public static partial void SetKittyKeyboardFlags(uint renderer, byte flags);

    [LibraryImport(LibName, EntryPoint = "processCapabilityResponse")]
    private static partial void ProcessCapabilityResponse(uint renderer, byte* response, uint length);

    public static void ProcessResponse(uint renderer, string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ProcessResponse(renderer, response.AsSpan());
    }

    public static void ProcessResponse(uint renderer, ReadOnlySpan<char> response)
    {
        Span<byte> scratch = stackalloc byte[512];
        byte[]? rented = null;
        try
        {
            ProcessResponse(renderer, EncodeUtf8(response, scratch, out rented));
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void ProcessResponse(uint renderer, ReadOnlySpan<byte> response)
    {
        fixed (byte* ptr = response) ProcessCapabilityResponse(renderer, ptr, (uint)response.Length);
    }

    [LibraryImport(LibName, EntryPoint = "enableMouse")]
    public static partial void EnableMouse(uint renderer, [MarshalAs(UnmanagedType.I1)] bool movement);

    [LibraryImport(LibName, EntryPoint = "disableMouse")]
    public static partial void DisableMouse(uint renderer);

    [LibraryImport(LibName, EntryPoint = "enableKittyKeyboard")]
    public static partial void EnableKittyKeyboard(uint renderer, byte flags);

    [LibraryImport(LibName, EntryPoint = "disableKittyKeyboard")]
    public static partial void DisableKittyKeyboard(uint renderer);

    [LibraryImport(LibName, EntryPoint = "getBufferWidth")]
    public static partial uint GetBufferWidth(uint buffer);

    [LibraryImport(LibName, EntryPoint = "getBufferHeight")]
    public static partial uint GetBufferHeight(uint buffer);

    [LibraryImport(LibName, EntryPoint = "bufferPushScissorRect")]
    public static partial void PushScissor(uint buffer, int x, int y, uint width, uint height);

    [LibraryImport(LibName, EntryPoint = "bufferPopScissorRect")]
    public static partial void PopScissor(uint buffer);

    [LibraryImport(LibName, EntryPoint = "bufferFillRect")]
    private static partial void BufferFillRect(uint buffer, uint x, uint y, uint width, uint height, NativeRgba* color);

    public static void FillRect(uint buffer, int x, int y, int width, int height, NativeRgba color)
    {
        if (width > 0 && height > 0 && x >= 0 && y >= 0)
            BufferFillRect(buffer, (uint)x, (uint)y, (uint)width, (uint)height, &color);
    }

    public static void Clear(uint buffer, NativeRgba color) => BufferClear(buffer, &color);

    [LibraryImport(LibName, EntryPoint = "setCursorPosition")]
    public static partial void SetCursorPosition(uint renderer, int x, int y, [MarshalAs(UnmanagedType.I1)] bool visible);

    [LibraryImport(LibName, EntryPoint = "getCurrentBuffer")]
    public static partial uint GetCurrentBuffer(uint renderer);

    [LibraryImport(LibName, EntryPoint = "getNextBuffer")]
    public static partial uint GetNextBuffer(uint renderer);

    [LibraryImport(LibName, EntryPoint = "getBufferWidthMethod")]
    public static partial byte GetBufferWidthMethod(uint buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct EncodedChar
    {
        public byte Width;
        public uint Character;
    }

    [LibraryImport(LibName, EntryPoint = "encodeUnicode")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool EncodeUnicode(byte* text, uint length, out IntPtr encoded, out nuint count, byte widthMethod);

    [LibraryImport(LibName, EntryPoint = "freeUnicode")]
    private static partial void FreeUnicode(IntPtr encoded, uint count);

    // This encodes display cells, not keyboard input. Use the buffer's negotiated
    // width method (0 wcwidth, 1 unicode, 3 unicode-wide), not a managed approximation.
    public static int MeasureCellWidth(string text, byte widthMethod)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MeasureCellWidth(text.AsSpan(), widthMethod);
    }

    public static int MeasureCellWidth(ReadOnlySpan<char> text, byte widthMethod)
    {
        if (text.IsEmpty) return 0;
        Span<byte> scratch = stackalloc byte[512];
        byte[]? rented = null;
        try
        {
            return MeasureCellWidth(EncodeUtf8(text, scratch, out rented), widthMethod);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static int MeasureCellWidth(ReadOnlySpan<byte> utf8, byte widthMethod)
    {
        if (utf8.IsEmpty) return 0;
        IntPtr encoded;
        nuint count;
        fixed (byte* ptr = utf8)
        {
            if (!EncodeUnicode(ptr, (uint)utf8.Length, out encoded, out count, widthMethod))
                throw new InvalidOperationException("OpenTUI could not measure terminal text.");
        }
        try
        {
            var width = 0;
            var cells = (EncodedChar*)encoded;
            for (nuint i = 0; i < count; i++) width = checked(width + cells[i].Width);
            return width;
        }
        finally
        {
            if (encoded != IntPtr.Zero) FreeUnicode(encoded, checked((uint)count));
        }
    }

    [LibraryImport(LibName, EntryPoint = "bufferClear")]
    public static partial void BufferClear(uint buffer, NativeRgba* rgba);

    [LibraryImport(LibName, EntryPoint = "bufferDrawText")]
    public static partial void BufferDrawText(
        uint buffer,
        byte* textPtr,
        uint textLen,
        uint x,
        uint y,
        NativeRgba* fgRgba,
        NativeRgba* bgRgba,
        uint attributes);

    public static void DrawText(uint buffer, string text, uint x, uint y, NativeRgba fg, NativeRgba? bg = null, uint attributes = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        DrawText(buffer, text.AsSpan(), x, y, fg, bg, attributes);
    }

    public static void DrawText(uint buffer, ReadOnlySpan<char> text, uint x, uint y, NativeRgba fg, NativeRgba? bg = null, uint attributes = 0)
    {
        Span<byte> scratch = stackalloc byte[512];
        byte[]? rented = null;
        try
        {
            DrawText(buffer, EncodeUtf8(text, scratch, out rented), x, y, fg, bg, attributes);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static void DrawText(uint buffer, ReadOnlySpan<byte> utf8, uint x, uint y, NativeRgba fg, NativeRgba? bg = null, uint attributes = 0)
    {
        var background = bg.GetValueOrDefault();
        fixed (byte* textPtr = utf8)
        {
            BufferDrawText(buffer, textPtr, (uint)utf8.Length, x, y, &fg, bg.HasValue ? &background : null, attributes);
        }
    }

    [LibraryImport(LibName, EntryPoint = "bufferDrawBox")]
    private static partial void BufferDrawBox(uint buffer, int x, int y, uint width, uint height,
        uint* borderChars, uint options, NativeRgba* borderColor, NativeRgba* backgroundColor,
        NativeRgba* titleColor, byte* title, uint titleLength, byte* bottomTitle, uint bottomTitleLength);

    /// <summary>Draws a box in one native call, without creating border strings or per-cell calls.</summary>
    /// <param name="borderChars">Exactly 11 codepoints: TL, TR, BL, BR, horizontal, vertical, top-T, bottom-T, left-T, right-T, cross.</param>
    /// <param name="options">Left=1, bottom=2, right=4, top=8, fill=16. Title alignment (0 left, 1 center, 2 right) is shifted by 5; bottom alignment by 7.</param>
    public static void DrawBox(uint buffer, int x, int y, uint width, uint height,
        ReadOnlySpan<uint> borderChars, uint options, NativeRgba borderColor, NativeRgba backgroundColor,
        ReadOnlySpan<byte> title = default, ReadOnlySpan<byte> bottomTitle = default, NativeRgba? titleColor = null)
    {
        if (borderChars.Length != 11) throw new ArgumentException("OpenTUI requires exactly 11 border codepoints.", nameof(borderChars));
        var color = titleColor ?? borderColor;
        fixed (uint* chars = borderChars)
        fixed (byte* top = title)
        fixed (byte* bottom = bottomTitle)
            BufferDrawBox(buffer, x, y, width, height, chars, options, &borderColor, &backgroundColor,
                &color, top, (uint)title.Length, bottom, (uint)bottomTitle.Length);
    }

    private static ReadOnlySpan<byte> EncodeUtf8(ReadOnlySpan<char> text, Span<byte> scratch, out byte[]? rented)
    {
        rented = null;
        // Small text uses caller scratch; larger text is counted before renting sufficient capacity.
        if (Encoding.UTF8.TryGetBytes(text, scratch, out var written)) return scratch[..written];
        var length = Encoding.UTF8.GetByteCount(text);
        rented = ArrayPool<byte>.Shared.Rent(length);
        written = Encoding.UTF8.GetBytes(text, rented.AsSpan(0, length));
        return rented.AsSpan(0, written);
    }
}
