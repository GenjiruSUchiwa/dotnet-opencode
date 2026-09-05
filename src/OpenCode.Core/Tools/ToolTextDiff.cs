namespace OpenCode.Core.Tools;

using System.Text;
using OpenCode.Schema;

internal static class ToolTextDiff
{
    private readonly record struct Line(char Kind, string Text);

    public static FileDiffInfo Create(string file, string before, string after, FileDiffStatus status)
    {
        var oldLines = Lines(before);
        var newLines = Lines(after);
        var rows = Diff(oldLines, newLines);
        var patch = new StringBuilder().Append("Index: ").Append(file).Append('\n')
            .Append("===================================================================\n--- ").Append(file).Append("\n+++ ").Append(file).Append('\n');
        var oldPosition = 1;
        var newPosition = 1;
        var index = 0;
        while (index < rows.Count)
        {
            var changed = rows.FindIndex(index, row => row.Kind != ' ');
            if (changed < 0) break;
            var start = Math.Max(index, changed - 4);
            foreach (var row in rows.GetRange(index, start - index))
            {
                if (row.Kind != '+') oldPosition++;
                if (row.Kind != '-') newPosition++;
            }
            var end = changed + 1;
            while (end < rows.Count)
            {
                var next = rows.FindIndex(end, row => row.Kind != ' ');
                if (next < 0 || next - end > 8) break;
                end = next + 1;
            }
            end = Math.Min(rows.Count, end + 4);
            var hunk = rows.GetRange(start, end - start);
            var oldCount = hunk.Count(row => row.Kind != '+');
            var newCount = hunk.Count(row => row.Kind != '-');
            patch.Append($"@@ -{(oldCount == 0 ? oldPosition - 1 : oldPosition)},{oldCount} +{(newCount == 0 ? newPosition - 1 : newPosition)},{newCount} @@\n");
            foreach (var row in hunk)
            {
                patch.Append(row.Kind).Append(row.Text);
                if (!row.Text.EndsWith('\n')) patch.Append("\n\\ No newline at end of file\n");
            }
            oldPosition += oldCount;
            newPosition += newCount;
            index = end;
        }
        return new(file, patch.ToString(), rows.Count(row => row.Kind == '+'), rows.Count(row => row.Kind == '-'), status);
    }

    private static string[] Lines(string text)
    {
        var lines = text.Split('\n');
        return lines.Select((line, index) => index < lines.Length - 1 ? line + "\n" : line).Where(line => line.Length > 0).ToArray();
    }

    private static List<Line> Diff(string[] before, string[] after)
    {
        var trace = new List<Dictionary<int, int>>();
        var frontier = new Dictionary<int, int> { [1] = 0 };
        var retained = 0L;
        for (var distance = 0; distance <= before.Length + after.Length; distance++)
        {
            // Bound worst-case diff memory explicitly rather than allocate an unbounded quadratic trace.
            retained += frontier.Count;
            if (retained > 2_000_000) throw new IOException("File diff exceeds the supported complexity limit; use a smaller edit.");
            trace.Add(new(frontier));
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var x = diagonal == -distance || (diagonal != distance && frontier.GetValueOrDefault(diagonal - 1) < frontier.GetValueOrDefault(diagonal + 1))
                    ? frontier.GetValueOrDefault(diagonal + 1) : frontier.GetValueOrDefault(diagonal - 1) + 1;
                var y = x - diagonal;
                while (x < before.Length && y < after.Length && before[x] == after[y]) { x++; y++; }
                frontier[diagonal] = x;
                if (x < before.Length || y < after.Length) continue;
                var result = new List<Line>();
                for (var step = distance; step >= 0; step--)
                {
                    var prior = trace[step];
                    var k = x - y;
                    var previous = k == -step || (k != step && prior.GetValueOrDefault(k - 1) < prior.GetValueOrDefault(k + 1)) ? k + 1 : k - 1;
                    var px = prior.GetValueOrDefault(previous);
                    var py = px - previous;
                    while (x > px && y > py) { result.Add(new(' ', before[--x])); y--; }
                    if (step == 0) break;
                    if (x == px) result.Add(new('+', after[--y]));
                    else result.Add(new('-', before[--x]));
                }
                result.Reverse();
                return result;
            }
        }
        throw new InvalidOperationException("Unable to calculate file diff.");
    }
}
