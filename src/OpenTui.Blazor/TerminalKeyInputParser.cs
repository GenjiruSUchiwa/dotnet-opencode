namespace OpenTui.Blazor;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using OpenTui.Blazor.Keymap;

/// <summary>Decodes complete framed key sequences only. TerminalInput remains the sole
/// VT/paste/mouse/response framer. Source: installed OpenTUI 0.5.9 parse.keypress-kitty.ts.</summary>
public static class TerminalKeyInputParser
{
    public static IReadOnlyDictionary<int, string> KittyFunctionalKeys { get; } = FunctionalKeys();

    /// <returns>False with no error means another protocol. False with error means a
    /// recognized key protocol was malformed/unsupported and must not be reinterpreted.</returns>
    public static bool TryParse(ReadOnlySpan<char> sequence, [NotNullWhen(true)] out TerminalKeyInput? input, out string? error)
    {
        input = null; error = null;
        if (sequence.Length < 3 || !sequence.StartsWith("\x1b[")) return false;
        var body = sequence[2..^1];
        if (body.StartsWith("?")) return false; // Kitty capability replies are not keys.
        var kitty = sequence[^1] == 'u';
        var modifyOtherKeys = sequence[^1] == '~' && body.StartsWith("27;");
        var special = body.Contains(':') && (sequence[^1] is >= 'A' and <= 'Z' || sequence[^1] == '~');
        if (!kitty && !modifyOtherKeys && !special) return false;
        if (sequence.Length > 4096) { error = "Keyboard protocol frame exceeds the shared 4096-character limit."; return false; }
        try
        {
            var raw = sequence.ToString();
            var fields = body.ToString().Split(';');
            input = kitty ? ParseCsiU(raw, fields) : modifyOtherKeys ? ParseModifyOtherKeys(raw, fields)
                : ParseSpecial(raw, fields, sequence[^1]);
            return true;
        }
        catch (FormatException exception) { error = exception.Message; return false; }
    }

    private static TerminalKeyInput ParseCsiU(string raw, string[] fields)
    {
        if (fields.Length is < 1 or > 3) throw Invalid("Unsupported Kitty field count.");
        var identity = fields[0].Split(':');
        if (identity.Length is < 1 or > 3) throw Invalid("Unsupported Kitty alternate-key fields.");
        var codepoint = Scalar(identity[0], allowZero: true);
        var shifted = identity.Length > 1 && identity[1].Length > 0 ? Scalar(identity[1], allowZero: true) : (int?)null;
        var basis = identity.Length > 2 && identity[2].Length > 0 ? Scalar(identity[2], allowZero: true) : (int?)null;
        var modifiers = ReadModifiers(fields.Length > 1 ? fields[1] : "");
        var known = KittyFunctionalKeys.TryGetValue(codepoint, out var keyName);
        if (!known && (codepoint is >= 57344 and <= 63743 || codepoint is > 0 and < 32))
            throw Invalid("Unmapped Kitty functional/control key code.");
        var name = known ? keyName! : codepoint == 0 ? "" : codepoint == 32 ? "space" : new Rune(codepoint).ToString();
        var associated = new List<int>();
        if (fields.Length > 2 && fields[2].Length > 0)
            foreach (var item in fields[2].Split(':')) associated.Add(Scalar(item, allowZero: true));
        // Retain every supplied scalar (including zero) while reproducing the source's
        // positive-codepoint text construction. Raw preserves empty subfield spelling.
        var associatedText = fields.Length > 2 ? string.Concat(associated.Where(value => value > 0).Select(value => new Rune(value).ToString())) : null;
        var text = associatedText ?? "";
        var textSource = text.Length > 0 ? TerminalKeyTextSource.Associated : TerminalKeyTextSource.None;
        if (text.Length == 0 && KeypadText(name) is { } keypad)
        {
            text = keypad; textSource = TerminalKeyTextSource.Keypad;
        }
        if (text.Length == 0 && !known && codepoint > 0)
        {
            if (codepoint == 32) { text = " "; textSource = TerminalKeyTextSource.Codepoint; }
            else if (modifiers.Flags.HasFlag(TerminalKeyModifiers.Shift) && shifted is > 0)
            { text = new Rune(shifted.Value).ToString(); textSource = TerminalKeyTextSource.ShiftedCodepoint; }
            else if (modifiers.Flags.HasFlag(TerminalKeyModifiers.Shift) && name.Length == 1)
            { text = name.ToUpper(CultureInfo.CurrentCulture); textSource = TerminalKeyTextSource.ShiftCasing; }
            else { text = name; textSource = TerminalKeyTextSource.Codepoint; }
        }
        if (codepoint == 0)
        {
            if (text.Length == 0) throw Invalid("Kitty text-only input requires associated text.");
            name = text;
        }
        return new(name, text.Length > 0 ? text : raw, raw, TerminalKeySource.Kitty, TerminalKeyProtocol.KittyCsiU)
        {
            Modifiers = modifiers.Flags, EventType = modifiers.Event == 3 ? KeyEventType.Release : KeyEventType.Press,
            Repeated = modifiers.Event == 2, WireModifiers = modifiers.Wire, WireEventType = modifiers.Event,
            Codepoint = codepoint, WireKeyCode = codepoint, ShiftedCodepoint = shifted, BaseLayoutCodepoint = basis,
            BaseCode = !known && codepoint > 0 && basis is > 0 ? basis : null,
            Code = known ? $"[{codepoint}u" : null,
            HasAssociatedText = fields.Length > 2, AssociatedCodepoints = associated.AsReadOnly(), AssociatedText = associatedText,
            Text = text, TextSource = textSource
            // Source Kitty ParsedKey leaves number=false, including digit key identities.
        };
    }

