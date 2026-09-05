namespace OpenTui.Blazor;

using System.Globalization;

internal sealed partial class TerminalInput
{
    // Xterm ctlseqs: SGR 1006, legacy X10/1005, and decimal urxvt 1015.
    // Pixel mode 1016 is deliberately not negotiated: layout uses terminal cells.
    private static bool TryPointer(ReadOnlySpan<char> sequence, out TerminalPointerInput input)
    {
        input = default;
        if (!sequence.StartsWith("\x1b[")) return false;
        int code, x, y;
        var sgr = sequence.Length > 3 && sequence[2] == '<';
        if (sequence.Length == 6 && sequence[2] == 'M')
        {
            code = sequence[3] - 32;
            x = sequence[4] - 32;
            y = sequence[5] - 32;
        }
        else
        {
            if (sequence[^1] is not ('M' or 'm') || !sgr && sequence[^1] != 'M') return false;
            var fields = sequence[(sgr ? 3 : 2)..^1];
            var first = fields.IndexOf(';');
            if (first < 1) return false;
            var rest = fields[(first + 1)..];
            var second = rest.IndexOf(';');
            if (second < 1 || !int.TryParse(fields[..first], NumberStyles.None, CultureInfo.InvariantCulture, out code)
                || !int.TryParse(rest[..second], NumberStyles.None, CultureInfo.InvariantCulture, out x)
                || !int.TryParse(rest[(second + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out y)) return false;
            if (!sgr) code -= 32;
        }
        if (code is < 0 or > 255 || x <= 0 || y <= 0) return false;
        var modifiers = ((code & 4) != 0 ? ConsoleModifiers.Shift : ConsoleModifiers.None)
            | ((code & 8) != 0 ? ConsoleModifiers.Alt : ConsoleModifiers.None) | ((code & 16) != 0 ? ConsoleModifiers.Control : ConsoleModifiers.None);
        var button = code & 3;
        if ((code & 64) != 0)
        {
            if ((code & 128) != 0 || sgr && sequence[^1] == 'm') return false;
            input = new(TerminalPointerKind.Wheel, x - 1, y - 1, Modifiers: modifiers,
                DeltaX: button >= 2 ? button == 2 ? -1 : 1 : 0, DeltaY: button < 2 ? button == 0 ? -1 : 1 : 0);
            return true;
        }
        var released = sgr ? sequence[^1] == 'm' : button == 3 && (code & 32) == 0;
        input = new(released ? TerminalPointerKind.Up : (code & 32) != 0 ? TerminalPointerKind.Move : TerminalPointerKind.Down,
            x - 1, y - 1, button == 3 ? TerminalPointerButton.None : (TerminalPointerButton)(button + ((code & 128) != 0 ? 8 : 0)), modifiers);
        return true;
    }
}
