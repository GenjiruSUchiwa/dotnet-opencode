namespace OpenTui.Blazor;

using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>Reads console records without dropping mouse events as Console.ReadKey does.</summary>
internal sealed class WindowsConsoleInput : IDisposable
{
    private readonly IntPtr _handle;
    private readonly uint _mode;
    private readonly Queue<object> _pending = [];
    private uint _buttons;
    private ConsoleKeyInfo _repeat;
    private int _remaining;

    public WindowsConsoleInput()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _handle = GetStdHandle(-10);
        if (!GetConsoleMode(_handle, out _mode) || !SetConsoleMode(_handle, _mode | 0x10))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public bool TryRead(out object? item)
    {
        if (_pending.TryDequeue(out item)) return true;
        if (_remaining > 0) { _remaining--; item = _repeat; return true; }
        if (!GetNumberOfConsoleInputEvents(_handle, out var count)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (count == 0) { item = null; return false; }
        if (!ReadConsoleInputW(_handle, out var record, 1, out var read)) throw new Win32Exception(Marshal.GetLastWin32Error());
        item = null;
        if (read == 0) return false;
        if (IsKey(record))
        {
            var modifiers = Modifiers(record.KeyModifiers);
            _repeat = new(record.Character, (ConsoleKey)record.Key, modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control));
            _remaining = Math.Max(0, record.Repeat - 1);
            item = _repeat;
            return true;
        }
        if (record.Type != 2) return true;
        var x = record.X - Console.WindowLeft;
        var y = record.Y - Console.WindowTop;
        var flags = Modifiers(record.MouseModifiers);
        if ((record.MouseFlags & 12) != 0)
        {
            var delta = (short)(record.Buttons >> 16);
            var steps = delta == 0 ? 0 : Math.Sign(delta) * Math.Max(1, Math.Abs((int)delta) / 120);
            item = new TerminalPointerInput(TerminalPointerKind.Wheel, x, y, Modifiers: flags,
                DeltaX: (record.MouseFlags & 8) != 0 ? steps : 0, DeltaY: (record.MouseFlags & 4) != 0 ? -steps : 0);
            return true;
        }
        var buttons = record.Buttons & 31;
        for (var index = 0; index < 5; index++)
        {
            var mask = 1u << index;
            if ((_buttons & mask) == (buttons & mask)) continue;
            _pending.Enqueue(new TerminalPointerInput((buttons & mask) != 0 ? TerminalPointerKind.Down : TerminalPointerKind.Up,
                x, y, Button(index), flags));
        }
        _buttons = buttons;
        if ((record.MouseFlags & 1) != 0)
            _pending.Enqueue(new TerminalPointerInput(TerminalPointerKind.Move, x, y,
                buttons == 0 ? TerminalPointerButton.None : Button(Enumerable.Range(0, 5).First(index => (buttons & (1u << index)) != 0)), flags));
        _pending.TryDequeue(out item);
        return true;
    }

    private static TerminalPointerButton Button(int index) => index switch
    {
        0 => TerminalPointerButton.Left, 1 => TerminalPointerButton.Right, 2 => TerminalPointerButton.Middle,
        3 => TerminalPointerButton.Back, _ => TerminalPointerButton.Forward
    };
    // ConsolePal.Windows.IsReadKeyEvent: retain IME/pasted/Alt-numpad Unicode
    // carried on Alt key-up, without leaking the intermediate numpad keystrokes.
    private static bool IsKey(InputRecord record)
    {
        if (record.Type != 1) return false;
        if (record.KeyDown == 0) return record.Key == 18 && record.Character != '\0';
        if (record.Key is 16 or 17 or 18 or 20 or 144 or 145) return false;
        if ((record.KeyModifiers & 3) == 0) return true;
        if (record.Key is >= 96 and <= 105) return false;
        if ((record.KeyModifiers & 256) == 0 && record.Key is 12 or 45 or >= 33 and <= 40) return false;
        return true;
    }
    private static ConsoleModifiers Modifiers(uint value) => ((value & 16) != 0 ? ConsoleModifiers.Shift : ConsoleModifiers.None)
        | ((value & 3) != 0 ? ConsoleModifiers.Alt : ConsoleModifiers.None) | ((value & 12) != 0 ? ConsoleModifiers.Control : ConsoleModifiers.None);

    public void Dispose()
    {
        if (!SetConsoleMode(_handle, _mode)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort Key;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint KeyModifiers;
        [FieldOffset(4)] public short X;
        [FieldOffset(6)] public short Y;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(12)] public uint MouseModifiers;
        [FieldOffset(16)] public uint MouseFlags;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfConsoleInputEvents(IntPtr handle, out uint count);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleInputW(IntPtr handle, out InputRecord record, uint count, out uint read);
}
