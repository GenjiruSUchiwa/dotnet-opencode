namespace OpenCode.Core.Session;

using System.Text;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Source ToolOutput.truncate; retention scheduling remains host-owned.</summary>
internal sealed record SessionToolOutput(double MaxLines, double MaxBytes)
{
    internal static SessionToolOutput FromConfig(JsonObject config)
    {
        var value = config["tool_output"];
        if (value is null) return new(2000, 50 * 1024);
        if (value is not JsonObject limits || limits.Any(pair => pair.Key is not ("max_lines" or "max_bytes")))
            throw new NotSupportedException("Tool output configuration supports max_lines and max_bytes only.");
        double Limit(string name, double fallback)
        {
            if (!limits.TryGetPropertyValue(name, out var node)) return fallback;
            if (node is not JsonValue number || !number.TryGetValue<double>(out var count) || !double.IsFinite(count) || count < 1 || count != Math.Truncate(count))
                throw new ArgumentException("Tool output limits must be positive integers.");
            return count;
        }
        return new(Limit("max_lines", 2000), Limit("max_bytes", 50 * 1024));
    }

    internal async Task<ToolExecutionResult> TruncateAsync(ToolExecutionResult result, CancellationToken ct)
    {
        if (result.Metadata?.ContainsKey("truncated") == true) return result;
        var content = result.Content ?? [];
        var text = string.Join('\n', content.OfType<ToolTextContent>().Select(part => part.Text));
        var lines = text.Split('\n');
        if (text.EndsWith('\n')) lines = lines[..^1];
        var total = Encoding.UTF8.GetByteCount(text);
        var metadata = result.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, object>();
        if (lines.Length <= MaxLines && total <= MaxBytes)
        {
            metadata["truncated"] = false;
            return result with { Metadata = metadata };
        }
        var kept = new List<string>();
        var bytes = 0;
        var hitBytes = false;
        foreach (var line in lines.Take((int)Math.Min(MaxLines, int.MaxValue)))
        {
            var size = Encoding.UTF8.GetByteCount(line) + (kept.Count > 0 ? 1 : 0);
            if ((long)bytes + size > MaxBytes) { hitBytes = true; break; }
            kept.Add(line);
            bytes += size;
        }
        if (!hitBytes && kept.Count == lines.Length && total > bytes) hitBytes = true;
        var removed = hitBytes ? total - bytes : lines.Length - kept.Count;
        var unit = hitBytes ? removed == 1 ? "byte" : "bytes" : removed == 1 ? "line" : "lines";
        var directory = Path.GetFullPath(Path.Combine(ConfigLoader.GetDefaultDataDirectory(), "tool-output"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "tool_" + Identifier.Ascending());
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), ct);
        var marker = $"... {removed} {unit} truncated; full content saved to {file} ...";
        var bounded = new List<ToolContent>();
        var remaining = string.Join('\n', kept).Length;
        var seen = false;
        var marked = false;
        foreach (var item in content)
        {
            if (item is ToolFileContent) { bounded.Add(item); continue; }
            if (item is not ToolTextContent part) throw new ToolContractException("Unsupported tool content.");
            if (seen && remaining > 0) remaining--;
            seen = true;
            if (remaining >= part.Text.Length) { bounded.Add(part); remaining -= part.Text.Length; continue; }
            if (remaining > 0) bounded.Add(new ToolTextContent(part.Text[..remaining]));
            if (!marked) bounded.Add(new ToolTextContent(marker));
            remaining = 0;
            marked = true;
        }
        if (!marked) bounded.Add(new ToolTextContent(marker));
        metadata["truncated"] = true;
        metadata["outputPath"] = file;
        return result with { Content = bounded, Metadata = metadata };
    }
}
