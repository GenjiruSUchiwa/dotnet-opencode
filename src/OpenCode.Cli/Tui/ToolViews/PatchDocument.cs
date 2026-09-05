namespace OpenCode.Cli.Tui.ToolViews;

using System.Globalization;
using System.Text.RegularExpressions;

public sealed record PatchLine(int Index, char Kind, string Text, long? OldNumber, long? NewNumber);
public sealed record PatchPair(int Index, PatchLine? Left, PatchLine? Right);
public sealed record PatchHunk(int Index, string Header, IReadOnlyList<PatchLine> Lines, IReadOnlyList<PatchPair> Pairs);
public sealed record PatchDocument(IReadOnlyList<PatchHunk> Hunks, int Digits, string? Error)
{
    /// <summary>Port of diff@9 hunk counts and OpenTUI Diff's ordered add/remove pairing; no patch application or inferred content.</summary>
    public static PatchDocument Parse(string patch)
    {
        var source = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hunks = new List<PatchHunk>();
        long maximum = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (hunks.Count > 0 && (source[index].StartsWith("diff ", StringComparison.Ordinal) || source[index].StartsWith("--- ", StringComparison.Ordinal))) break;
            if (!source[index].StartsWith("@@", StringComparison.Ordinal)) continue;
            var header = source[index];
            var match = HunkHeader.Match(header);
            if (!match.Success) return Invalid(index, "Invalid hunk header.");
            if (!long.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.CurrentCulture, out var oldStart) || !long.TryParse(match.Groups[3].Value, System.Globalization.CultureInfo.CurrentCulture, out var newStart)
                || !long.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "1", System.Globalization.CultureInfo.CurrentCulture, out var oldCount)
                || !long.TryParse(match.Groups[4].Success ? match.Groups[4].Value : "1", System.Globalization.CultureInfo.CurrentCulture, out var newCount)
                || oldStart > long.MaxValue - oldCount - 1 || newStart > long.MaxValue - newCount - 1)
                return Invalid(index, "Hunk line range is not supported.");
            var oldNumber = oldStart + (oldCount == 0 ? 1 : 0);
            var newNumber = newStart + (newCount == 0 ? 1 : 0);
            long removed = 0, added = 0;
            var lines = new List<PatchLine>();
            for (index++; index < source.Length && (removed < oldCount || added < newCount || source[index].StartsWith('\\')); index++)
            {
                var value = source[index];
                var kind = value.Length > 0 ? value[0] : index < source.Length - 1 ? ' ' : '\0';
                if (kind is not ('+' or '-' or ' ' or '\\')) return Invalid(index, "Invalid hunk line or truncated patch.");
                if (kind == '\\')
                {
                    // Keep the marker as a pairing boundary, but do not display or number it.
                    lines.Add(new(lines.Count, kind, value, null, null));
                    continue;
                }
                var old = kind != '+' ? oldNumber++ : (long?)null;
                var next = kind != '-' ? newNumber++ : (long?)null;
                if (old is not null) removed++;
                if (next is not null) added++;
                maximum = Math.Max(maximum, Math.Max(old ?? 0, next ?? 0));
                lines.Add(new(lines.Count, kind, value.Length > 0 ? value[1..] : "", old, next));
            }
            if (added == 0 && newCount == 1) newCount = 0;
            if (removed == 0 && oldCount == 1) oldCount = 0;
            if (added != newCount || removed != oldCount) return Invalid(index, "Hunk line counts do not match.");
            if (index < source.Length && source[index].Length > 0 && source[index][0] is '+' or '-' or ' '
                && !source[index].StartsWith("--- ", StringComparison.Ordinal) && !source[index].StartsWith("+++ ", StringComparison.Ordinal))
                return Invalid(index, "Hunk has more lines than its header declares.");
            var pairs = new List<PatchPair>();
            for (var line = 0; line < lines.Count;)
            {
                if (lines[line].Kind == '\\') { line++; continue; }
                if (lines[line].Kind == ' ') { pairs.Add(new(pairs.Count, lines[line], lines[line])); line++; continue; }
                var deletes = new List<PatchLine>();
                var adds = new List<PatchLine>();
                while (line < lines.Count && lines[line].Kind is '+' or '-')
                {
                    if (lines[line].Kind == '-') deletes.Add(lines[line]);
                    else adds.Add(lines[line]);
                    line++;
                }
                for (var pair = 0; pair < Math.Max(deletes.Count, adds.Count); pair++)
                    pairs.Add(new(pairs.Count, deletes.ElementAtOrDefault(pair), adds.ElementAtOrDefault(pair)));
            }
            hunks.Add(new(hunks.Count, header, lines.Where(line => line.Kind != '\\').ToArray(), pairs));
            index--;
        }
        return new(hunks, maximum.ToString(CultureInfo.InvariantCulture).Length, null);
    }

    private static PatchDocument Invalid(int index, string reason) => new([], 1, $"Error parsing diff at line {index + 1}: {reason}");
    private static readonly Regex HunkHeader = new(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@.*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
