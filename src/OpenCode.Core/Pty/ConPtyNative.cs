namespace OpenCode.Core.Pty;

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Layouts and constants from the Windows SDK consoleapi.h, processthreadsapi.h,
// and winbase.h. HPCON is NOT a kernel HANDLE: release it with ClosePseudoConsole.
internal static partial class ConPtyNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord { internal short X; internal short Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx { internal StartupInfo StartupInfo; internal nint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { internal nint Process, Thread; internal uint ProcessId, ThreadId; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, uint size);

    [LibraryImport("kernel32.dll")]
    internal static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out SafePseudoConsole console);

    [LibraryImport("kernel32.dll")]
    internal static partial int ResizePseudoConsole(SafePseudoConsole console, Coord size);

    [LibraryImport("kernel32.dll")]
    internal static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, SafePseudoConsole value, nuint size, nint previous, nint returnedSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcess(string? application, nint commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    internal static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
}

internal sealed class SafePseudoConsole() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle() { ConPtyNative.ClosePseudoConsole(handle); return true; }
}

internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeThreadHandle(nint value) : base(true) { SetHandle(value); }
    protected override bool ReleaseHandle() => ConPtyNative.CloseHandle(handle);
}