    private static TerminalKeyInput ParseSpecial(string raw, string[] fields, char terminator)
    {
        if (fields.Length != 2 || fields[1].Split(':').Length != 2 || fields[1].StartsWith(':') || fields[1].EndsWith(':'))
            throw Invalid("Malformed Kitty functional-key event fields.");
        var number = Number(fields[0], 1, 0x10ffff);
        var modifiers = ReadModifiers(fields[1]);
        var name = terminator == '~' ? TildeName(number) : number == 1 ? FunctionalName(terminator) : null;
        if (name is null) throw Invalid("Unmapped Kitty functional-key sequence.");
        return new(name, raw, raw, TerminalKeySource.Kitty, TerminalKeyProtocol.KittyFunctional)
        {
            Modifiers = modifiers.Flags, EventType = modifiers.Event == 3 ? KeyEventType.Release : KeyEventType.Press,
            Repeated = modifiers.Event == 2, WireModifiers = modifiers.Wire, WireEventType = modifiers.Event,
            WireKeyCode = number
        };
    }

    private static TerminalKeyInput ParseModifyOtherKeys(string raw, string[] fields)
    {
        if (fields.Length != 3 || fields[0] != "27") throw Invalid("Malformed modifyOtherKeys fields.");
        var modifier = Number(fields[1], 1, 32); // source raw protocol exposes Shift/Alt/Ctrl/Super/Hyper.
        var codepoint = Scalar(fields[2], allowZero: false);
        var name = codepoint switch
        {
            13 => "return", 27 => "escape", 9 => "tab", 32 => "space", 8 or 127 => "backspace",
            _ when codepoint < 32 || codepoint is >= 57344 and <= 63743 => throw Invalid("Unmapped modifyOtherKeys control/functional code."),
            _ => new Rune(codepoint).ToString()
        };
        var special = codepoint is 8 or 9 or 13 or 27 or 32 or 127;
        var text = special ? codepoint == 32 ? " " : "" : new Rune(codepoint).ToString();
        return new(name, special ? raw : text, raw, TerminalKeySource.Raw, TerminalKeyProtocol.ModifyOtherKeys)
        {
            Modifiers = (TerminalKeyModifiers)(modifier - 1), WireModifiers = modifier, WireKeyCode = codepoint, Codepoint = codepoint,
            Text = text, TextSource = text.Length > 0 ? TerminalKeyTextSource.Codepoint : TerminalKeyTextSource.None,
            Number = codepoint is >= '0' and <= '9'
        };
    }

