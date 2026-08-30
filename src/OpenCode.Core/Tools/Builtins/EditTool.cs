namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/edit.ts
/// </summary>
public sealed class EditTool : ITool
{
    public string Name => "edit";

    public string Description =>
        "Edit the contents of a file by finding and replacing exact text. When editing text from Read output, preserve the exact indentation (tabs or spaces) and omit the line-number prefix, such as `1: `. Never include the prefix in oldString or newString. The edit fails if oldString is not found. By default, oldString must identify a UNIQUE location. Multiple matches FAIL unless replaceAll is true. Add more surrounding context to disambiguate, or set replaceAll to true to replace every occurrence.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "File to edit" },
            "oldString": { "type": "string", "description": "Exact text to find and replace" },
            "newString": { "type": "string", "description": "Text to replace oldString with (must differ from oldString)" },
            "replaceAll": { "type": "boolean", "description": "Whether to replace every occurrence of oldString. When false, oldString must match exactly once. Defaults to false." }
        },
        "required": ["path", "oldString", "newString"]
    }
    """).RootElement;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var path = input.GetProperty("path").GetString()!;
        var oldString = input.GetProperty("oldString").GetString()!;
        var newString = input.GetProperty("newString").GetString()!;
        var replaceAll = input.TryGetProperty("replaceAll", out var repProp) && repProp.GetBoolean();

        if (oldString == newString)
        {
            throw new ArgumentException("newString must differ from oldString.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(oldString, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += oldString.Length;
        }

        if (count == 0)
        {
            throw new InvalidOperationException($"oldString was not found in {path}. Make sure exact indentation and line breaks match.");
        }

        if (count > 1 && !replaceAll)
        {
            throw new InvalidOperationException($"oldString matched {count} times in {path}. Provide more surrounding context to make it unique, or set replaceAll: true.");
        }

        string updatedText;
        if (replaceAll)
        {
            updatedText = text.Replace(oldString, newString);
        }
        else
        {
            int firstIdx = text.IndexOf(oldString, StringComparison.Ordinal);
            updatedText = text[..firstIdx] + newString + text[(firstIdx + oldString.Length)..];
        }

        await File.WriteAllTextAsync(path, updatedText, Encoding.UTF8, ct);

        return new ToolExecutionResult($"Successfully replaced {count} occurrence(s) in {path}.");
    }
}
