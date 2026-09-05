namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

public sealed class GrepTool(ToolFilePolicy? policy = null, RipgrepProcess? ripgrep = null)
{
    public ToolInfo Create() => ToolInfo.FromJson(Name, Description, InputSchema, ExecuteAsync, BuiltinToolSchemas.Grep, new ToolOptions(CodeMode: false));
    public string Name => "grep";
    public string Description => "Search file contents using ripgrep regular expressions, with optional path and include glob filters.";
    public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            pattern = new { type = "string", minLength = 1 }, path = new { type = "string" },
            include = new { type = "string" }, limit = new { type = "integer", minimum = 1 }
        },
        required = new[] { "pattern" }
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var args = new ToolInput(input);
        var pattern = args.String("pattern");
        if (pattern.Length == 0) throw new ToolExecutionException("Pattern must not be empty");
        var path = args.OptionalString("path") ?? ".";
        var include = args.OptionalString("include");
        var limit = args.Integer("limit", 100, 1, int.MaxValue - 1);
        if (policy is null || ripgrep is null) throw new NotSupportedException("grep requires Location, permission and ripgrep services.");
        var target = await policy.ResolveAsync(path, null, context, ct).ConfigureAwait(true);
        await policy.AssertAsync(Name, [pattern], ["*"], context,
            new Dictionary<string, object> { ["root"] = ".", ["path"] = path, ["include"] = include ?? "", ["limit"] = limit }, ct).ConfigureAwait(true);
        var directory = Directory.Exists(target.Absolute);
        if (!directory && !File.Exists(target.Absolute)) throw new ToolExecutionException($"Search path does not exist: {path}");
        var cwd = directory ? target.Absolute : Path.GetDirectoryName(target.Absolute)!;
        var rows = await ripgrep.GrepAsync(cwd, pattern, directory ? null : Path.GetFileName(target.Absolute), include, limit + 1, ct).ConfigureAwait(true);
        var matches = rows.Take(limit).Select(row => row with
        {
            Entry = row.Entry with { Path = Path.GetRelativePath(policy.Location.Directory, Path.GetFullPath(row.Entry.Path, cwd)) }
        }).ToArray();
        var truncated = rows.Count > limit;
        var text = new StringBuilder(matches.Length == 0 ? "No matches found" : $"Found {matches.Length} matches");
        string? current = null;
        foreach (var match in matches)
        {
            if (current != match.Entry.Path)
            {
                text.AppendLine().AppendLine().Append(Path.GetFullPath(match.Entry.Path, policy.Location.Directory)).Append(':');
                current = match.Entry.Path;
            }
            var previewLength = Math.Min(2000, match.Text.Length);
            if (previewLength < match.Text.Length && char.IsHighSurrogate(match.Text[previewLength - 1])) previewLength--;
            text.AppendLine().Append(System.Globalization.CultureInfo.InvariantCulture, $"  Line {match.Line}: {match.Text[..previewLength]}{(previewLength < match.Text.Length ? "..." : "")}");
        }
        if (truncated) text.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n\n(Results are truncated: showing first {matches.Length} results. Consider using a more specific path or pattern.)");
        return new(text.ToString(), matches, new Dictionary<string, object> { ["matches"] = matches.Length, ["truncated"] = truncated });
    }
}
