namespace OpenTui.Native;

using System.Runtime.InteropServices;

public static unsafe partial class OpenTuiNative
{
    /// <summary>OpenTUI renderer-scoped cursor color; RGBA uses the same packed lanes as buffer colors.</summary>
    public static void SetCursorColor(uint renderer, NativeRgba color)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderer);
        SetCursorColorCore(renderer, in color);
    }

    [LibraryImport(LibName, EntryPoint = "setCursorColor")]
    private static partial void SetCursorColorCore(uint renderer, in NativeRgba color);
}
