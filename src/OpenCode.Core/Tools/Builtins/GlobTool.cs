namespace OpenCode.Core.Tools.Builtins;

using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/glob.ts
/// </summary>
public sealed class GlobTool : ITool
{
    public string Name => "glob";

    public string Description =>
        "Search file paths using a glob pattern (examples: \"**/*.ts\", \"src/**/*.tsx\").";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "pattern": { "type": "string", "description": "Glob pattern to match files against" },
            "path": { "type": "string", "description": "Directory to search. Defaults to the current working directory." },
            "limit": { "type": "integer", "description": "Maximum number of matching files to return (default: 100)" }
        },
        "required": ["pattern"]
    }
    """).RootElement;

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var searchDir = input.TryGetProperty("path", out var pProp) && !string.IsNullOrEmpty(pProp.GetString())
            ? pProp.GetString()!
            : Directory.GetCurrentDirectory();

        var limit = input.TryGetProperty("limit", out var limProp) ? limProp.GetInt32() : 100;
        if (limit < 1) limit = 100;

        if (!Directory.Exists(searchDir))
        {
            throw new DirectoryNotFoundException($"Directory not found: {searchDir}");
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);
        matcher.AddExclude("**/.git/**");
        matcher.AddExclude("**/node_modules/**");
        matcher.AddExclude("**/bin/**");
        matcher.AddExclude("**/obj/**");

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(searchDir)));
        var files = result.Files.Take(limit).Select(f => Path.Combine(searchDir, f.Path)).ToArray();

        if (files.Length == 0)
        {
            return Task.FromResult(new ToolExecutionResult("No files found matching pattern."));
        }

        return Task.FromResult(new ToolExecutionResult(string.Join("\n", files)));
    }
}
