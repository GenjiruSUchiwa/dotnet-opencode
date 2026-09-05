namespace OpenTui.Blazor;

using System.Text;
using OpenTui.Native;

public enum EmbeddedTerminalDataSource { Input, Response }
public sealed record EmbeddedTerminalData(ReadOnlyMemory<byte> Bytes, EmbeddedTerminalDataSource Source);
public readonly record struct EmbeddedTerminalSize(int Columns, int Rows);

/// <summary>Dispatcher-owned embedded screen. No PTY, process, network, or application policy lives here.</summary>
public sealed class EmbeddedTerminalState : IDisposable
{
    private NativeEmbeddedTerminal? _terminal;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private char? _surrogate;
    private bool _disposed;
    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;
    public bool Focused { get; private set; }
    internal bool FocusRequested { get; set; }
    internal bool IsDisposed => _disposed;
    public Task Ready => _ready.Task;
    public Func<ConsoleKeyInfo, bool>? DeferKey { get; set; }
    public Func<TerminalKeyInput, bool>? DeferRichKey { get; set; }
    public event Action? Changed;
    public event Action<EmbeddedTerminalData>? Data;
    public event Action<EmbeddedTerminalSize>? Resized;
    public event Action<bool>? FocusChanged;
    internal NativeEmbeddedTerminal Terminal => _terminal ?? throw new InvalidOperationException("The embedded terminal is not mounted.");

