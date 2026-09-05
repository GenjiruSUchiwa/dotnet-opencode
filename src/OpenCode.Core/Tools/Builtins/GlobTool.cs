namespace OpenCode.Core.Tools.Builtins;

using System.Text.Json;
using OpenCode.Schema;

public sealed class GlobTool(ToolFilePolicy? policy = null, RipgrepProcess? ripgrep = null)
{
    public ToolInfo Create() => ToolInfo.FromJson(Name, Description, InputSchema, ExecuteAsync, BuiltinToolSchemas.Glob, new ToolOptions(CodeMode: false));
    public string Name => "glob";
    public string Description => "Search file paths using a glob pattern.";
    public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { pattern = new { type = "string" }, path = new { type = "string" }, limit = new { type = "integer", minimum = 1 } },
        required = new[] { "pattern" }
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var args = new ToolInput(input);
        var pattern = args.String("pattern");
        var path = args.OptionalString("path");
        if (path is null or "undefined" or "null") path = ".";
        var limit = args.Integer("limit", 100, 1, int.MaxValue - 1);
        if (policy is null || ripgrep is null) throw new NotSupportedException("glob requires Location, permission and ripgrep services.");
        var target = await policy.ResolveAsync(path, ToolPathKind.Directory, context, ct);
        await policy.AssertAsync(Name, [pattern], ["*"], context,
            new Dictionary<string, object> { ["root"] = path, ["path"] = path, ["limit"] = limit }, ct);
        if (!Directory.Exists(target.Absolute)) throw new ToolExecutionException($"Search path is not a directory: {path}");
        var rows = await ripgrep.GlobAsync(target.Absolute, pattern, limit + 1, ct);
        var entries = rows.Take(limit).Select(row => row with
        {
            Path = Path.GetRelativePath(policy.Location.Directory, Path.GetFullPath(row.Path, target.Absolute))
        }).ToArray();
        var truncated = rows.Count > limit;
        var content = entries.Length == 0 ? "No files found" : string.Join('\n', entries.Select(entry => Path.GetFullPath(entry.Path, policy.Location.Directory)));
        if (truncated) content += $"\n\n(Results are truncated: showing first {entries.Length} results. Consider using a more specific path or pattern.)";
        return new(content, entries, new Dictionary<string, object> { ["count"] = entries.Length, ["truncated"] = truncated });
    }
}
