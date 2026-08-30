namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/grep.ts
/// </summary>
public sealed class GrepTool : ITool
{
    public string Name => "grep";

    public string Description =>
        "Search file contents using regular expressions. Use it to locate specific code, symbols, or text patterns, and narrow searches with `path` or `include`. Returns matching file paths, line numbers, and line previews.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "pattern": { "type": "string", "description": "Regular expression to search for in file contents (ripgrep syntax)" },
            "path": { "type": "string", "description": "File or directory to search. Defaults to the current working directory." },
            "include": { "type": "string", "description": "Glob pattern to filter files (for example, \"*.js\" or \"*.{ts,tsx}\")" },
            "limit": { "type": "integer", "description": "Maximum number of matching lines to return (default: 100)" }
        },
        "required": ["pattern"]
    }
    """).RootElement;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var searchPath = input.TryGetProperty("path", out var pProp) && !string.IsNullOrEmpty(pProp.GetString())
            ? pProp.GetString()!
            : Directory.GetCurrentDirectory();

        var limit = input.TryGetProperty("limit", out var limProp) ? limProp.GetInt32() : 100;
        if (limit < 1) limit = 100;

        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.Multiline);
        var sb = new StringBuilder();
        int totalMatches = 0;

        IEnumerable<string> files;
        if (File.Exists(searchPath))
        {
            files = [searchPath];
        }
        else if (Directory.Exists(searchPath))
        {
            files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".git") && !f.Contains("bin") && !f.Contains("obj") && !f.Contains("node_modules"));
        }
        else
        {
            throw new DirectoryNotFoundException($"Directory or file not found: {searchPath}");
        }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested || totalMatches >= limit) break;

            try
            {
                var lines = await File.ReadAllLinesAsync(file, Encoding.UTF8, ct);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        totalMatches++;
                        sb.AppendLine($"{file}:{i + 1}: {lines[i]}");
                        if (totalMatches >= limit) break;
                    }
                }
            }
            catch { }
        }

        if (totalMatches == 0)
        {
            return new ToolExecutionResult($"No matches found for pattern '{pattern}'.");
        }

        return new ToolExecutionResult(sb.ToString().TrimEnd());
    }
}
