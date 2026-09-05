namespace OpenTui.Blazor;

using System.Globalization;
using System.Text;

/// <summary>UTF-16 editing positions aligned to text-element boundaries.</summary>
public static class TerminalTextEditing
{
    public const int MaximumPasteLength = 1024 * 1024;

    public static string NormalizePaste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumPasteLength)
            throw new ArgumentException($"Paste exceeds {MaximumPasteLength:N0} UTF-16 code units; nothing was inserted.", nameof(text));
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                result.Append('\n');
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                result.Append(character).Append(text[++index]);
            }
            else if (char.IsSurrogate(character)) result.Append('\uFFFD');
            else if (character is '\n' or '\t' || !char.IsControl(character)) result.Append(character);
        }
        return result.ToString();
    }

    public static int WordBoundary(string text, int cursor, int direction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cursor, text.Length);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var starts = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        var position = Array.BinarySearch(starts, cursor);
        if (position < 0) position = ~position - 1;
        if (direction < 0)
        {
            while (position > 0 && Kind(text, starts[position - 1]) == 0) position--;
            if (position == 0) return 0;
            var kind = Kind(text, starts[position - 1]);
            while (position > 0 && Kind(text, starts[position - 1]) == kind) position--;
        }
        else
        {
            if (position >= starts.Length - 1) return text.Length;
            var kind = Kind(text, starts[position]);
            while (position < starts.Length - 1 && Kind(text, starts[position]) == kind) position++;
            while (position < starts.Length - 1 && Kind(text, starts[position]) == 0) position++;
        }
        return starts[position];
    }

    public static int LineStart(string text, int cursor) => cursor == 0 ? 0 : text.LastIndexOf('\n', cursor - 1) + 1;
    public static int LineEnd(string text, int cursor)
    {
        var end = text.IndexOf('\n', cursor);
        return end < 0 ? text.Length : end;
    }

    private static int Kind(string text, int index)
    {
        var rune = Rune.GetRuneAt(text, index);
        if (Rune.IsWhiteSpace(rune)) return 0;
        return Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ? 1 : 2;
    }
}
