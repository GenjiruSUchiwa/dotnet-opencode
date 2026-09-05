namespace OpenCode.Core.Vcs;

using System.Text;
using System.Text.RegularExpressions;

internal static class GitPatch
{
    internal static string Empty(string file) => $"Index: {file}\n===================================================================\n--- {file}\t\n+++ {file}\t\n";

    internal static Dictionary<string, string> Chunks(string text, bool truncated, Func<int, string?> fallback)
    {
        var starts = Regex.Matches(text, @"(?:^|\n)diff --git ", RegexOptions.NonBacktracking).Select(match => match.Index + (match.Value[0] == '\n' ? 1 : 0)).ToArray();
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < starts.Length - (truncated ? 1 : 0); index++)
        {
            var chunk = text[starts[index]..(index + 1 < starts.Length ? starts[index + 1] : text.Length)];
            var file = FileFromChunk(chunk) ?? fallback(index);
            if (file is not null) output[file] = output.GetValueOrDefault(file, "") + chunk;
        }
        return output;
    }

    private static string? FileFromChunk(string chunk)
    {
        foreach (var pattern in new[] { @"^\+\+\+ (?<path>.+)$", @"^--- (?<path>.+)$" })
        {
            var match = Regex.Match(chunk, pattern, RegexOptions.Multiline | RegexOptions.NonBacktracking);
            if (match.Success && PathToken(match.Groups[1].Value) is { } file) return file;
        }
        var header = Regex.Match(chunk, @"^diff --git (?<header>.+)$", RegexOptions.Multiline | RegexOptions.NonBacktracking).Groups[1].Value;
        if (header.StartsWith('"'))
        {
            var first = Quoted(header);
            if (first is null) return null;
            return PathToken(header[first.Value.End..].TrimStart());
        }
        var separator = header.IndexOf(" b/", StringComparison.Ordinal);
        return separator < 0 ? null : PathToken(header[(separator + 1)..]);
    }

    private static string? PathToken(string value)
    {
        if (value.Length == 0 || value == "/dev/null") return null;
        var path = value.StartsWith('"') ? Quoted(value)?.Text ?? value : value.Split('\t')[0];
        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
    }

    private static (string Text, int End)? Quoted(string value)
    {
        var output = new StringBuilder();
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"') return (output.ToString(), index + 1);
            if (character != '\\') { output.Append(character); continue; }
            if (++index == value.Length) break;
            output.Append(value[index] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', var next => next });
        }
        return null;
    }
}
