namespace OpenTui.Native;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

/// <summary>Owns OS terminal input modes and an alternate-screen renderer, but not the host input loop.</summary>
/// <remarks>Serialize construction, rendering, resizing, and disposal. Stop input/render callbacks before disposal.</remarks>
public sealed partial class NativeTerminal : IDisposable
{
    private const uint ProcessedInput = 0x0001;
    private const uint LineInput = 0x0002;
    private const uint EchoInput = 0x0004;
    private const uint QuickEditMode = 0x0040;
    private const uint ExtendedFlags = 0x0080;
    private const uint VirtualTerminalInput = 0x0200;
    private const uint ProcessedOutput = 0x0001;
    private const uint VirtualTerminalProcessing = 0x0004;

    public uint Renderer => _renderer?.Handle ?? 0;
    private NativeRenderer? _renderer;
    private bool _disposed;
    private readonly IntPtr _input;
    private readonly IntPtr _output;
    private readonly uint _inputMode;
    private readonly uint _outputMode;
    private readonly UnixTerminalMode? _unix;

    /// <summary>Unix hosts must consume bytes, not System.Console key-reading APIs.</summary>
    public NativeTerminalInputKind InputKind => _unix is null ? NativeTerminalInputKind.WindowsConsoleEvents : NativeTerminalInputKind.UnixBytes;

    public NativeTerminal(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (OperatingSystem.IsWindows())
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
                throw new InvalidOperationException("NativeTerminal requires interactive terminal input and output.");
            _input = GetStdHandle(-10);
            _output = GetStdHandle(-11);
            if (!GetConsoleMode(_input, out _inputMode) || !GetConsoleMode(_output, out _outputMode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else
        {
            // Capture checks the supported ABI and both TTY descriptors before changing
            // any mode. It deliberately does not initialize System.Console on Unix.
            _unix = UnixTerminalMode.Capture();
        }
        try
        {
            if (_unix is not null) _unix.EnterRaw();
            // VT output requires processed output. Ctrl+C remains input for the host.
            else if (!SetConsoleMode(_output, _outputMode | ProcessedOutput | VirtualTerminalProcessing)
                || !SetConsoleMode(_input, (_inputMode & ~(ProcessedInput | LineInput | EchoInput | QuickEditMode | VirtualTerminalInput)) | ExtendedFlags))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _renderer = new NativeRenderer(width, height);
            OpenTuiNative.SetUseThread(Renderer, false);
            // Preserve existing Windows keyboard behavior. Unix exposes legacy VT bytes;
            // its host must install a byte decoder before enabling other keyboard protocols.
            OpenTuiNative.SetKittyKeyboardFlags(Renderer, 0);
            OpenTuiNative.SetupTerminal(Renderer, true);
        }
        catch (Exception setupError)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Terminal setup and restoration both failed.", setupError, cleanupError);
            }
            throw;
        }
    }

    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer!.Resize(width, height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var renderer = _renderer;
        _renderer = null;
        List<Exception>? errors = null;
        try
        {
            // destroyRenderer performs shutdown. restoreTerminalModes re-enables active TUI modes.
            renderer?.Dispose();
        }
        catch (Exception error)
        {
            (errors ??= []).Add(error);
        }

        if (_unix is not null)
        {
            try { _unix.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        else
        {
            // Quick Edit changes require ExtendedFlags, even if the saved mode did not contain it.
            if (!SetConsoleMode(_input, _inputMode | ExtendedFlags))
                (errors ??= []).Add(new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore terminal input flags."));
            if (!SetConsoleMode(_input, _inputMode))
                (errors ??= []).Add(new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the saved terminal input mode."));
            if (!SetConsoleMode(_output, _outputMode))
                (errors ??= []).Add(new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore the saved terminal output mode."));
        }

        if (errors is { Count: 1 }) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors is { Count: > 1 }) throw new AggregateException("Terminal cleanup failed.", errors);
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int standardHandle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr handle, uint mode);
}
