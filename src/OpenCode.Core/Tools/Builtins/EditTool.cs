namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// Matching behavior from packages/core/src/tool/plugin/edit.ts. Requires real Location mutation services.
/// </summary>
public sealed class EditTool(ToolFilePolicy? policy = null, IToolFileMutation? mutation = null)
{
    public ToolInfo Create() => ToolInfo.FromJson(Name, Description, InputSchema, ExecuteAsync, BuiltinToolSchemas.Edit,
        new ToolOptions(Permission: "edit", CodeMode: false));
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
        var args = new ToolInput(input);
        var path = args.String("path");
        var oldString = args.String("oldString");
        var newString = args.String("newString");
        var replaceAll = args.Boolean("replaceAll");

        if (oldString == newString)
        {
            throw new ToolExecutionException("No changes to apply: oldString and newString are identical.");
        }

        if (oldString.Length == 0)
            throw new ToolExecutionException("oldString must not be empty. Use write to create or overwrite a file.");
        if (policy is null || mutation is null)
            throw new NotSupportedException("edit requires Location, permission and file mutation services.");
        var target = await policy.ResolveAsync(path, ToolPathKind.File, context, ct);
        await using var transaction = await mutation.LockAsync(target.Absolute, ct);

        var original = await transaction.ReadAsync(ct) ?? throw new ToolExecutionException($"File not found: {path}");
        var text = original.Text;
        var ending = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        oldString = oldString.Replace("\r\n", "\n").Replace("\n", ending);
        newString = newString.Replace("\r\n", "\n").Replace("\n", ending);
        var matches = Occurrences(text, oldString);
        if (matches.Count == 0) matches = Occurrences(Normalize(text), Normalize(oldString));
        if (matches.Count == 0) matches = LineOccurrences(text, oldString);
        var count = matches.Count;
        var selected = replaceAll ? matches : matches.Take(1).ToList();
        var length = text.Length + selected.Sum(match => (long)newString.Length - (match.End - match.Start));
        if (length > mutation.MaximumBytes)
            throw new ToolExecutionException($"Edited content exceeds the configured {mutation.MaximumBytes} byte limit.");
        var replacement = new StringBuilder((int)length);
        var position = 0;
        foreach (var match in selected)
        {
            replacement.Append(text.AsSpan(position, match.Start - position)).Append(newString);
            position = match.End;
        }
        var updatedText = replacement.Append(text.AsSpan(position)).ToString();
        if (Encoding.UTF8.GetByteCount(updatedText) > mutation.MaximumBytes)
            throw new ToolExecutionException($"Edited content exceeds the configured {mutation.MaximumBytes} byte limit.");
        var preview = count > 0 && (count == 1 || replaceAll)
            ? mutation.Diff(target.Resource, text, updatedText, FileDiffStatus.Modified) : null;
        await policy.AssertAsync("edit", [target.Resource], ["*"], context,
            preview is null ? null : new Dictionary<string, object> { ["files"] = new[] { preview } }, ct);

        if (count == 0)
        {
            throw new ToolExecutionException($"Could not find oldString in {path}. It must match exactly, including whitespace and indentation.");
        }

        if (count > 1 && !replaceAll)
        {
            throw new ToolExecutionException($"Found {count} matches for oldString, but expected exactly one. Add more surrounding context to make oldString unique, or set replaceAll to true to replace every occurrence.");
        }

        var formatted = await transaction.WriteTextAsync(updatedText, ct);
        var files = new[] { mutation.Diff(target.Resource, text, formatted, FileDiffStatus.Modified) };
        return new ToolExecutionResult($"Edited {target.Resource} ({count} replacement{(count == 1 ? "" : "s")})",
            new { files, replacements = count }, new Dictionary<string, object> { ["files"] = files });
    }

    private readonly record struct Match(int Start, int End);

    private static List<Match> Occurrences(string content, string search)
    {
        var result = new List<Match>();
        var offset = 0;
        while ((offset = content.IndexOf(search, offset, StringComparison.Ordinal)) != -1)
        {
            result.Add(new(offset, offset + search.Length));
            offset += search.Length;
        }
        return result;
    }

    // One-to-one mappings retain offsets in the original UTF-16 source.
    private static string Normalize(string value) => string.Concat(value.Select(character => character switch
    {
        '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
        '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
        >= '\u2010' and <= '\u2015' or '\u2212' => '-',
        '\u00A0' or >= '\u2002' and <= '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
        _ => character
    }));

    private static List<Match> LineOccurrences(string content, string search)
    {
        // ECMAScript trimEnd includes BOM, but not .NET's U+0085 whitespace.
        const string whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
        var trailingNewline = search.EndsWith('\n');
        var expected = search.Split('\n');
        if (trailingNewline) expected = expected[..^1];
        var lines = new List<(int Start, int End, int ContentEnd, string Text, bool Newline)>();
        var start = 0;
        while (start < content.Length)
        {
            var newline = content.IndexOf('\n', start);
            var end = newline == -1 ? content.Length : newline + 1;
            var text = content[start..(newline == -1 ? end : newline)];
            lines.Add((start, end, start + text.Length - (text.EndsWith('\r') ? 1 : 0), text, newline != -1));
            start = end;
        }
        var result = new List<Match>();
        for (var index = 0; index + expected.Length <= lines.Count; index++)
        {
            if (!expected.Select((line, offset) => Normalize(lines[index + offset].Text.TrimEnd(whitespace.ToCharArray())) == Normalize(line.TrimEnd(whitespace.ToCharArray()))).All(match => match)) continue;
            var last = lines[index + expected.Length - 1];
            if (trailingNewline && !last.Newline) continue;
            var candidate = new Match(lines[index].Start, trailingNewline ? last.End : last.ContentEnd);
            if (!result.Any(match => match.End > candidate.Start && match.Start < candidate.End)) result.Add(candidate);
        }
        return result;
    }
}
