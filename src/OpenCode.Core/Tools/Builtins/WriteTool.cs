namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/write.ts
/// </summary>
public sealed class WriteTool : ITool
{
    public string Name => "write";

    public string Description =>
        "Writes a file to the local filesystem, overwriting if one exists. Missing parent directories are created automatically. " +
        "Use this tool to create new files or overwrite existing files. For partial changes, use the edit tool instead.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "Path to the file to write to" },
            "content": { "type": "string", "description": "Content to write to the file" }
        },
        "required": ["path", "content"]
    }
    """).RootElement;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var path = input.GetProperty("path").GetString()!;
        var content = input.GetProperty("content").GetString()!;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);

        return new ToolExecutionResult($"Successfully wrote {content.Length} characters to {path}.");
    }
}
