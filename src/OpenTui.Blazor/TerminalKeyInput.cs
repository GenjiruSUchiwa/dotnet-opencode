namespace OpenTui.Blazor;

using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using OpenTui.Blazor.Keymap;

[Flags]
public enum TerminalKeyModifiers
{
    None = 0, Shift = 1, Alt = 2, Control = 4, Super = 8, Hyper = 16,
    KittyMeta = 32, CapsLock = 64, NumLock = 128
}

public enum TerminalKeySource { Raw, Kitty, Console }
public enum TerminalKeyProtocol { Utf8, Escape, Vt, ModifyOtherKeys, KittyCsiU, KittyFunctional, Console }
public enum TerminalKeyTextSource { None, Raw, Associated, ShiftedCodepoint, ShiftCasing, Keypad, Codepoint }

[Flags]
public enum TerminalKeyProjectionLoss
{
    None = 0, Repeat = 1, ExtendedModifiers = 2, LockState = 4, AlternateCodepoints = 8,
    AssociatedText = 16, Utf16Split = 32, KeyIdentity = 64, TextOnly = 128, LinefeedAlias = 256
}

/// <summary>Explicit legacy-behavior projection, not a replacement for the source record.
/// A release has no legacy press. Loss flags identify enhanced behavior/identity that
/// ConsoleKeyInfo cannot retain; raw protocol provenance stays on TerminalKeyInput.</summary>
public sealed record TerminalConsoleKeyProjection(IReadOnlyList<ConsoleKeyInfo> Keys,
    TerminalKeyProjectionLoss Loss, bool SuppressedRelease)
{
    public bool IsExact => !SuppressedRelease && Loss == TerminalKeyProjectionLoss.None;
}

