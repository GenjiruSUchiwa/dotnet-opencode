namespace OpenCode.Cli.Tui.Dialogs;

using System.Text;

// Scoring port of fuzzysort v3.1.0 (the upstream TUI lockfile version).
// See LICENSE.fuzzysort. No JavaScript runtime, heap, or process-global caches.
internal static class DialogSearch
{
    private sealed record Match(double Score, int[] Indexes);
    private sealed record Target(string Lower, int[] Next);

    public static double Score(string query, string title, string? category, string? searchText)
    {
        var search = Fold(query.Trim());
        if (search.Length == 0) return 1;
        var parts = search.Contains(' ') ? search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray() : [search];
        var results = new double[3];
        var found = new bool[parts.Length];
        var fields = new[] { title, category ?? "", searchText ?? "" };
        for (var field = 0; field < fields.Length; field++)
        {
            var target = Prepare(fields[field]);
            var original = target.Next.ToArray();
            var score = 0d;
            var previous = 0;
            var matched = false;
            for (var part = 0; part < parts.Length; part++)
            {
                var result = Algorithm(parts[part], target);
                if (result is null) continue;
                matched = found[part] = true;
                score += result.Score / parts.Length;
                if (result.Indexes[0] < previous) score -= (previous - result.Indexes[0]) * 2;
                previous = result.Indexes[0];
                if (part == parts.Length - 1 || result.Indexes.Zip(result.Indexes.Skip(1)).Any(pair => pair.Second - pair.First != 1)) continue;
                var next = result.Indexes[^1] + 1;
                var replace = target.Next[next - 1];
                for (var i = next - 1; i >= 0 && target.Next[i] == replace; i--) target.Next[i] = next;
            }
            if (!matched) continue;
            original.CopyTo(target.Next, 0);
            var whole = Algorithm(search, target);
            if (whole is not null && whole.Score > score) score = whole.Score;
            results[field] = Normalize(score);
        }
        return found.All(value => value) ? results[0] * 2 + results[1] + results[2] : 0;
    }

    private static Match? Algorithm(string search, Target target)
    {
        if (search.Length == 0 || target.Lower.Length == 0) return null;
        var simple = new int[search.Length];
        var position = 0;
        for (var i = 0; i < search.Length; i++)
        {
            position = target.Lower.IndexOf(search[i], position);
            if (position < 0) return null;
            simple[i] = position++;
        }
        var strict = new int[search.Length];
        var searchIndex = 0;
        var index = simple[0] == 0 ? 0 : target.Next[simple[0] - 1];
        var backtracks = 0;
        var success = false;
        while (index < target.Lower.Length || searchIndex > 0)
        {
            if (index >= target.Lower.Length)
            {
                if (++backtracks > 200) break;
                index = target.Next[strict[--searchIndex]];
                continue;
            }
            if (search[searchIndex] == target.Lower[index])
            {
                strict[searchIndex++] = index++;
                if (searchIndex == search.Length) { success = true; break; }
            }
            else index = target.Next[index];
        }
        var substring = search.Length > 1 ? target.Lower.IndexOf(search, simple[0], StringComparison.Ordinal) : -1;
        var beginning = substring >= 0 && (substring == 0 || target.Next[substring - 1] == substring);
        if (substring >= 0 && !beginning)
            for (var i = 0; i < target.Next.Length; i = target.Next[i])
                if (i > substring && target.Lower.AsSpan(i).StartsWith(search, StringComparison.Ordinal)) { substring = i; beginning = true; break; }
        var matches = success ? strict : simple;
        if (substring >= 0 && (!success || beginning)) matches = Enumerable.Range(substring, search.Length).ToArray();
        var score = 0d;
        var groups = 0;
        for (var i = 1; i < matches.Length; i++)
            if (matches[i] - matches[i - 1] != 1) { score -= matches[i]; groups++; }
        score -= (12 + matches[^1] - matches[0] - (search.Length - 1)) * groups;
        if (matches[0] != 0) score -= matches[0] * (double)matches[0] * .2;
        if (!success) score *= 1000;
        else
        {
            var count = 1;
            for (var i = target.Next[0]; i < target.Lower.Length; i = target.Next[i]) count++;
            if (count > 24) score *= (count - 24) * 10;
        }
        score -= (target.Lower.Length - search.Length) / 2d;
        if (substring >= 0) score /= 1 + search.Length * (double)search.Length;
        if (beginning) score /= 1 + search.Length * (double)search.Length;
        score -= (target.Lower.Length - search.Length) / 2d;
        return new(score, matches);
    }

    private static Target Prepare(string value)
    {
        var original = Fold(value, false);
        var beginnings = new List<int>();
        var wasUpper = false;
        var wasAlphanumeric = false;
        for (var i = 0; i < original.Length; i++)
        {
            var upper = original[i] is >= 'A' and <= 'Z';
            var alphanumeric = upper || original[i] is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (upper && !wasUpper || !wasAlphanumeric || !alphanumeric) beginnings.Add(i);
            wasUpper = upper;
            wasAlphanumeric = alphanumeric;
        }
        var next = new int[original.Length];
        var boundary = 0;
        for (var i = 0; i < next.Length; i++)
        {
            while (boundary < beginnings.Count && beginnings[boundary] <= i) boundary++;
            next[i] = boundary < beginnings.Count ? beginnings[boundary] : next.Length;
        }
        return new(original.ToLowerInvariant(), next);
    }

    private static string Fold(string value, bool lower = true)
    {
        var text = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            // Normalize Latin accents only; full-string NFD alters Japanese text.
            var part = rune.Value is >= 0xC0 and <= 0x24F or >= 0x1E00 and <= 0x1EFF
                ? rune.ToString().Normalize(NormalizationForm.FormD) : rune.ToString();
            foreach (var c in part) if (c is < '\u0300' or > '\u036f') text.Append(c);
        }
        return lower ? text.ToString().ToLowerInvariant() : text.ToString();
    }

    private static double Normalize(double score) => Math.Exp((Math.Pow(-score + 1, .04307) - 1) * -2);
}
