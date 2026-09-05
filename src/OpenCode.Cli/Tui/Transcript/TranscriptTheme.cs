namespace OpenCode.Cli.Tui.Transcript;

using OpenTui.Native;
using System.Globalization;

// Supply resolved semantic colors from the host theme, including its elevated surface.
public sealed record TranscriptTheme(
    string Text, string Subdued, string Background, string ElevatedBackground,
    string RaisedBackground, string Border, string Agent,
    string Warning, string Error, string Success,
    string MarkdownText, string MarkdownHeading, string MarkdownCode,
    string MarkdownLink, string MarkdownQuote, string MarkdownList,
    string? DiffAdded = null, string? DiffRemoved = null, string? DiffHunk = null,
    string? ActionText = null);

internal static class TranscriptColor
{
    public static NativeRgba Parse(string hex)
    {
        var value = hex.AsSpan().TrimStart('#');
        if (value.Length is not (6 or 8))
            throw new ArgumentException("Transcript colors must be resolved #RRGGBB or #RRGGBBAA values.", nameof(hex));
        return new NativeRgba(byte.Parse(value[..2], NumberStyles.HexNumber),
            byte.Parse(value.Slice(2, 2), NumberStyles.HexNumber),
            byte.Parse(value.Slice(4, 2), NumberStyles.HexNumber),
            value.Length == 8 ? byte.Parse(value.Slice(6, 2), NumberStyles.HexNumber) : (byte)255);
    }
}
