namespace OpenCode.Cli.Commands.Run;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>The run callbacks from tui/mini/tool.ts; no scroll/snapshot renderer.</summary>
internal static class RunToolPresentation
{
    internal sealed record Inline(string Icon, string Title, string? Description = null, bool Block = false, string? Body = null);

    internal static Inline Describe(string originalName, JsonNode? rawInput, JsonNode? rawMetadata, string status, string output, string directory)
    {
        var name = originalName switch { "bash" => "shell", "task" => "subagent", "apply_patch" => "patch", _ => originalName };
        var input = rawInput is JsonObject obj ? obj.DeepClone().AsObject() : new JsonObject();
        var metadata = rawMetadata as JsonObject ?? new JsonObject();
        var path = Text(input["path"]);
        if (path.Length == 0) path = Text(input["filePath"]);
        if (name is "read" or "write" or "edit" or "lsp" && path.Length > 0) input["path"] = path;
        var agent = Text(input["agent"]);
        if (agent.Length == 0) agent = Text(input["subagent_type"]);
        if (name == "subagent" && agent.Length > 0) input["agent"] = agent;
        try
        {
            switch (name)
            {
                case "shell": return new("$", Truthy(input["command"]) ? Value(input["command"]) : "", Block: true, Body: status == "completed" ? output.Trim() : null);
                case "write": return new("←", "Write " + DisplayPath(Text(input["path"]), directory), Block: true, Body: status == "completed" ? output : null);
                case "edit":
                    var file = Files(metadata["files"]).FirstOrDefault();
                    return new("←", "Edit " + DisplayPath(Text(input["path"]), directory), Block: true,
                        Body: file?["patch"] is JsonValue patch && patch.TryGetValue<string>(out var diff) ? diff : Text(metadata["diff"]));
                case "patch":
                    var files = Files(metadata["files"]).Count();
                    return new("%", files == 0 ? "Patch" : $"Patch {files} file{(files == 1 ? "" : "s")}");
                case "invalid": return new("✗", "Invalid Tool", Block: true, Body: status == "completed" ? output : null);
                case "batch":
                    var calls = (input["tool_calls"] as JsonArray)?.Count ?? 0;
                    return new("#", calls > 0 ? $"Batch {calls} tool{(calls == 1 ? "" : "s")}" : "Batch", Block: true, Body: status == "completed" ? output : null);
                case "glob": case "grep":
                    var root = input["path"];
                    var suffix = Truthy(root) ? "in " + DisplayPath(Value(root), directory) : "";
                    var hasCount = metadata.TryGetPropertyValue(name == "glob" ? "count" : "matches", out var count);
                    var description = !hasCount ? suffix : suffix + (suffix.Length > 0 ? " · " : "") + (count is null ? "null" : Value(count))
                        + " match" + (count is JsonValue n && n.TryGetValue<double>(out var number) && number == 1 ? "" : "es");
                    return new("✱", (name == "glob" ? "Glob" : "Grep") + " \"" + Value(input["pattern"]) + "\"", description.Length == 0 ? null : description);
                case "list":
                    var dir = Text(input["path"]);
                    return new("→", dir.Length == 0 ? "List" : "List " + DisplayPath(dir, directory));
                case "read":
                    var fields = input.Where(pair => pair.Key != "path" && pair.Value is JsonValue value
                        && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        .Select(pair => pair.Key + "=" + Value(pair.Value)).ToArray();
                    return new("→", "Read " + DisplayPath(Text(input["path"]), directory), fields.Length == 0 ? null : "[" + string.Join(", ", fields) + "]");
                case "webfetch": return new("%", Truthy(input["url"]) ? "WebFetch " + Value(input["url"]) : "WebFetch");
                case "websearch":
                    var provider = Text(metadata["provider"]);
                    var title = provider.Length == 0 ? "Web Search" : "Web Search via " + provider[..1].ToUpperInvariant() + provider[1..];
                    return new("◈", Truthy(input["query"]) ? title + " \"" + Value(input["query"]) + "\"" : title);
                case "subagent":
                    var kind = Truthy(input["agent"]) ? Value(input["agent"]) : "unknown";
#pragma warning disable MA0009 // Fixed-width boundary+one-character match is linear. ECMAScript word semantics must not become Unicode \w semantics.
                    kind = Regex.Replace(kind, @"\b\w", match => match.Value.ToUpperInvariant(), RegexOptions.ECMAScript);
#pragma warning restore MA0009
                    var desc = input["description"];
                    return new(status == "error" ? "✗" : status == "running" ? "•" : "✓",
                        Truthy(desc) ? Value(desc) : kind + " Subagent", Truthy(desc) ? kind + " Agent" : null);
                case "skill": return new("→", "Skill \"" + Value(metadata["name"] ?? input["id"]) + "\"");
                case "question":
                    var total = (input["questions"] as JsonArray)?.Count ?? 0;
                    return new("→", $"Asked {total} question{(total == 1 ? "" : "s")}");
                case "lsp":
                    var operation = Truthy(input["operation"]) ? Value(input["operation"]) : "request";
                    var location = Truthy(input["path"]) ? DisplayPath(Value(input["path"]), directory) : "";
                    var position = input["line"]?.GetValueKind() == JsonValueKind.Number && input["character"]?.GetValueKind() == JsonValueKind.Number
                        ? ":" + Value(input["line"]) + ":" + Value(input["character"]) : "";
                    return new("→", "LSP " + operation + (location.Length == 0 ? "" : " " + location + position));
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException) { } // Source falls back if a tool-specific draw fails.
        return new("⚙", name + " " + (input.Count > 0 ? input.ToJsonString(new JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : "Unknown"));
    }

    private static IEnumerable<JsonObject> Files(JsonNode? files) => files is JsonArray array
        ? array.OfType<JsonObject>().Where(file => new[] { "file", "relativePath", "filePath" }.Any(key => Text(file[key]).Length > 0)) : [];
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static bool Truthy(JsonNode? node) => node is not null && (node.GetValueKind() switch
    {
        JsonValueKind.String => Text(node).Length != 0,
        JsonValueKind.False or JsonValueKind.Null => false,
        JsonValueKind.Number => node.GetValue<double>() != 0,
        _ => true
    });
    private static string Value(JsonNode? node) => node switch
    {
        null => "",
        JsonObject => "[object Object]",
        JsonArray array => string.Join(",", array.Select(Value)),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString()
    };

    // Source formatPath: relative inside the base, otherwise absolute. Pure lexical
    // handling supports remote Windows paths without interpreting them as local files.
    private static string DisplayPath(string input, string directory)
    {
        if (input.Length == 0) return "";
        var windows = Windows(directory);
        if (!windows && Windows(input)) return input.Replace('\\', '/');
        var basis = Normalize(directory, windows, null);
        var target = Normalize(input, windows, basis);
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(basis.Root, target.Root, comparison) && target.Parts.Length >= basis.Parts.Length
            && basis.Parts.Select((part, index) => string.Equals(part, target.Parts[index], comparison)).All(equal => equal))
            return target.Parts.Length == basis.Parts.Length ? "." : string.Join('/', target.Parts.Skip(basis.Parts.Length));
        return target.Root + "/" + string.Join('/', target.Parts);
    }
    private static bool Windows(string value) => (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\')
        || value.StartsWith("\\\\", StringComparison.Ordinal);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Keep source-path display failures unchanged; these are compound source-platform context errors.")]
    private static (string Root, string[] Parts) Normalize(string value, bool windows, (string Root, string[] Parts)? basis)
    {
        value = windows ? value.Replace('\\', '/') : value;
        var root = basis?.Root ?? "";
        var parts = new List<string>();
        if (windows && value.StartsWith("//", StringComparison.Ordinal))
        {
            var unc = value[2..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (unc.Length < 2) throw new ArgumentException("Incomplete UNC path.");
            root = "//" + unc[0] + "/" + unc[1];
            value = string.Join('/', unc.Skip(2));
        }
        else if (windows && value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/') { root = value[..2]; value = value[3..]; }
        else if (value.StartsWith('/')) { if (!windows) root = ""; value = value.TrimStart('/'); }
        else
        {
            if (windows && value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') throw new ArgumentException("Drive-relative tool path needs source drive context.");
            if (basis is null) throw new ArgumentException("Tool display base must be absolute.");
            parts.AddRange(basis.Value.Parts);
        }
        foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return (root, parts.ToArray());
    }
}
