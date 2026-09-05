namespace OpenTui.Native;

using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>Windows CF_UNICODETEXT clipboard writer. Call only in response to a user's copy action.</summary>
public static partial class NativeClipboard
{
    public static void WriteText(string text, IntPtr owner = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native clipboard adapter supports Windows only.");
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) throw new ArgumentException("Clipboard text cannot contain embedded NUL characters.", nameof(text));
        if (owner == IntPtr.Zero) owner = GetConsoleWindow();
        if (owner == IntPtr.Zero) throw new InvalidOperationException("Clipboard writing requires a window owner.");
        if (!OpenClipboard(owner)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open the clipboard.");
        var memory = IntPtr.Zero;
        try
        {
            memory = GlobalAlloc(0x0002, checked((nuint)((long)text.Length + 1) * 2));
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not allocate clipboard text.");
            var address = GlobalLock(memory);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not lock clipboard text.");
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, address, text.Length);
                Marshal.WriteInt16(address, checked(text.Length * 2), 0);
            }
            finally { GlobalUnlock(memory); }
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not clear the clipboard.");
            if (SetClipboardData(13, memory) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not transfer clipboard text.");
            memory = IntPtr.Zero; // Successful SetClipboardData transfers ownership to Windows.
        }
        finally
        {
            try { if (memory != IntPtr.Zero) GlobalFree(memory); }
            finally { CloseClipboard(); }
        }
    }

    [LibraryImport("kernel32.dll")] private static partial IntPtr GetConsoleWindow();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll", SetLastError = true)] private static partial IntPtr SetClipboardData(uint format, IntPtr memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalLock(IntPtr memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial IntPtr GlobalFree(IntPtr memory);
}