    internal void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terminal is not null) return;
        try { _terminal = new(Columns, Rows); _ready.TrySetResult(); }
        catch (Exception exception) { _ready.TrySetException(exception); throw; }
    }
    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (columns is < 1 or > 65535 || rows is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(columns));
        if (Columns == columns && Rows == rows) return;
        _terminal?.Resize(columns, rows);
        Columns = columns; Rows = rows;
        if (_terminal is not null) FlushResponses();
        Resized?.Invoke(new(columns, rows));
        Changed?.Invoke();
    }
    public void Write(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Terminal.Write(bytes);
        FlushResponses();
        Changed?.Invoke();
    }
    public void SendKey(NativeEmbeddedKey key) => Send(Terminal.EncodeKey(key));
    public void RequestFocus() { FocusRequested = true; Changed?.Invoke(); }
    internal void SendKey(TerminalKeyInput key)
    {
        if (key.Hyper || key.Modifiers.HasFlag(TerminalKeyModifiers.KittyMeta))
            throw new NotSupportedException("The embedded-terminal ABI cannot encode independent Hyper or Kitty Meta modifiers.");
        var physical = key.ConsoleKey is { } console ? PhysicalKey(console.Key) : RichPhysicalKey(key);
        var text = key.Text.Length > 0 ? key.Text : key.Name.Length == 1 && !char.IsControl(key.Name[0]) ? key.Name : "";
        var modifiers = (key.Shift ? NativeEmbeddedModifiers.Shift : NativeEmbeddedModifiers.None) | (key.Ctrl ? NativeEmbeddedModifiers.Control : NativeEmbeddedModifiers.None)
            | (key.Option ? NativeEmbeddedModifiers.Alt : NativeEmbeddedModifiers.None) | (key.Super ? NativeEmbeddedModifiers.Super : NativeEmbeddedModifiers.None)
            | (key.CapsLock ? NativeEmbeddedModifiers.CapsLock : NativeEmbeddedModifiers.None) | (key.NumLock ? NativeEmbeddedModifiers.NumLock : NativeEmbeddedModifiers.None);
        // A legacy LF is Ctrl+J, not Enter/CR. Let the native codec apply the child's active key protocol.
        if (key.Name == "linefeed") { physical = "KeyJ"; text = "j"; modifiers |= NativeEmbeddedModifiers.Control; }
        SendKey(new NativeEmbeddedKey(physical, text, modifiers,
            Action: key.EventType == Keymap.KeyEventType.Release ? NativeEmbeddedKeyAction.Release : key.Repeated ? NativeEmbeddedKeyAction.Repeat : NativeEmbeddedKeyAction.Press,
            UnshiftedCodepoint: key.BaseCode is { } basis ? checked((uint)basis)
                : physical.StartsWith("Key", StringComparison.Ordinal) && physical.Length == 4 ? (uint)char.ToLowerInvariant(physical[3])
                : physical.StartsWith("Digit", StringComparison.Ordinal) && physical.Length == 6 ? (uint)physical[5] : 0u));
    }
    private static string RichPhysicalKey(TerminalKeyInput key)
    {
        if (key.IsTextOnly) return "";
        if (key.Code is { Length: > 2 } code && !code.StartsWith('[')) return code;
        if (key.TextSource == TerminalKeyTextSource.Keypad)
        {
            if (key.Text.Length == 1 && char.IsAsciiDigit(key.Text[0])) return "Numpad" + key.Text;
            return key.Text switch { "." => "NumpadDecimal", "+" => "NumpadAdd", "-" => "NumpadSubtract", "*" => "NumpadMultiply", "/" => "NumpadDivide", _ => "" };
        }
        if (key.Name.Length == 1 && char.IsAsciiLetter(key.Name[0])) return "Key" + char.ToUpperInvariant(key.Name[0]);
        if (key.Name.Length == 1 && char.IsAsciiDigit(key.Name[0])) return "Digit" + key.Name;
        if (key.Name.StartsWith('f') && int.TryParse(key.Name.AsSpan(1), System.Globalization.CultureInfo.CurrentCulture, out var function) && function is >= 1 and <= 35) return "F" + function;
        return key.Name switch
        {
            "return" => "Enter", "kpenter" => "NumpadEnter", "tab" => "Tab", "backspace" => "Backspace", "escape" => "Escape",
            "left" => "ArrowLeft", "right" => "ArrowRight", "up" => "ArrowUp", "down" => "ArrowDown",
            "home" => "Home", "end" => "End", "insert" => "Insert", "delete" => "Delete", "pageup" => "PageUp", "pagedown" => "PageDown",
            "space" => "Space", "kpplus" => "NumpadAdd", "kpminus" => "NumpadSubtract", "kpdecimal" => "NumpadDecimal",
            "kpmultiply" => "NumpadMultiply", "kpdivide" => "NumpadDivide", "kpequal" => "NumpadEqual",
            _ when key.Name.Length == 3 && key.Name.StartsWith("kp", StringComparison.Ordinal) && char.IsAsciiDigit(key.Name[2]) => "Numpad" + key.Name[2],
            _ => ""
        };
    }
    internal void SendKey(ConsoleKeyInfo key)
    {
        if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; return; }
        var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is { } high ? new string([high, key.KeyChar]) : ""
            : !char.IsControl(key.KeyChar) ? key.KeyChar.ToString()
            : key.Key is >= ConsoleKey.A and <= ConsoleKey.Z ? ((char)((key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 'A' : 'a') + key.Key - ConsoleKey.A)).ToString() : "";
        _surrogate = null;
        var physical = PhysicalKey(key.Key);
        SendKey(new NativeEmbeddedKey(physical, text, Modifiers(key.Modifiers), UnshiftedCodepoint:
            physical.StartsWith("Key", StringComparison.Ordinal) && physical.Length == 4 ? (uint)char.ToLowerInvariant(physical[3])
            : physical.StartsWith("Digit", StringComparison.Ordinal) && physical.Length == 6 ? (uint)physical[5] : 0u));
    }
    internal void Paste(string text) => Send(Terminal.EncodePaste(Encoding.UTF8.GetBytes(text)));
    internal void Focus(bool focused)
    {
        if (_disposed || Focused == focused) return;
        Focused = focused;
        if (_terminal is not null) Send(Terminal.EncodeFocus(focused));
        FocusChanged?.Invoke(focused);
        Changed?.Invoke();
    }
    internal bool Mouse(TerminalPointerInput input, int x, int y, bool dragging)
    {
        var button = input.Kind == TerminalPointerKind.Wheel
            ? input.DeltaY < 0 ? NativeEmbeddedMouseButton.Four : input.DeltaY > 0 ? NativeEmbeddedMouseButton.Five
                : input.DeltaX < 0 ? NativeEmbeddedMouseButton.Six : NativeEmbeddedMouseButton.Seven
            : input.Button switch { TerminalPointerButton.Left => NativeEmbeddedMouseButton.Left, TerminalPointerButton.Middle => NativeEmbeddedMouseButton.Middle,
                TerminalPointerButton.Right => NativeEmbeddedMouseButton.Right, _ => NativeEmbeddedMouseButton.None };
        var action = input.Kind == TerminalPointerKind.Up ? NativeEmbeddedMouseAction.Release
            : input.Kind == TerminalPointerKind.Move ? NativeEmbeddedMouseAction.Motion : NativeEmbeddedMouseAction.Press;
        var output = Terminal.EncodeMouse(action, button, Modifiers(input.Modifiers), x, y, dragging || input.Kind == TerminalPointerKind.Down);
        if (output.Length > 0) { Send(output); return true; }
        if (input.Kind != TerminalPointerKind.Wheel || input.DeltaY == 0) return false;
        Terminal.Scroll(input.DeltaY * 3);
        Changed?.Invoke();
        return true;
    }
    public void Scroll(int rows) { Terminal.Scroll(rows); Changed?.Invoke(); }
    public void Select(int x1, int y1, int x2, int y2) { Terminal.Select(x1, y1, x2, y2); Changed?.Invoke(); }
    public void ClearSelection() { Terminal.ClearSelection(); Changed?.Invoke(); }
    public string SelectedText() => Terminal.SelectedText();
    private void FlushResponses()
    {
        var bytes = Terminal.DrainResponses();
        if (bytes.Length > 0) Data?.Invoke(new(bytes, EmbeddedTerminalDataSource.Response));
    }
    private void Send(byte[] bytes) { if (bytes.Length > 0) Data?.Invoke(new(bytes, EmbeddedTerminalDataSource.Input)); }
    private static NativeEmbeddedModifiers Modifiers(ConsoleModifiers modifiers) =>
        (modifiers.HasFlag(ConsoleModifiers.Shift) ? NativeEmbeddedModifiers.Shift : NativeEmbeddedModifiers.None) |
        (modifiers.HasFlag(ConsoleModifiers.Control) ? NativeEmbeddedModifiers.Control : NativeEmbeddedModifiers.None) |
        (modifiers.HasFlag(ConsoleModifiers.Alt) ? NativeEmbeddedModifiers.Alt : NativeEmbeddedModifiers.None);
    private static string PhysicalKey(ConsoleKey key) => key switch
    {
        >= ConsoleKey.A and <= ConsoleKey.Z => "Key" + key,
        >= ConsoleKey.D0 and <= ConsoleKey.D9 => "Digit" + (key - ConsoleKey.D0),
        >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad9 => "Numpad" + (key - ConsoleKey.NumPad0),
        >= ConsoleKey.F1 and <= ConsoleKey.F24 => key.ToString(),
        ConsoleKey.Decimal => "NumpadDecimal", ConsoleKey.Add => "NumpadAdd", ConsoleKey.Subtract => "NumpadSubtract",
        ConsoleKey.Multiply => "NumpadMultiply", ConsoleKey.Divide => "NumpadDivide", ConsoleKey.Separator => "NumpadComma",
        ConsoleKey.LeftArrow => "ArrowLeft", ConsoleKey.RightArrow => "ArrowRight", ConsoleKey.UpArrow => "ArrowUp", ConsoleKey.DownArrow => "ArrowDown",
        ConsoleKey.Spacebar => "Space", ConsoleKey.OemMinus => "Minus", ConsoleKey.OemPlus => "Equal", ConsoleKey.OemComma => "Comma",
        ConsoleKey.OemPeriod => "Period", ConsoleKey.Oem1 => "Semicolon", ConsoleKey.Oem2 => "Slash", ConsoleKey.Oem3 => "Backquote",
        ConsoleKey.Oem4 => "BracketLeft", ConsoleKey.Oem5 => "Backslash", ConsoleKey.Oem6 => "BracketRight", ConsoleKey.Oem7 => "Quote",
        ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.Backspace or ConsoleKey.Delete or ConsoleKey.Insert or ConsoleKey.Home or ConsoleKey.End
            or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Escape => key.ToString(),
        _ => ""
    };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready.TrySetCanceled();
        _terminal?.Dispose();
        _terminal = null;
    }
}
