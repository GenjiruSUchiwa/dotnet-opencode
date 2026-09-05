namespace OpenCode.Cli.Tui.Transcript;

using System.Text.RegularExpressions;

public sealed record ReasoningSummary(string? Title, string Body)
{
    public static ReasoningSummary Parse(string text)
    {
        var content = text.Trim();
        var match = ReasoningSummaryPattern.Title().Match(content);
        return match.Success ? new(match.Groups["title"].Value.Trim(), content[match.Length..].TrimEnd()) : new(null, content);
    }
}

internal static partial class ReasoningSummaryPattern
{
    // context/thinking.ts: only a complete initial bold block is disclosure metadata.
    [GeneratedRegex(@"^\*\*(?<title>[^*\n]+)\*\*(?:\r?\n\r?\n|$)", RegexOptions.NonBacktracking)]
    internal static partial Regex Title();
}
