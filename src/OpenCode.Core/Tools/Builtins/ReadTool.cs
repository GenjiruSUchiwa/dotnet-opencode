namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/read.ts
/// </summary>
public sealed class ReadTool : ITool
{
    public string Name => "read";

    public string Description =>
        "Read the contents of a file or directory. Supports text files, images, and PDFs. Images and PDFs are presented directly to the model. " +
        "Each text line is prefixed by its 1-based line number as <line>: <content>. The prefix is for reference and is not part of the file content. " +
        "Directory entries are returned one per line. Use offset and limit to read large files or directories in sections. Prefer one larger read over many small slices, and use grep to find specific content in large files.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "File or directory to read" },
            "offset": { "type": "integer", "description": "The line or directory entry to start reading from (1-based)" },
            "limit": { "type": "integer", "description": "The maximum number of lines or directory entries to read (defaults to 2000)" }
        },
        "required": ["path"]
    }
    """).RootElement;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var path = input.GetProperty("path").GetString()!;
        var offset = input.TryGetProperty("offset", out var offProp) ? offProp.GetInt32() : 1;
        var limit = input.TryGetProperty("limit", out var limProp) ? limProp.GetInt32() : 2000;

        if (offset < 1) offset = 1;
        if (limit < 1) limit = 2000;

        if (Directory.Exists(path))
        {
            var entries = Directory.GetFileSystemEntries(path);
            var paged = entries.Skip(offset - 1).Take(limit).ToArray();
            var content = string.Join("\n", paged.Select(Path.GetFileName));
            return new ToolExecutionResult(content);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File or directory not found: {path}");
        }

        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, ct);
        var sb = new StringBuilder();

        var startIndex = offset - 1;
        var count = Math.Min(limit, lines.Length - startIndex);

        for (int i = 0; i < count; i++)
        {
            var lineNum = startIndex + i + 1;
            sb.Append(lineNum).Append(": ").AppendLine(lines[startIndex + i]);
        }

        return new ToolExecutionResult(sb.ToString().TrimEnd());
    }
}
