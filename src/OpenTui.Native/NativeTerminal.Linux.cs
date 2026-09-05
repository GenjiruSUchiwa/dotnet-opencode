namespace OpenTui.Native;

using System.Runtime.InteropServices;

public sealed partial class NativeTerminal
{
    private sealed unsafe partial class UnixTerminalMode
    {
        /// <summary>Named Linux libc boundary. Common declarations share the verified
        /// public termios/poll/read ABI; ioctl's request width is selected explicitly.</summary>
        private static partial class Linux
        {
            private enum LibcAbi { Glibc, Musl }

            // .NET 11 uses this name in Interop.Libraries. CoreCLR FixLibCName maps it
            // to glibc's LIBC_SO (libc.so.6), or libc.so on musl. musl builds its shared
            // libc as libc.so; its ld-musl loader is that same library. No process-wide
            // DllImportResolver, native-library probing, or glibc fallback on musl.
            private const string Library = "libc";

            // RuntimeIdentifier is opaque: recognize supported .NET portable RID values
            // as whole values, rather than guessing a libc from a distro-name substring.
            private static LibcAbi Abi => (RuntimeInformation.RuntimeIdentifier, RuntimeInformation.ProcessArchitecture) switch
            {
                ("linux-x64", Architecture.X64) or ("linux-arm64", Architecture.Arm64) => LibcAbi.Glibc,
                ("linux-musl-x64", Architecture.X64) or ("linux-musl-arm64", Architecture.Arm64) => LibcAbi.Musl,
                _ => throw new PlatformNotSupportedException("Unsupported Linux terminal runtime ABI. Expected linux[-musl]-x64 or linux[-musl]-arm64 matching the process architecture.")
            };

            internal static void RequireSupportedAbi() => _ = Abi;

            internal static int GetWindowSize(int descriptor, out WindowSize size) => Abi == LibcAbi.Musl
                ? MuslGetWindowSize(descriptor, 0x5413, out size)
                : GlibcGetWindowSize(descriptor, 0x5413u, out size);

            [LibraryImport(Library, EntryPoint = "isatty", SetLastError = true)]
            internal static partial int IsATty(int descriptor);
            [LibraryImport(Library, EntryPoint = "tcgetattr", SetLastError = true)]
            internal static partial int GetAttributes(int descriptor, out LinuxTermios attributes);
            [LibraryImport(Library, EntryPoint = "tcsetattr", SetLastError = true)]
            internal static partial int SetAttributes(int descriptor, int action, in LinuxTermios attributes);
            [LibraryImport(Library, EntryPoint = "tcgetpgrp", SetLastError = true)]
            internal static partial int GetTerminalProcessGroup(int descriptor);
            [LibraryImport(Library, EntryPoint = "getpgrp")]
            internal static partial int GetProcessGroup();
            // Both Linux libcs use unsigned long nfds_t (64 bits on these ABIs).
            [LibraryImport(Library, EntryPoint = "poll", SetLastError = true)]
            internal static partial int Poll(ref PollDescriptor descriptors, nuint count, int timeout);
            [LibraryImport(Library, EntryPoint = "read", SetLastError = true)]
            internal static partial nint Read(int descriptor, byte* buffer, nuint count);

            // musl include/sys/ioctl.h and src/misc/ioctl.c declare int request;
            // glibc sys/ioctl.h declares unsigned long request. Do not conflate them
            // merely because this particular positive request number fits both.
            [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
            private static partial int MuslGetWindowSize(int descriptor, int request, out WindowSize size);
            [LibraryImport(Library, EntryPoint = "ioctl", SetLastError = true)]
            private static partial int GlibcGetWindowSize(int descriptor, nuint request, out WindowSize size);
        }
    }
}
