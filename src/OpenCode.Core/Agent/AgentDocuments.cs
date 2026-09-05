namespace OpenCode.Core.Agent;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

/// <summary>ConfigMarkdown and ConfigAgentPlugin directory documents. No instruction or permission cache.</summary>
public static class AgentDocuments
{
    private static readonly string[] NativeKeys = ["model", "request", "system", "description", "mode", "hidden", "color", "steps", "disabled", "permissions"];
    private static readonly string[] LegacyKeys = ["name", "model", "variant", "temperature", "top_p", "prompt", "tools", "disable", "description", "mode", "hidden", "options", "color", "steps", "maxSteps", "permission"];

    public static async Task<IReadOnlyList<(string? Path, JsonObject Info)>> LoadAsync(
        IReadOnlyList<(string? Path, JsonObject Info)> documents, IReadOnlyList<string> configurationDirectories, CancellationToken ct = default)
    {
        var result = new List<(string? Path, JsonObject Info)>();
        if (configurationDirectories.Count == 0) return documents.Select(document => (document.Path, Normalize(document.Info))).ToArray();
        var names = new[] { "opencode.json", "opencode.jsonc" };
        var cursor = 0;
        foreach (var name in names)
            if (cursor < documents.Count && Same(documents[cursor].Path, System.IO.Path.Combine(configurationDirectories[0], name)))
                Add(documents[cursor++]);
        await AddDirectory(configurationDirectories[0]).ConfigureAwait(false);

        // ProducerConfiguration retains documents in Config.entries order but
        // omits directory markers. Project JSON entries form its final file suffix;
        // use their last occurrence so an explicit/direct copy is not moved.
        var projectStart = documents.Count;
        for (var index = 0; index < documents.Count; index++)
            if (documents[index].Path is null) { projectStart = index; break; }
        foreach (var root in configurationDirectories.Skip(1))
            foreach (var name in names)
            {
                var path = System.IO.Path.Combine(root, name);
                var last = Enumerable.Range(cursor, documents.Count - cursor).LastOrDefault(index => Same(documents[index].Path, path), -1);
                if (last >= 0) projectStart = Math.Min(projectStart, last);
            }
        while (cursor < projectStart) Add(documents[cursor++]);
        foreach (var root in configurationDirectories.Skip(1))
        {
            foreach (var name in names)
                if (cursor < documents.Count && Same(documents[cursor].Path, System.IO.Path.Combine(root, name))) Add(documents[cursor++]);
            await AddDirectory(root).ConfigureAwait(false);
        }
        while (cursor < documents.Count) Add(documents[cursor++]);
        return result;

        void Add((string? Path, JsonObject Info) document) => result.Add((document.Path, Normalize(document.Info)));
        async Task AddDirectory(string root)
        {
            var discovered = new List<(string File, bool Primary)>();
            try
            {
                foreach (var primary in new[] { false, true })
                {
                    var files = new List<string>();
                    var visiting = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    foreach (var name in primary ? new[] { "mode", "modes" } : ["agent", "agents"])
                        Walk(new DirectoryInfo(System.IO.Path.Combine(root, name)), System.IO.Path.Combine(root, name), !primary);
                    discovered.AddRange(files.Order(StringComparer.Ordinal).Select(file => (file, primary)));

                    void Walk(DirectoryInfo directory, string logical, bool recurse)
                    {
                        ct.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(directory.FullName); }
                        catch (FileNotFoundException) { return; }
                        catch (DirectoryNotFoundException) { return; }
                        if ((attributes & FileAttributes.Directory) == FileAttributes.None) return;
                        var physical = directory.ResolveLinkTarget(true) as DirectoryInfo ?? directory;
                        if (!visiting.Add(physical.FullName)) return;
                        try
                        {
                            foreach (var child in physical.EnumerateFileSystemInfos())
                            {
                                var path = System.IO.Path.Combine(logical, child.Name);
                                if ((child.Attributes & FileAttributes.Directory) != FileAttributes.None)
                                {
                                    if (recurse) Walk(new DirectoryInfo(child.FullName), path, true);
                                }
                                else if (child.Name.EndsWith(".md", StringComparison.Ordinal)) files.Add(path);
                            }
                        }
                        finally { visiting.Remove(physical.FullName); }
                    }
                }
            }
            // ConfigAgentPlugin.discover discards the whole directory discovery
            // on I/O failure; readFileStringSafe then omits individual unreadable files.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
            foreach (var file in discovered)
            {
                ct.ThrowIfCancellationRequested();
                string content;
                try { content = await File.ReadAllTextAsync(file.File, ct).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if (content.Length == 0) continue;
                var item = Parse(content, file.Primary);
                if (item is null) continue;
                var relative = System.IO.Path.GetRelativePath(root, file.File).Replace('\\', '/');
                var id = relative[(relative.IndexOf('/') + 1)..^3];
                result.Add((file.File, new JsonObject { ["agents"] = new JsonObject { [id] = item } }));
            }
        }
    }