/// <summary>One complete input key, retaining source ParsedKey fields and the original
/// Kitty subfields. Base layout codepoints are reported Unicode, never scan codes.</summary>
public sealed record TerminalKeyInput(string Name, string Sequence, string Raw,
    TerminalKeySource Source, TerminalKeyProtocol Protocol)
{
    public TerminalKeyModifiers Modifiers { get; init; }
    public KeyEventType EventType { get; init; } = KeyEventType.Press;
    public bool Repeated { get; init; }
    public bool Number { get; init; }
    public string? Code { get; init; }
    public int? Codepoint { get; init; }
    public int? ShiftedCodepoint { get; init; }
    public int? BaseLayoutCodepoint { get; init; }
    /// <summary>Source baseCode matching field; populated only for reported printable-key alternatives.</summary>
    public int? BaseCode { get; init; }
    public int? WireKeyCode { get; init; }
    public int? WireModifiers { get; init; }
    public int? WireEventType { get; init; }
    public bool HasAssociatedText { get; init; }
    public IReadOnlyList<int> AssociatedCodepoints { get; init; } = Array.Empty<int>();
    public string? AssociatedText { get; init; }
    public string Text { get; init; } = "";
    public TerminalKeyTextSource TextSource { get; init; }
    public bool AltPrefix { get; init; }
    public ConsoleKeyInfo? ConsoleKey { get; init; }
    public bool RawSequenceAvailable => Source != TerminalKeySource.Console;
    public bool Ctrl => Modifiers.HasFlag(TerminalKeyModifiers.Control);
    public bool Shift => Modifiers.HasFlag(TerminalKeyModifiers.Shift);
    public bool Option => Modifiers.HasFlag(TerminalKeyModifiers.Alt);
    public bool Meta => Option || Modifiers.HasFlag(TerminalKeyModifiers.KittyMeta);
    public bool Super => Modifiers.HasFlag(TerminalKeyModifiers.Super);
    public bool Hyper => Modifiers.HasFlag(TerminalKeyModifiers.Hyper);
    public bool CapsLock => Modifiers.HasFlag(TerminalKeyModifiers.CapsLock);
    public bool NumLock => Modifiers.HasFlag(TerminalKeyModifiers.NumLock);
    public bool HasReportedLockState => Source == TerminalKeySource.Kitty;
    public bool IsTextOnly => Codepoint == 0 && Text.Length > 0;

    /// <summary>Source keymap matching retains press/release, repeat, Super/Hyper and baseCode.
    /// Lock state and associated text remain on this record, not in shortcut identity.
    /// Text-only commits must be delivered as text, never fabricated physical key strokes.</summary>
    public bool TryGetKeymapEvent([NotNullWhen(true)] out KeymapEvent? input)
    {
        input = null;
        if (IsTextOnly || string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsControl)) return false;
        input = new(new KeyStroke(Name, Ctrl, Shift, Meta, Super, Hyper), EventType, BaseCode, Repeated);
        return true;
    }

    public TerminalConsoleKeyProjection ProjectConsoleKeys()
    {
        if (EventType == KeyEventType.Release) return new(Array.Empty<ConsoleKeyInfo>(), TerminalKeyProjectionLoss.None, true);
        var loss = Repeated ? TerminalKeyProjectionLoss.Repeat : TerminalKeyProjectionLoss.None;
        if (Super || Hyper || Modifiers.HasFlag(TerminalKeyModifiers.KittyMeta)) loss |= TerminalKeyProjectionLoss.ExtendedModifiers;
        if (CapsLock || NumLock) loss |= TerminalKeyProjectionLoss.LockState;
        if (ShiftedCodepoint is not null || BaseLayoutCodepoint is not null) loss |= TerminalKeyProjectionLoss.AlternateCodepoints;
        if (HasAssociatedText) loss |= TerminalKeyProjectionLoss.AssociatedText;
        if (IsTextOnly) return new(Array.Empty<ConsoleKeyInfo>(), loss | TerminalKeyProjectionLoss.TextOnly, false);
        if (ConsoleKey is { } original) return new(Array.AsReadOnly(new[] { original }), loss, false);

        var name = Name.ToLowerInvariant();
        var code = NamedConsoleKey(name);
        var text = Text;
        if (name == "linefeed")
        {
            loss |= TerminalKeyProjectionLoss.LinefeedAlias;
            return new(Array.AsReadOnly(new[] { new ConsoleKeyInfo('\n', System.ConsoleKey.J, Shift, Meta, true) }), loss, false);
        }
        if (code is null)
        {
            // Printable text is an identity, not evidence of a physical/OEM key position.
            if (!Rune.TryGetRuneAt(Name, 0, out var rune) || rune.Utf16SequenceLength != Name.Length || Rune.IsControl(rune))
                return new(Array.Empty<ConsoleKeyInfo>(), loss | TerminalKeyProjectionLoss.KeyIdentity, false);
            code = rune.Value switch
            {
                >= 'a' and <= 'z' => System.ConsoleKey.A + rune.Value - 'a',
                >= 'A' and <= 'Z' => System.ConsoleKey.A + rune.Value - 'A',
                >= '0' and <= '9' => System.ConsoleKey.D0 + rune.Value - '0',
                _ => (ConsoleKey)0
            };
            if (text.Length == 0) text = Name;
        }
        if (text.Length == 0) text = name switch
        {
            "return" or "kpenter" => "\r", "tab" => "\t", "escape" => "\x1b", "backspace" => "\b", "space" => " ", _ => "\0"
        };
        if (text.Length > 1)
        {
            if (!Rune.TryGetRuneAt(text, 0, out var rune) || rune.Utf16SequenceLength != text.Length)
                return new(Array.Empty<ConsoleKeyInfo>(), loss | TerminalKeyProjectionLoss.AssociatedText, false);
            loss |= TerminalKeyProjectionLoss.Utf16Split;
        }
        return new(Array.AsReadOnly(text.Select(character => new ConsoleKeyInfo(character, code.Value, Shift, Meta, Ctrl)).ToArray()), loss, false);
    }

    internal TerminalKeyInput WithAltPrefix() => this with
    {
        Modifiers = Modifiers | TerminalKeyModifiers.Alt, AltPrefix = true,
        Raw = "\x1b" + Raw, Sequence = Sequence == Raw ? "\x1b" + Sequence : Sequence
    };

    internal static TerminalKeyModifiers FromConsoleModifiers(ConsoleModifiers modifiers) =>
        (modifiers.HasFlag(ConsoleModifiers.Shift) ? TerminalKeyModifiers.Shift : TerminalKeyModifiers.None)
        | (modifiers.HasFlag(ConsoleModifiers.Alt) ? TerminalKeyModifiers.Alt : TerminalKeyModifiers.None)
        | (modifiers.HasFlag(ConsoleModifiers.Control) ? TerminalKeyModifiers.Control : TerminalKeyModifiers.None);

    internal static TerminalKeyInput FromConsole(ConsoleKeyInfo input) => new(ConsoleName(input),
        input.KeyChar == '\0' ? "" : input.KeyChar.ToString(), "", TerminalKeySource.Console, TerminalKeyProtocol.Console)
    {
        Modifiers = FromConsoleModifiers(input.Modifiers), ConsoleKey = input,
        Text = char.IsControl(input.KeyChar) ? "" : input.KeyChar.ToString(),
        TextSource = char.IsControl(input.KeyChar) ? TerminalKeyTextSource.None : TerminalKeyTextSource.Raw,
        Number = input.KeyChar is >= '0' and <= '9'
    };

    internal static TerminalKeyInput FromLegacy(ConsoleKeyInfo input, string raw, string? code = null)
    {
        var text = char.IsControl(input.KeyChar) ? "" : input.KeyChar.ToString();
        var keypad = code is not null && code.Length == 2 && code[0] == 'O' && text.Length > 0;
        return new(keypad ? text : ConsoleName(input), keypad ? text : raw, raw, TerminalKeySource.Raw, TerminalKeyProtocol.Vt)
        {
            Code = code, Modifiers = FromConsoleModifiers(input.Modifiers), Text = text,
            TextSource = keypad ? TerminalKeyTextSource.Keypad : TerminalKeyTextSource.None,
            Number = keypad && input.KeyChar is >= '0' and <= '9'
        };
    }

    internal static TerminalKeyInput FromRaw(Rune rune, bool alt = false, bool extraEscape = false)
    {
        var text = rune.ToString();
        var code = rune.Value;
        var name = code switch
        {
            0 or 32 => "space", 8 or 127 => "backspace", 9 => "tab", 10 => "linefeed", 13 => "return", 27 => "escape",
            >= 1 and <= 26 => ((char)('a' + code - 1)).ToString(),
            >= 28 and <= 31 => ((char)(code + 64)).ToString(),
            >= 'A' and <= 'Z' => text.ToLowerInvariant(), _ => text
        };
        var ctrl = code is 0 or >= 1 and <= 26 or >= 28 and <= 31 && code is not (8 or 9 or 10 or 13);
        var raw = (alt ? extraEscape ? "\x1b\x1b" : "\x1b" : "") + text;
        return new(name, raw, raw, TerminalKeySource.Raw, alt ? TerminalKeyProtocol.Escape : TerminalKeyProtocol.Utf8)
        {
            Codepoint = code, AltPrefix = alt, Number = code is >= '0' and <= '9',
            Modifiers = (ctrl ? TerminalKeyModifiers.Control : TerminalKeyModifiers.None) | (alt ? TerminalKeyModifiers.Alt : TerminalKeyModifiers.None),
            Text = Rune.IsControl(rune) ? "" : text,
            TextSource = Rune.IsControl(rune) ? TerminalKeyTextSource.None : TerminalKeyTextSource.Raw
        };
    }

    internal static string ConsoleName(ConsoleKeyInfo key) => key.Key switch
    {
        System.ConsoleKey.Enter => "return", System.ConsoleKey.Escape => "escape", System.ConsoleKey.Tab => "tab",
        System.ConsoleKey.Spacebar => "space", System.ConsoleKey.Backspace => "backspace", System.ConsoleKey.Clear => "clear",
        System.ConsoleKey.LeftArrow => "left", System.ConsoleKey.RightArrow => "right", System.ConsoleKey.UpArrow => "up", System.ConsoleKey.DownArrow => "down",
        System.ConsoleKey.PageUp => "pageup", System.ConsoleKey.PageDown => "pagedown", System.ConsoleKey.Home => "home", System.ConsoleKey.End => "end",
        System.ConsoleKey.Insert => "insert", System.ConsoleKey.Delete => "delete", System.ConsoleKey.Applications => "menu",
        >= System.ConsoleKey.A and <= System.ConsoleKey.Z => ((char)('a' + key.Key - System.ConsoleKey.A)).ToString(),
        >= System.ConsoleKey.D0 and <= System.ConsoleKey.D9 => ((char)('0' + key.Key - System.ConsoleKey.D0)).ToString(),
        >= System.ConsoleKey.NumPad0 and <= System.ConsoleKey.NumPad9 => "kp" + ((int)key.Key - (int)System.ConsoleKey.NumPad0).ToString(CultureInfo.InvariantCulture),
        >= System.ConsoleKey.F1 and <= System.ConsoleKey.F24 => key.Key.ToString().ToLowerInvariant(),
        _ => key.KeyChar != '\0' ? key.KeyChar.ToString() : key.Key.ToString().ToLowerInvariant()
    };

    private static ConsoleKey? NamedConsoleKey(string name)
    {
        if (name.StartsWith('f') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function)
            && function is >= 1 and <= 24) return System.ConsoleKey.F1 + function - 1;
        if (name.Length == 3 && name.StartsWith("kp", StringComparison.Ordinal) && name[2] is >= '0' and <= '9')
            return System.ConsoleKey.NumPad0 + name[2] - '0';
        return name switch
        {
            "return" => System.ConsoleKey.Enter, "tab" => System.ConsoleKey.Tab, "escape" => System.ConsoleKey.Escape,
            "space" => System.ConsoleKey.Spacebar, "backspace" => System.ConsoleKey.Backspace, "clear" => System.ConsoleKey.Clear,
            "left" => System.ConsoleKey.LeftArrow, "right" => System.ConsoleKey.RightArrow, "up" => System.ConsoleKey.UpArrow, "down" => System.ConsoleKey.DownArrow,
            "home" => System.ConsoleKey.Home, "end" => System.ConsoleKey.End, "pageup" => System.ConsoleKey.PageUp, "pagedown" => System.ConsoleKey.PageDown,
            "insert" => System.ConsoleKey.Insert, "delete" => System.ConsoleKey.Delete, "menu" => System.ConsoleKey.Applications,
            "printscreen" => System.ConsoleKey.PrintScreen, "pause" => System.ConsoleKey.Pause,
            "kpdecimal" => System.ConsoleKey.Decimal, "kpdivide" => System.ConsoleKey.Divide,
            "kpmultiply" => System.ConsoleKey.Multiply, "kpminus" => System.ConsoleKey.Subtract,
            "kpplus" => System.ConsoleKey.Add, "kpseparator" => System.ConsoleKey.Separator,
            _ => null // Do not turn distinct keypad navigation, modifiers, or media keys into lookalikes.
        };
    }
}
