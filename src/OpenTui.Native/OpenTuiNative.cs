namespace OpenTui.Native;

using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)]
public struct NativeRgba
{
    public byte R;
    public byte G;
    public byte B;
    public byte A;

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

public static unsafe partial class OpenTuiNative
{
    private const string LibName = "opentui";

    static OpenTuiNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenTuiNative).Assembly, (name, assembly, path) =>
        {
            if (name == LibName)
            {
                var baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "opentui.dll"),
                    Path.Combine(baseDir, "runtimes", "win-x64", "native", "opentui.dll"),
                    @"C:\Users\Lukem\.bun\install\cache\@opentui\core-win32-x64@0.5.9@@@1\opentui.dll"
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        return handle;
                    }
                }
            }

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
        var bytes = Encoding.UTF8.GetBytes(title);
        fixed (byte* ptr = bytes)
        {
            SetTerminalTitle(renderer, ptr, (uint)bytes.Length);
        }
    }

    [LibraryImport(LibName, EntryPoint = "resizeRenderer")]
    public static partial void ResizeRenderer(uint renderer, uint width, uint height);

    [LibraryImport(LibName, EntryPoint = "setCursorPosition")]
    public static partial void SetCursorPosition(uint renderer, int x, int y, [MarshalAs(UnmanagedType.I1)] bool visible);

    [LibraryImport(LibName, EntryPoint = "getCurrentBuffer")]
    public static partial uint GetCurrentBuffer(uint renderer);

    [LibraryImport(LibName, EntryPoint = "getNextBuffer")]
    public static partial uint GetNextBuffer(uint renderer);

    [LibraryImport(LibName, EntryPoint = "bufferClear")]
    public static partial void BufferClear(uint buffer, byte* rgba);

    [LibraryImport(LibName, EntryPoint = "bufferDrawText")]
    public static partial void BufferDrawText(
        uint buffer,
        byte* textPtr,
        uint textLen,
        uint x,
        uint y,
        byte* fgRgba,
        byte* bgRgba,
        uint attributes);

    public static void DrawText(uint buffer, string text, uint x, uint y, NativeRgba fg, NativeRgba? bg = null, uint attributes = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var fgBytes = stackalloc byte[4] { fg.R, fg.G, fg.B, fg.A };
        byte* bgPtr = null;
        if (bg.HasValue)
        {
            var b = bg.Value;
            var bgBytes = stackalloc byte[4] { b.R, b.G, b.B, b.A };
            bgPtr = bgBytes;
        }

        fixed (byte* textPtr = bytes)
        {
            BufferDrawText(buffer, textPtr, (uint)bytes.Length, x, y, fgBytes, bgPtr, attributes);
        }
    }
}
