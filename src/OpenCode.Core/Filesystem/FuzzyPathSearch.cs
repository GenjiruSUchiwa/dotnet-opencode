namespace OpenCode.Core.Filesystem;

using System.Text;
using OpenCode.Schema;

// Adapted from the no-key search in fuzzysort 3.1.0 (its source banner says 3.0.2).
// Copyright (c) 2018 Stephen Kamenar. MIT; see THIRD-PARTY-NOTICES.md.
// Scores stay unnormalized, as in fuzzysort.go. No substring/glob substitute ranking.
internal static class FuzzyPathSearch
{
    internal sealed record Target(FileSystemEntry Entry, string Text, string Lower, int[] Beginnings);
    private sealed record Match(Target Target, double Score, int[] Indexes);

    internal static Target Prepare(FileSystemEntry entry)
    {
        var text = RemoveAccents(entry.Path);
        var beginnings = new List<int>();
        var wasUpper = false;
        var wasAlphanumeric = false;
        for (var index = 0; index < text.Length; index++)
        {
            var upper = text[index] is >= 'A' and <= 'Z';
            var alphanumeric = upper || text[index] is >= 'a' and <= 'z' or >= '0' and <= '9';
            if ((upper && !wasUpper) || !wasAlphanumeric || !alphanumeric) beginnings.Add(index);
            wasUpper = upper;
            wasAlphanumeric = alphanumeric;
        }
        var next = new int[text.Length];
        var position = 0;
        for (var index = 0; index < next.Length; index++)
        {
            while (position < beginnings.Count && beginnings[position] <= index) position++;
            next[index] = position < beginnings.Count ? beginnings[position] : text.Length;
        }
        return new(entry, text, text.ToLowerInvariant(), next);
    }

