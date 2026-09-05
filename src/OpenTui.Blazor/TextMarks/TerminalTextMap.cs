namespace OpenTui.Blazor.TextMarks;

using System.Globalization;
using System.Text;

/// <summary>Explicit conversion boundary. The caller supplies the live native display width
/// of a text element; this class never guesses Unicode width or invokes native functions.</summary>
public sealed class TerminalTextMap
{
    public readonly record struct Boundary(int Utf16, int Display, int Codepoints, int Newlines);
    private readonly Boundary[] _boundaries;
    public int Utf16Length => _boundaries[^1].Utf16;
    public int DisplayLength => _boundaries[^1].Display;

    public TerminalTextMap(string text, Func<string, int> width)
    {
        var boundaries = new List<Boundary> { new(0, 0, 0, 0) };
        var display = 0;
        var codepoints = 0;
        var newlines = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            // StringInfo groups CRLF into one text element. It still contributes
            // one newline coordinate before HighlightOffset subtracts newlines.
            var size = element is "\n" or "\r\n" ? 1 : width(element);
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(width), "Display widths must be nonnegative.");
            display = checked(display + size);
            codepoints += element.EnumerateRunes().Count();
            newlines += element.Count(character => character == '\n');
            boundaries.Add(new(elements.ElementIndex + element.Length, display, codepoints, newlines));
        }
        _boundaries = boundaries.ToArray();
    }

    public int DisplayAtUtf16(int index) => AtUtf16(index).Display;
    public int CodepointsAtUtf16(int index) => AtUtf16(index).Codepoints;
    // Mirrors offsetExcludingNewlines in the installed managed controller. Its
    // result is still display-based; it must not be mislabeled as a codepoint index.
    public int HighlightOffsetAtUtf16(int index)
    {
        var boundary = AtUtf16(index);
        return boundary.Display - boundary.Newlines;
    }

    public int Utf16AtDisplay(int offset, bool roundUp = false)
    {
        offset = Math.Clamp(offset, 0, DisplayLength);
        if (roundUp) return _boundaries.First(boundary => boundary.Display >= offset).Utf16;
        return _boundaries.Last(boundary => boundary.Display <= offset).Utf16;
    }

    public int Utf16AtCodepoints(int offset, bool roundUp = false)
    {
        offset = Math.Clamp(offset, 0, _boundaries[^1].Codepoints);
        return roundUp ? _boundaries.First(boundary => boundary.Codepoints >= offset).Utf16
            : _boundaries.Last(boundary => boundary.Codepoints <= offset).Utf16;
    }

    public TerminalTextMark Project(TerminalExtmark mark, TerminalTextMarkStyle style) => new(mark.Id,
        Utf16AtDisplay(mark.Start), Utf16AtDisplay(mark.End, roundUp: true), style, mark.Priority);

    private Boundary AtUtf16(int index)
    {
        if (index < 0 || index > Utf16Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _boundaries.FirstOrDefault(boundary => boundary.Utf16 == index) is var value && value.Utf16 == index
            ? value : throw new ArgumentException("The UTF-16 index is inside a text element.", nameof(index));
    }
}