    private static (TerminalKeyModifiers Flags, int? Wire, int? Event) ReadModifiers(string value)
    {
        var fields = value.Split(':');
        if (fields.Length > 2) throw Invalid("Unsupported Kitty modifier subfields.");
        var wire = fields[0].Length > 0 ? Number(fields[0], 1, 256) : (int?)null;
        var eventType = fields.Length > 1 && fields[1].Length > 0 ? Number(fields[1], 1, 3) : (int?)null;
        return ((TerminalKeyModifiers)((wire ?? 1) - 1), wire, eventType);
    }

    private static int Scalar(string value, bool allowZero)
    {
        var codepoint = Number(value, allowZero ? 0 : 1, 0x10ffff);
        if (!Rune.IsValid(codepoint)) throw Invalid("Keyboard protocol contains a non-scalar Unicode value.");
        return codepoint;
    }
    private static int Number(string value, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum
            ? number : throw Invalid("Malformed or unsupported numeric keyboard protocol field.");
    private static FormatException Invalid(string message) => new(message);

    internal static string? FunctionalName(char suffix) => suffix switch
    {
        'A' => "up", 'B' => "down", 'C' => "right", 'D' => "left", 'H' => "home", 'F' => "end", 'E' => "clear",
        'P' => "f1", 'Q' => "f2", 'S' => "f4", _ => null // Source excludes R (CPR/F3 ambiguity).
    };
    internal static string? TildeName(int number) => number switch
    {
        1 or 7 => "home", 2 => "insert", 3 => "delete", 4 or 8 => "end", 5 => "pageup", 6 => "pagedown",
        11 => "f1", 12 => "f2", 13 => "f3", 14 => "f4", 15 => "f5", 17 => "f6", 18 => "f7", 19 => "f8",
        20 => "f9", 21 => "f10", 23 => "f11", 24 => "f12", 29 => "menu", 57427 => "clear", _ => null
    };
    private static string? KeypadText(string name) => name switch
    {
        "kp0" => "0", "kp1" => "1", "kp2" => "2", "kp3" => "3", "kp4" => "4", "kp5" => "5", "kp6" => "6", "kp7" => "7", "kp8" => "8", "kp9" => "9",
        "kpdecimal" => ".", "kpdivide" => "/", "kpmultiply" => "*", "kpminus" => "-", "kpplus" => "+", "kpequal" => "=", "kpseparator" => ",", _ => null
    };

    private static IReadOnlyDictionary<int, string> FunctionalKeys()
    {
        var result = new Dictionary<int, string> { [27] = "escape", [9] = "tab", [13] = "return", [127] = "backspace" };
        string[] basic = ["escape", "return", "tab", "backspace", "insert", "delete", "left", "right", "up", "down", "pageup", "pagedown", "home", "end", "capslock", "scrolllock", "numlock", "printscreen", "pause", "menu"];
        for (var index = 0; index < basic.Length; index++) result[57344 + index] = basic[index];
        for (var index = 0; index < 35; index++) result[57364 + index] = "f" + (index + 1).ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < 10; index++) result[57399 + index] = "kp" + index.ToString(CultureInfo.InvariantCulture);
        string[] extended = ["kpdecimal", "kpdivide", "kpmultiply", "kpminus", "kpplus", "kpenter", "kpequal", "kpseparator",
            "kpleft", "kpright", "kpup", "kpdown", "kppageup", "kppagedown", "kphome", "kpend", "kpinsert", "kpdelete", "clear",
            "mediaplay", "mediapause", "mediaplaypause", "mediareverse", "mediastop", "mediafastforward", "mediarewind", "medianext", "mediaprev", "mediarecord",
            "volumedown", "volumeup", "mute", "leftshift", "leftctrl", "leftalt", "leftsuper", "lefthyper", "leftmeta",
            "rightshift", "rightctrl", "rightalt", "rightsuper", "righthyper", "rightmeta", "iso_level3_shift", "iso_level5_shift"];
        for (var index = 0; index < extended.Length; index++) result[57409 + index] = extended[index];
        return new ReadOnlyDictionary<int, string>(result);
    }
}