    /// <summary>Invalid Markdown/frontmatter is omitted, matching ConfigMarkdown.parseOption and decodeAgent.</summary>
    public static JsonObject? Parse(string content, bool primary = false)
    {
        try
        {
            var body = content.TrimStart('\uFEFF');
            var data = new JsonObject();
            if (body.StartsWith("---\n", StringComparison.Ordinal) || body.StartsWith("---\r\n", StringComparison.Ordinal))
            {
                var start = body.IndexOf('\n') + 1;
                var end = Regex.Match(body[start..], @"(?m)^---\r?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                if (!end.Success) return null;
                var header = body.Substring(start, end.Index);
                try { data = Yaml(header); }
                catch (YamlException)
                {
                    header = string.Join("\n", Regex.Split(header, "\r?\n", RegexOptions.NonBacktracking).SelectMany(line =>
                    {
                        var match = Regex.Match(line, @"^(?<key>[a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(?<value>.*)$", RegexOptions.NonBacktracking);
                        if (!match.Success) return new[] { line };
                        var value = match.Groups[2].Value.Trim();
                        return value.Length > 0 && value is not (">" or "|") && value[0] is not ('\'' or '"') && value.Contains(':')
                            ? new[] { match.Groups[1].Value + ": |-", "  " + value } : [line];
                    }));
                    data = Yaml(header);
                }
                body = body[(start + end.Index + end.Length)..];
            }
            var legacy = data.Any(pair => pair.Key != "variant" && !NativeKeys.Contains(pair.Key, StringComparer.Ordinal));
            data[legacy ? "prompt" : "system"] = body.Trim();
            var agent = legacy ? Migrate(data) : Canonical(data);
            if (primary) agent["mode"] = "primary";
            return agent;
        }
        catch (Exception error) when (error is YamlException or JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static JsonObject Normalize(JsonObject document)
    {
        var result = document.DeepClone().AsObject();
        var agents = new JsonObject();
        if (document.TryGetPropertyValue("agent", out var legacy))
            foreach (var item in legacy as JsonObject ?? throw new JsonException("Legacy agents must be an object."))
                agents[item.Key] = Migrate(item.Value as JsonObject ?? throw new JsonException("Legacy agent must be an object."));
        if (document.TryGetPropertyValue("small_model", out var small) && LegacyModel(Text(small), null) is { } model)
        {
            agents["title"] ??= new JsonObject();
            agents["title"]!["model"] ??= model;
        }
        if (document.TryGetPropertyValue("mode", out var modes))
            foreach (var item in modes as JsonObject ?? throw new JsonException("Legacy modes must be an object."))
            {
                var mode = Migrate(item.Value as JsonObject ?? throw new JsonException("Legacy mode must be an object."));
                mode["mode"] = "primary";
                agents[item.Key] = mode;
            }
        if (document.TryGetPropertyValue("agents", out var native))
            foreach (var item in native as JsonObject ?? throw new JsonException("Agents must be an object."))
                agents[item.Key] = Canonical(item.Value as JsonObject ?? throw new JsonException("Agent must be an object."));
        result.Remove("agent");
        result.Remove("mode");
        if (agents.Count > 0 || document.ContainsKey("agents") || document.ContainsKey("agent") || document.ContainsKey("mode")) result["agents"] = agents;
        return result;
    }

    private static JsonObject Migrate(JsonObject input)
    {
        foreach (var key in new[] { "model", "variant", "prompt", "description" }) if (input.ContainsKey(key)) _ = Text(input[key]);
        foreach (var key in new[] { "disable", "hidden" }) if (input.ContainsKey(key)) _ = Boolean(input[key]);
        foreach (var key in new[] { "steps", "maxSteps" }) if (input.ContainsKey(key)) Positive(input[key]);
        foreach (var key in new[] { "temperature", "top_p" })
            if (input.ContainsKey(key) && !double.IsFinite(Number(input[key]))) throw new JsonException("Expected finite sampling value.");
        var body = input.TryGetPropertyValue("options", out var options)
            ? options?.DeepClone() as JsonObject ?? throw new JsonException("Agent options must be an object.") : new JsonObject();
        foreach (var item in input)
            if (!LegacyKeys.Contains(item.Key, StringComparer.Ordinal)) body[item.Key] = item.Value?.DeepClone();
        foreach (var key in new[] { "temperature", "top_p" }) if (input.ContainsKey(key)) body[key] = input[key]?.DeepClone();

        var permission = new JsonObject();
        if (input.TryGetPropertyValue("tools", out var tools))
            foreach (var item in tools as JsonObject ?? throw new JsonException("Tools must be a boolean map."))
                permission[item.Key is "write" or "edit" or "patch" ? "edit" : item.Key] = Boolean(item.Value) ? "allow" : "deny";
        if (input.TryGetPropertyValue("permission", out var rules))
        {
            if (rules is JsonValue) permission["*"] = PermissionEffect(rules);
            else foreach (var item in rules as JsonObject ?? throw new JsonException("Permission must be an effect or map."))
            {
                if (item.Value is JsonObject resources)
                {
                    if (item.Key is "question" or "webfetch" or "websearch" or "doom_loop") throw new JsonException("Permission requires an effect.");
                    foreach (var resource in resources) _ = PermissionEffect(resource.Value);
                }
                else _ = PermissionEffect(item.Value);
                permission[item.Key] = item.Value?.DeepClone();
            }
        }
        var output = new JsonObject();
        if (input.TryGetPropertyValue("model", out var model) && LegacyModel(Text(model), input.ContainsKey("variant") ? Text(input["variant"]) : null) is { } selection) output["model"] = selection;
        if (body.Count > 0) output["request"] = new JsonObject { ["body"] = body };
        foreach (var (before, after) in new[] { ("prompt", "system"), ("description", "description"), ("mode", "mode"), ("hidden", "hidden"), ("disable", "disabled") })
            if (input.ContainsKey(before)) output[after] = input[before]?.DeepClone();
        if (input.ContainsKey("steps") || input.ContainsKey("maxSteps")) output["steps"] = (input["steps"] ?? input["maxSteps"])?.DeepClone();
        if (input.ContainsKey("color"))
        {
            var color = Text(input["color"]);
            if (!Regex.IsMatch(color, "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.NonBacktracking) && color is not ("primary" or "secondary" or "accent" or "success" or "warning" or "error" or "info")) throw new JsonException("Invalid legacy agent color.");
            output["color"] = color.StartsWith('#') ? color : "#aaaaaa";
        }
        var converted = new JsonArray();
        foreach (var item in permission)
        {
            var action = item.Key switch { "write" or "patch" => "edit", "task" => "subagent", "bash" => "shell", _ => item.Key };
            if (item.Value is JsonObject resources)
                foreach (var resource in resources) converted.Add(new JsonObject { ["action"] = action, ["resource"] = resource.Key, ["effect"] = PermissionEffect(resource.Value) });
            else converted.Add(new JsonObject { ["action"] = action, ["resource"] = "*", ["effect"] = PermissionEffect(item.Value) });
        }
        if (converted.Count > 0) output["permissions"] = converted;
        return Canonical(output);
    }

    private static JsonObject Canonical(JsonObject input)
    {
        var output = new JsonObject();
        foreach (var key in NativeKeys) if (input.ContainsKey(key)) output[key] = input[key]?.DeepClone();
        foreach (var key in new[] { "system", "description" }) if (output.ContainsKey(key)) _ = Text(output[key]);
        foreach (var key in new[] { "hidden", "disabled" }) if (output.ContainsKey(key)) _ = Boolean(output[key]);
        if (output.ContainsKey("mode") && Text(output["mode"]) is not ("primary" or "subagent" or "all")) throw new JsonException("Invalid agent mode.");
        if (output.ContainsKey("color") && !Regex.IsMatch(Text(output["color"]), "\\A#[0-9a-fA-F]{6}\\z", RegexOptions.NonBacktracking)) throw new JsonException("Invalid agent color.");
        if (output.ContainsKey("steps")) Positive(output["steps"]);
        if (output.ContainsKey("model"))
        {
            var model = output["model"] is JsonValue ? OpenCode.Schema.ModelRef.Parse(Text(output["model"])) : output["model"] is JsonObject item
                ? new OpenCode.Schema.ModelRef(Text(item["providerID"]), Text(item["model"]), item.ContainsKey("variant") ? Text(item["variant"]) : null)
                : throw new JsonException("Invalid model selection.");
            if (model.ProviderId.Length == 0 || model.ProviderId.Contains('/') || model.ProviderId.Contains('#') || model.Id.Length == 0 || model.Id.Contains('#') || model.Variant is { } variant && (variant.Length == 0 || variant.Contains('#'))) throw new JsonException("Invalid model selection.");
        }
        if (output.ContainsKey("request"))
        {
            var request = output["request"] as JsonObject ?? throw new JsonException("Request must be an object.");
            if (request.ContainsKey("headers")) foreach (var header in request["headers"] as JsonObject ?? throw new JsonException("Headers must be an object.")) _ = Text(header.Value);
            if (request.ContainsKey("body") && request["body"] is not JsonObject) throw new JsonException("Body must be an object.");
            foreach (var key in request.Select(item => item.Key).Where(key => key is not ("headers" or "body")).ToArray()) request.Remove(key);
        }
        if (output.ContainsKey("permissions"))
            foreach (var rule in output["permissions"] as JsonArray ?? throw new JsonException("Permissions must be an array."))
            {
                if (rule is not JsonObject item) throw new JsonException("Permission must be an object.");
                _ = Text(item["action"]); _ = Text(item["resource"]); _ = PermissionEffect(item["effect"]);
            }
        return output;
    }

    private static JsonObject? LegacyModel(string input, string? variant)
    {
        if (!Regex.IsMatch(input, "\\A[^/#]+/[^#]+\\z", RegexOptions.NonBacktracking)) return null;
        var split = input.IndexOf('/');
        var provider = input[..split] switch { "azure-cognitive-services" => "azure", "google-vertex-anthropic" => "google-vertex", var id => id };
        var model = new JsonObject { ["providerID"] = provider, ["model"] = input[(split + 1)..] };
        if (!string.IsNullOrEmpty(variant) && !variant.Contains('#')) model["variant"] = variant;
        return model;
    }

    private static JsonObject Yaml(string header)
    {
        var reader = new DeserializerBuilder().WithNodeDeserializer(new LiteralScalar()).WithDuplicateKeyChecking()
            .WithAttemptingUnquotedStringTypeDeserialization().Build();
        return Convert(reader.Deserialize<object?>(new MergingParser(new Parser(new StringReader(header))))) as JsonObject
            ?? throw new JsonException("Agent frontmatter must be an object.");

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

    private static bool Same(string? left, string right) => left is not null && string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : throw new JsonException("Expected a string.");
    private static bool Boolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag : throw new JsonException("Expected a boolean.");
    private static double Number(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<double>(out var number) ? number : throw new JsonException("Expected a number.");
    private static string PermissionEffect(JsonNode? value) => Text(value) is "allow" or "deny" or "ask" ? Text(value) : throw new JsonException("Invalid permission effect.");
    private static void Positive(JsonNode? value)
    {
        var number = Number(value);
        if (!double.IsFinite(number) || number < 1 || number > 9_007_199_254_740_991 || number != Math.Truncate(number)) throw new JsonException("Expected a positive integer.");
    }
}
