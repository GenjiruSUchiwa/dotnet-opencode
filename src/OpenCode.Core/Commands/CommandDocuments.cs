namespace OpenCode.Core.Commands;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Schema;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

public static class CommandDocuments
{
    /// <summary>Consumes ProducerConfiguration provenance, not the lossy merged commands map.</summary>
    public static async Task<IReadOnlyList<LocalCommand>> LoadAsync(
        IReadOnlyList<(string? Path, JsonObject Info)> documents, IReadOnlyList<string> directories, CancellationToken ct = default)
    {
        var result = new List<LocalCommand>();
        var cursor = 0;
        var names = new[] { "opencode.json", "opencode.jsonc" };
        if (directories.Count == 0)
        {
            foreach (var document in documents) Add(document);
            return result;
        }
        foreach (var name in names)
            if (cursor < documents.Count && Same(documents[cursor].Path, Path.Combine(directories[0], name))) Add(documents[cursor++]);
        await AddDirectory(directories[0]);
        var projectStart = documents.Count;
        for (var index = 0; index < documents.Count; index++)
            if (documents[index].Path is null) { projectStart = index; break; }
        foreach (var root in directories.Skip(1))
            foreach (var name in names)
            {
                var last = Enumerable.Range(cursor, documents.Count - cursor)
                    .LastOrDefault(index => Same(documents[index].Path, Path.Combine(root, name)), -1);
                if (last >= 0) projectStart = Math.Min(projectStart, last);
            }
        while (cursor < projectStart) Add(documents[cursor++]);
        foreach (var root in directories.Skip(1))
        {
            foreach (var name in names)
                if (cursor < documents.Count && Same(documents[cursor].Path, Path.Combine(root, name))) Add(documents[cursor++]);
            await AddDirectory(root);
        }
        while (cursor < documents.Count) Add(documents[cursor++]);
        return result;

        void Add((string? Path, JsonObject Info) document)
        {
            foreach (var key in new[] { "command", "commands" })
            {
                if (!document.Info.ContainsKey(key)) continue;
                foreach (var item in document.Info[key] as JsonObject ?? throw new JsonException("Commands must be an object."))
                    result.Add(Decode(item.Key, item.Value as JsonObject ?? throw new JsonException("Command must be an object."), document.Path, key == "command"));
            }
        }

        async Task AddDirectory(string root)
        {
            var files = new List<string>();
            var visiting = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            try
            {
                foreach (var name in new[] { "command", "commands" }) Walk(new DirectoryInfo(Path.Combine(root, name)), Path.Combine(root, name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var command = ParseMarkdown(relative[(relative.IndexOf('/') + 1)..^3], content, file);
                    if (command is not null) result.Add(command);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            void Walk(DirectoryInfo directory, string logical)
            {
                ct.ThrowIfCancellationRequested();
                if (!directory.Exists) return;
                var physical = directory.ResolveLinkTarget(true) as DirectoryInfo ?? directory;
                if (!visiting.Add(physical.FullName)) return;
                try
                {
                    foreach (var child in physical.EnumerateFileSystemInfos())
                    {
                        var path = Path.Combine(logical, child.Name);
                        if ((child.Attributes & FileAttributes.Directory) != 0) Walk(new DirectoryInfo(child.FullName), path);
                        else if (child.Name.EndsWith(".md", StringComparison.Ordinal)) files.Add(path);
                    }
                }
                finally { visiting.Remove(physical.FullName); }
            }
        }
    }

    public static LocalCommand? ParseMarkdown(string name, string content, string? source = null)
    {
        try
        {
            var body = content.TrimStart('\uFEFF');
            var data = new JsonObject();
            if (body.StartsWith("---\n", StringComparison.Ordinal) || body.StartsWith("---\r\n", StringComparison.Ordinal))
            {
                var start = body.IndexOf('\n') + 1;
                var end = Regex.Match(body[start..], @"(?m)^---\r?$", RegexOptions.CultureInvariant);
                if (!end.Success) return null;
                var header = body.Substring(start, end.Index);
                try { data = Yaml(header); }
                catch (YamlException)
                {
                    data = Yaml(string.Join("\n", Regex.Split(header, "\r?\n").SelectMany(line =>
                    {
                        var match = Regex.Match(line, @"^([a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(.*)$");
                        if (!match.Success) return new[] { line };
                        var value = match.Groups[2].Value.Trim();
                        return value.Length > 0 && value is not (">" or "|") && value[0] is not ('\'' or '"') && value.Contains(':')
                            ? new[] { match.Groups[1].Value + ": |-", "  " + value } : [line];
                    })));
                }
                body = body[(start + end.Index + end.Length)..];
            }
            data["template"] = body.Trim();
            return Decode(name, data, source);
        }
        catch (Exception error) when (error is YamlException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException) { return null; }
    }

    private static LocalCommand Decode(string name, JsonObject data, string? source, bool legacy = false)
    {
        ConfigModelSelection? model = null;
        if (data.ContainsKey("model"))
        {
            if (!legacy) model = data["model"]?.Deserialize<ConfigModelSelection>() ?? throw new JsonException("Model cannot be null.");
            if (legacy)
            {
                var text = Text(data["model"]);
                if (Regex.IsMatch(text, @"\A[^/#]+/[^#]+\z"))
                {
                    var split = text.IndexOf('/');
                    var provider = text[..split] switch { "azure-cognitive-services" => "azure", "google-vertex-anthropic" => "google-vertex", var id => id };
                    var variant = data.ContainsKey("variant") ? Text(data["variant"]) : null;
                    model = new(provider, text[(split + 1)..], variant is { Length: > 0 } && !variant.Contains('#') ? variant : null);
                }
            }
        }
        if (legacy && data.ContainsKey("variant")) _ = Text(data["variant"]);
        return new LocalCommand(name, Text(data["template"]), data.ContainsKey("description") ? Text(data["description"]) : null,
            data.ContainsKey("agent") ? Text(data["agent"]) : null, model,
            data.ContainsKey("subtask") ? data["subtask"]?.GetValue<bool>() ?? throw new JsonException("Subtask must be boolean.") : null, source);
    }

    private static JsonObject Yaml(string header)
    {
        var reader = new DeserializerBuilder().WithNodeDeserializer(new LiteralScalar()).WithDuplicateKeyChecking()
            .WithAttemptingUnquotedStringTypeDeserialization().Build();
        return Convert(reader.Deserialize<object?>(new MergingParser(new Parser(new StringReader(header))))) as JsonObject
            ?? throw new JsonException("Command frontmatter must be an object.");

        static JsonNode? Convert(object? value) => value switch
        {
            null => new JsonObject(),
            IDictionary<object, object?> map => new JsonObject(map.Select(item => new KeyValuePair<string, JsonNode?>(item.Key as string ?? throw new JsonException("Frontmatter keys must be strings."), item.Value is null ? null : Convert(item.Value)))),
            IList<object?> list => new JsonArray(list.Select(item => item is null ? null : Convert(item)).ToArray()),
            _ => JsonSerializer.SerializeToNode(value)
        };
    }

    private sealed class LiteralScalar : INodeDeserializer
    {
        public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value, ObjectDeserializer rootDeserializer)
        {
            value = null;
            if (expectedType != typeof(object) || !parser.Accept<Scalar>(out var scalar) || scalar.Style != ScalarStyle.Literal) return false;
            parser.MoveNext(); value = scalar.Value; return true;
        }
    }

    private static bool Same(string? left, string right) => left is not null && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : throw new JsonException("Expected a string.");
}
