namespace OpenTui.Native;

using System.ComponentModel;
using System.Runtime.InteropServices;

public enum NativeTerminalInputKind { WindowsConsoleEvents, UnixBytes }
public readonly record struct NativeTerminalSize(int Width, int Height, int PixelWidth, int PixelHeight);

public sealed partial class NativeTerminal
{
    /// <summary>Reads stdout's actual TIOCGWINSZ dimensions before construction or during
    /// resize polling. Does not initialize Console PAL or change terminal modes.</summary>
    public static NativeTerminalSize GetUnixSize() => UnixTerminalMode.GetSize();
    /// <summary>Polls Unix stdin without changing its file-status flags. False means no bytes
    /// are available; true with bytesRead=0 means EOF/hangup. Serialize with Dispose and
    /// use exactly one input reader. The host owns UTF-8/VT decoding and Ctrl+C policy.</summary>
    public bool TryReadUnixInput(Span<byte> destination, out int bytesRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_unix is null) throw new PlatformNotSupportedException("Use the Windows console-event reader on Windows.");
        if (destination.IsEmpty) throw new ArgumentException("An input buffer must not be empty.", nameof(destination));
        return _unix.TryRead(destination, out bytesRead);
    }

    /// <summary>One process-local owner of borrowed stdin's termios. No finalizer, signal
    /// handlers, Console PAL calls, descriptor replacement, or output escape sequences.</summary>
    private sealed unsafe partial class UnixTerminalMode : IDisposable
    {
        private const int Stdin = 0;
        private const int Stdout = 1;
        private const int Now = 0; // TCSANOW on all supported ABIs; never TCSAFLUSH.
        private const int Interrupted = 4;
        private const short Readable = 0x1;
        private const short PollError = 0x8;
        private const short Hangup = 0x10;
        private const short InvalidDescriptor = 0x20;
        private static int _owner;
        private readonly bool _darwin;
        private LinuxTermios _linuxSaved;
        private DarwinTermios _darwinSaved;
        private bool _restore;
        private bool _disposed;

        private UnixTerminalMode(bool darwin) => _darwin = darwin;

        internal static UnixTerminalMode Capture()
        {
            RequireSupportedAbi();
            if (Interlocked.CompareExchange(ref _owner, 1, 0) != 0)
                throw new InvalidOperationException("Another NativeTerminal owns Unix stdin. Dispose it before acquiring terminal modes again.");
            var terminal = new UnixTerminalMode(OperatingSystem.IsMacOS());
            try
            {
                // This checks actual descriptors without causing .NET Console PAL to cache modes.
                if ((terminal._darwin ? Darwin.IsATty(Stdin) : Linux.IsATty(Stdin)) != 1
                    || (terminal._darwin ? Darwin.IsATty(Stdout) : Linux.IsATty(Stdout)) != 1)
                    throw new InvalidOperationException("NativeTerminal requires interactive TTY input and output.");
                var foreground = terminal._darwin ? Darwin.GetTerminalProcessGroup(Stdin) : Linux.GetTerminalProcessGroup(Stdin);
                if (foreground == -1) throw Failure("Could not inspect the terminal foreground process group.");
                if (foreground != (terminal._darwin ? Darwin.GetProcessGroup() : Linux.GetProcessGroup()))
                    throw new InvalidOperationException("NativeTerminal must own a foreground terminal; background mode changes could suspend the process.");
                int result;
                do
                {
                    result = terminal._darwin ? Darwin.GetAttributes(Stdin, out terminal._darwinSaved)
                        : Linux.GetAttributes(Stdin, out terminal._linuxSaved);
                } while (result == -1 && Marshal.GetLastPInvokeError() == Interrupted);
                if (result != 0) throw Failure("Could not capture terminal modes.");
                return terminal;
            }
            catch
            {
                // Capture never changes termios; only release the acquisition guard here.
                Volatile.Write(ref _owner, 0);
                throw;
            }
        }

        private static void RequireSupportedAbi()
        {
            if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) || OperatingSystem.IsAndroid()
                || !BitConverter.IsLittleEndian || IntPtr.Size != 8
                || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
                throw new PlatformNotSupportedException("Unix terminal mode support is limited to little-endian Linux (glibc or musl) and macOS x64/arm64 processes.");
            if (OperatingSystem.IsLinux()) Linux.RequireSupportedAbi();
        }

        internal static NativeTerminalSize GetSize()
        {
            RequireSupportedAbi();
            var darwin = OperatingSystem.IsMacOS();
            if ((darwin ? Darwin.IsATty(Stdout) : Linux.IsATty(Stdout)) != 1)
                throw new InvalidOperationException("Terminal dimensions require interactive stdout.");
            WindowSize size;
            int result;
            do { result = darwin ? Darwin.GetWindowSize(Stdout, 0x40087468u, out size) : Linux.GetWindowSize(Stdout, out size); }
            while (result == -1 && Marshal.GetLastPInvokeError() == Interrupted);
            if (result != 0) throw Failure("Could not query terminal dimensions.");
            if (size.Columns == 0 || size.Rows == 0) throw new IOException("The terminal did not provide nonzero cell dimensions.");
            return new(size.Columns, size.Rows, size.PixelWidth, size.PixelHeight);
        }

        internal void EnterRaw()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Match libuv UV_TTY_MODE_RAW (the OpenTUI JS host uses setRawMode(true)),
            // not cfmakeraw/UV_TTY_MODE_IO: preserve output processing and enable ONLCR.
            // Clear BRKINT|ICRNL|INPCK|ISTRIP|IXON, enable CS8, clear
            // ECHO|ICANON|IEXTEN|ISIG, set VMIN=1 and VTIME=0.
            int result;
            if (_darwin)
            {
                var raw = _darwinSaved;
                raw.InputFlags &= ~(0x2ul | 0x100ul | 0x10ul | 0x20ul | 0x200ul);
                raw.OutputFlags |= 0x2ul;
                raw.ControlFlags |= 0x300ul;
                raw.LocalFlags &= ~(0x8ul | 0x100ul | 0x400ul | 0x80ul);
                raw.ControlCharacters[16] = 1;
                raw.ControlCharacters[17] = 0;
                // Even a failed tcsetattr is followed by restoration during setup cleanup.
                _restore = true;
                do { result = Darwin.SetAttributes(Stdin, Now, in raw); }
                while (result == -1 && Marshal.GetLastPInvokeError() == Interrupted);
            }
            else
            {
                var raw = _linuxSaved;
                raw.InputFlags &= ~(0x2u | 0x100u | 0x10u | 0x20u | 0x400u);
                raw.OutputFlags |= 0x4u;
                raw.ControlFlags |= 0x30u;
                raw.LocalFlags &= ~(0x8u | 0x2u | 0x8000u | 0x1u);
                raw.ControlCharacters[6] = 1;
                raw.ControlCharacters[5] = 0;
                _restore = true;
                do { result = Linux.SetAttributes(Stdin, Now, in raw); }
                while (result == -1 && Marshal.GetLastPInvokeError() == Interrupted);
            }
            if (result != 0) throw Failure("Could not enable raw terminal input.");
        }

        internal bool TryRead(Span<byte> destination, out int bytesRead)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bytesRead = 0;
            var descriptor = new PollDescriptor { Descriptor = Stdin, Events = Readable };
            var available = _darwin ? Darwin.Poll(ref descriptor, 1u, 0) : Linux.Poll(ref descriptor, 1u, 0);
            if (available == 0) return false;
            if (available == -1)
            {
                if (Marshal.GetLastPInvokeError() == Interrupted) return false;
                throw Failure("Could not poll terminal input.");
            }
            if ((descriptor.ReturnedEvents & InvalidDescriptor) != 0) throw new IOException("The terminal input descriptor is no longer valid.");
            if ((descriptor.ReturnedEvents & (Readable | Hangup)) == 0)
            {
                if ((descriptor.ReturnedEvents & PollError) != 0) throw new IOException("The terminal input descriptor reported an error.");
                return false;
            }
            nint count;
            fixed (byte* buffer = destination)
                count = _darwin ? Darwin.Read(Stdin, buffer, (nuint)destination.Length) : Linux.Read(Stdin, buffer, (nuint)destination.Length);
            if (count == -1)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == Interrupted || error == (_darwin ? 35 : 11)) return false; // EAGAIN/EWOULDBLOCK.
                throw new Win32Exception(error, "Could not read terminal input.");
            }
            bytesRead = checked((int)count);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_restore) return;
                int result;
                do { result = _darwin ? Darwin.SetAttributes(Stdin, Now, in _darwinSaved) : Linux.SetAttributes(Stdin, Now, in _linuxSaved); }
                while (result == -1 && Marshal.GetLastPInvokeError() == Interrupted);
                if (result != 0) throw Failure("Could not restore the saved Unix terminal modes.");
            }
            finally { Volatile.Write(ref _owner, 0); }
        }

        private static Win32Exception Failure(string message) => new(Marshal.GetLastPInvokeError(), message);

        // Shared glibc/musl x64/arm64 public layout, NOT the smaller kernel TCGETS
        // structure. glibc declarations: libc 0.2.189 linux/gnu/mod.rs and b64
        // x86_64/aarch64 modules. musl v1.2.5 include/termios.h + generic bits/termios.h:
        // tcflag_t/speed_t=u32, cc_t=u8, NCCS=32, c_line then c_cc, speed fields
        // at 52/56 (musl names them __c_ispeed/__c_ospeed). No second mode algorithm.
        [StructLayout(LayoutKind.Explicit, Size = 60)]
        private struct LinuxTermios
        {
            [FieldOffset(0)] internal uint InputFlags;
            [FieldOffset(4)] internal uint OutputFlags;
            [FieldOffset(8)] internal uint ControlFlags;
            [FieldOffset(12)] internal uint LocalFlags;
            [FieldOffset(16)] internal byte LineDiscipline;
            [FieldOffset(17)] internal fixed byte ControlCharacters[32];
            [FieldOffset(52)] internal uint InputSpeed;
            [FieldOffset(56)] internal uint OutputSpeed;
        }

        // libc 0.2.189: unix/bsd/{mod.rs,apple/mod.rs}. Darwin LP64 tcflag_t and
        // speed_t are unsigned long (8 bytes); NCCS=20; there is no c_line member.
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        private struct DarwinTermios
        {
            [FieldOffset(0)] internal ulong InputFlags;
            [FieldOffset(8)] internal ulong OutputFlags;
            [FieldOffset(16)] internal ulong ControlFlags;
            [FieldOffset(24)] internal ulong LocalFlags;
            [FieldOffset(32)] internal fixed byte ControlCharacters[20];
            [FieldOffset(56)] internal ulong InputSpeed;
            [FieldOffset(64)] internal ulong OutputSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollDescriptor
        {
            internal int Descriptor;
            internal short Events;
            internal short ReturnedEvents;
        }

        // Linux asm-generic/termios.h and Darwin bsd/sys/ttycom.h: four u16 values.
        [StructLayout(LayoutKind.Sequential, Size = 8)]
        private struct WindowSize
        {
            internal ushort Rows;
            internal ushort Columns;
            internal ushort PixelWidth;
            internal ushort PixelHeight;
        }

        private static partial class Darwin
        {
            private const string Library = "/usr/lib/libSystem.B.dylib";
            [LibraryImport(Library, EntryPoint = "isatty", SetLastError = true)] internal static partial int IsATty(int descriptor);
            [LibraryImport(Library, EntryPoint = "tcgetattr", SetLastError = true)] internal static partial int GetAttributes(int descriptor, out DarwinTermios attributes);
            [LibraryImport(Library, EntryPoint = "tcsetattr", SetLastError = true)] internal static partial int SetAttributes(int descriptor, int action, in DarwinTermios attributes);
            [LibraryImport(Library, EntryPoint = "tcgetpgrp", SetLastError = true)] internal static partial int GetTerminalProcessGroup(int descriptor);
            [LibraryImport(Library, EntryPoint = "getpgrp")] internal static partial int GetProcessGroup();
            [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)] internal static partial int Poll(ref PollDescriptor descriptors, uint count, int timeout);
            [LibraryImport(Library, EntryPoint = "read", SetLastError = true)] internal static partial nint Read(int descriptor, byte* buffer, nuint count);
            // Apple libsyscall/wrappers/ioctl.c declares this fixed-argument shim.
            // The macOS 11.3 SDK exports ___ioctl (Mach-O spelling) for x64/arm64.
            // Binding public variadic ioctl as a fixed 3-argument call is wrong on arm64.
            [LibraryImport("/usr/lib/system/libsystem_kernel.dylib", EntryPoint = "__ioctl", SetLastError = true)]
            internal static partial int GetWindowSize(int descriptor, nuint request, out WindowSize size);
        }
    }
}