    internal static IReadOnlyList<FileSystemEntry> Find(IReadOnlyList<Target> targets, string query,
        FileSystemEntryType? type, int limit, CancellationToken ct)
    {
        // Empty and whitespace-only searches return no results in go(...), without all:true.
        var trimmed = query.Trim(Whitespace);
        if (trimmed.Length == 0) return [];
        var search = RemoveAccents(trimmed).ToLowerInvariant();
        var parts = search.Contains(' ') ? trimmed.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray() : [];
        var heap = new List<Match>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (type is not null && target.Entry.Type != type) continue;
            var match = parts.Length > 0 ? MatchSpaces(target, search, parts) : MatchOne(target, search, target.Beginnings);
            if (match is null) continue;
            if (heap.Count < limit)
            {
                var index = heap.Count;
                heap.Add(match);
                for (var parent = (index - 1) >> 1; index > 0 && match.Score < heap[parent].Score; parent = (index - 1) >> 1)
                {
                    heap[index] = heap[parent];
                    index = parent;
                }
                heap[index] = match;
            }
            else if (match.Score > heap[0].Score)
            {
                heap[0] = match;
                Sift(heap);
            }
        }
        var result = new FileSystemEntry[heap.Count];
        for (var index = result.Length - 1; index >= 0; index--)
        {
            result[index] = heap[0].Target.Entry;
            heap[0] = heap[^1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count > 0) Sift(heap);
        }
        return result;
    }

    // Preserve the upstream heap's tie behavior, not a secondary alphabetical sort.
    private static void Sift(List<Match> heap)
    {
        var value = heap[0];
        var index = 0;
        for (var child = 1; child < heap.Count; child = 1 + (index << 1))
        {
            index = child + 1 < heap.Count && heap[child + 1].Score < heap[child].Score ? child + 1 : child;
            heap[(index - 1) >> 1] = heap[index];
        }
        for (var parent = (index - 1) >> 1; index > 0 && value.Score < heap[parent].Score; parent = (index - 1) >> 1)
        {
            heap[index] = heap[parent];
            index = parent;
        }
        heap[index] = value;
    }

    private static Match? MatchSpaces(Target target, string search, string[] parts)
    {
        var next = (int[])target.Beginnings.Clone();
        var score = 0.0;
        var previousStart = 0;
        var indexes = new HashSet<int>();
        for (var part = 0; part < parts.Length; part++)
        {
            // prepareSearch's word lowerCodes are accent-folded, but its substring
            // needle is the original lowercased word. Preserve that source distinction.
            var result = MatchOne(target, RemoveAccents(parts[part]).ToLowerInvariant(), next, parts[part].ToLowerInvariant());
            if (result is null) return null;
            if (part != parts.Length - 1 && result.Indexes.Zip(result.Indexes.Skip(1)).All(pair => pair.Second == pair.First + 1))
            {
                var beginning = result.Indexes[^1] + 1;
                var replace = next[beginning - 1];
                for (var index = beginning - 1; index >= 0 && next[index] == replace; index--) next[index] = beginning;
            }
            score += result.Score / parts.Length;
            if (result.Indexes[0] < previousStart) score -= (previousStart - result.Indexes[0]) * 2;
            previousStart = result.Indexes[0];
            indexes.UnionWith(result.Indexes);
        }
        var whole = MatchOne(target, search, target.Beginnings);
        return whole is not null && whole.Score > score ? whole : new(target, score, [.. indexes]);
    }

    private static Match? MatchOne(Target target, string search, int[] next, string? substringSearch = null)
    {
        if (search.Length == 0 || target.Lower.Length == 0) return null;
        var simple = new int[search.Length];
        var found = 0;
        for (var index = 0; index < target.Lower.Length && found < search.Length; index++)
            if (target.Lower[index] == search[found]) simple[found++] = index;
        if (found != search.Length) return null;

        var strict = new int[search.Length];
        var searchIndex = 0;
        var targetIndex = simple[0] == 0 ? 0 : next[simple[0] - 1];
        var success = false;
        var backtracks = 0;
        if (targetIndex != target.Text.Length)
        {
            while (true)
            {
                if (targetIndex >= target.Text.Length)
                {
                    if (searchIndex <= 0 || ++backtracks > 200) break;
                    targetIndex = next[strict[--searchIndex]];
                }
                else if (search[searchIndex] == target.Lower[targetIndex])
                {
                    strict[searchIndex++] = targetIndex;
                    if (searchIndex == search.Length) { success = true; break; }
                    targetIndex++;
                }
                else targetIndex = next[targetIndex];
            }
        }
        var substring = search.Length <= 1 ? -1 : target.Lower.IndexOf(substringSearch ?? search, simple[0], StringComparison.Ordinal);
        var substringBeginning = substring >= 0 && (substring == 0 || next[substring - 1] == substring);
        if (substring >= 0 && !substringBeginning)
        {
            for (var index = 0; index < next.Length; index = next[index])
            {
                if (index <= substring || index + search.Length > target.Lower.Length) continue;
                if (!target.Lower.AsSpan(index, search.Length).SequenceEqual(search)) continue;
                substring = index;
                substringBeginning = true;
                break;
            }
        }
        var matches = success && !substringBeginning ? strict : simple;
        if (substring >= 0 && (!success || substringBeginning))
            for (var index = 0; index < search.Length; index++) matches[index] = substring + index;
        var score = 0.0;
        var groups = 0;
        for (var index = 1; index < matches.Length; index++)
            if (matches[index] - matches[index - 1] != 1) { score -= matches[index]; groups++; }
        score -= (12 + matches[^1] - matches[0] - (search.Length - 1)) * groups;
        if (matches[0] != 0) score -= matches[0] * (double)matches[0] * .2;
        if (!success) score *= 1000;
        else
        {
            var beginnings = 1;
            for (var index = next[0]; index < target.Text.Length; index = next[index]) beginnings++;
            if (beginnings > 24) score *= (beginnings - 24) * 10;
        }
        score -= (target.Text.Length - search.Length) / 2.0;
        if (substring >= 0) score /= 1 + search.Length * (double)search.Length;
        if (substringBeginning) score /= 1 + search.Length * (double)search.Length;
        score -= (target.Text.Length - search.Length) / 2.0;
        return new(target, score, matches);
    }

    // Only Latin text is decomposed: globally applying NFD changes Japanese matches.
    private static string RemoveAccents(string text)
    {
        if (text.All(character => character < 128)) return text;
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x300 and <= 0x36f) continue;
            var value = Latin(rune.Value) ? rune.ToString().Normalize(NormalizationForm.FormD) : rune.ToString();
            foreach (var character in value)
                if (character is not (>= '\u0300' and <= '\u036f')) result.Append(character);
        }
        return result.ToString();
    }

    // Script=Latin table from regenerate-unicode-properties 10.2.2, Script/Latin.js.
    private static bool Latin(int value) => value is 0xaa or 0xba or 0x2071 or 0x207f or 0x2132 or 0x214e
        or >= 0x41 and <= 0x5a or >= 0x61 and <= 0x7a or >= 0xc0 and <= 0xd6 or >= 0xd8 and <= 0xf6
        or >= 0xf8 and <= 0x2b8 or >= 0x2e0 and <= 0x2e4 or >= 0x1d00 and <= 0x1d25
        or >= 0x1d2c and <= 0x1d5c or >= 0x1d62 and <= 0x1d65 or >= 0x1d6b and <= 0x1d77
        or >= 0x1d79 and <= 0x1dbe or >= 0x1e00 and <= 0x1eff or >= 0x2090 and <= 0x209c
        or >= 0x212a and <= 0x212b or >= 0x2160 and <= 0x2188 or >= 0x2c60 and <= 0x2c7f
        or >= 0xa722 and <= 0xa787 or >= 0xa78b and <= 0xa7dc or >= 0xa7f1 and <= 0xa7ff
        or >= 0xab30 and <= 0xab5a or >= 0xab5c and <= 0xab64 or >= 0xab66 and <= 0xab69
        or >= 0xfb00 and <= 0xfb06 or >= 0xff21 and <= 0xff3a or >= 0xff41 and <= 0xff5a
        or >= 0x10780 and <= 0x10785 or >= 0x10787 and <= 0x107b0 or >= 0x107b2 and <= 0x107ba
        or >= 0x1df00 and <= 0x1df1e or >= 0x1df25 and <= 0x1df2a;

    private static readonly char[] Whitespace = ['\u0009', '\u000a', '\u000b', '\u000c', '\u000d', '\u0020', '\u00a0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a',
        '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff'];
}
