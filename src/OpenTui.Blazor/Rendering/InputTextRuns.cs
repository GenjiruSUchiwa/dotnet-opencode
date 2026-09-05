namespace OpenTui.Blazor.Rendering;

using System.Collections.Immutable;
using System.Globalization;
using OpenTui.Blazor.TextMarks;
using OpenTui.Native;

/// <summary>Paint projection only. Text edits, virtual ranges and history remain with the mark owner.</summary>
internal static class InputTextRuns
{
    internal static ImmutableArray<NativeTextRun> Create(string text, IReadOnlyList<TerminalTextMark> marks)
    {
        if (text.Length == 0 || marks.Count == 0) return default;
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        var ranges = marks.Select(mark => new
        {
            Mark = mark,
            Start = Snap(Math.Clamp(mark.Start, 0, text.Length), false),
            End = Snap(Math.Clamp(mark.End, 0, text.Length), true)
        }).Where(range => range.End > range.Start).ToArray();
        if (ranges.Length == 0) return default;
        var points = ranges.SelectMany(range => new[] { range.Start, range.End }).Append(0).Append(text.Length).Distinct().Order().ToArray();
        var runs = ImmutableArray.CreateBuilder<NativeTextRun>();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            // Native highlight spans choose a single highest-priority style.
            // Stable input order resolves ties without changing mark metadata.
            var style = ranges.Where(range => range.Start <= start && range.End >= end)
                .OrderByDescending(range => range.Mark.Priority).FirstOrDefault()?.Mark.Style;
            runs.Add(new NativeTextRun(text[start..end], style?.Foreground, style?.Background, style?.Attributes ?? 0));
        }
        return runs.ToImmutable();

        int Snap(int index, bool end)
        {
            var found = Array.BinarySearch(boundaries, index);
            if (found >= 0) return boundaries[found];
            var next = ~found;
            return boundaries[end ? next : Math.Max(0, next - 1)];
        }
    }
}
