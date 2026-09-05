namespace OpenCode.Core.Config;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Schema;

public sealed class ConfigLoader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string GetDefaultConfigDirectory() =>
        Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } root
                ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "opencode");

    public static string GetDefaultDataDirectory() =>
        Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR") is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } root
                ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "opencode");

    public static OpenCodeConfig LoadConfig(string? configPath = null, string? directory = null) =>
        LoadDocument(configPath, directory).Deserialize<OpenCodeConfig>() ?? new();

    /// <summary>Returns a caller-owned merged document, retaining fields not represented by OpenCodeConfig.</summary>
    public static JsonObject LoadDocument(string? configPath = null, string? directory = null)
    {
        // An explicit path retains the existing isolated-file API. Automatic discovery
        // follows Config.entries: global, explicit, ancestor files, .opencode, content.
        if (configPath is not null) return ReadDocument(configPath);

        var result = new JsonObject();
        var location = Path.GetFullPath(directory ?? Directory.GetCurrentDirectory());
        foreach (var source in Discover(location, includeDirectories: false))
            MergeDocument(result, ReadDocument(source.Path));
        if (Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT") is { } content)
            MergeDocument(result, ParseDocument(content, location));
        return result;
    }

    private static JsonObject ReadDocument(string path) => File.Exists(path)
        ? ParseDocument(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path))!) : new JsonObject();

    private static JsonObject ParseDocument(string text, string directory)
        => NormalizeDocument(ParseSourceDocument(text, directory));

    private static JsonObject ParseSourceDocument(string text, string directory)
    {
        var document = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject
            ?? throw new JsonException("Configuration must be a JSON object.");
        Substitute(document, directory);
        return document;
    }

    /// <summary>Read one source snapshot without merging away provenance. Does not start
    /// plugins/watchers, access credentials, or write configuration files.</summary>
    public static async Task<ConfigSnapshot> LoadSnapshotAsync(string? directory = null, CancellationToken ct = default)
    {
        var location = Path.GetFullPath(directory ?? Directory.GetCurrentDirectory());
        var sources = new List<ConfigSource>();
        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var source in Discover(location, includeDirectories: true))
        {
            ct.ThrowIfCancellationRequested();
            if (source.Type != "document")
            {
                sources.Add(new ConfigSource.Discovery(source.Type switch
                {
                    "claude" => new ConfigClaudeDirectory(source.Path),
                    "agents" => new ConfigAgentsDirectory(source.Path),
                    _ => new ConfigDirectory(source.Path)
                }));
                continue;
            }
            string text;
            try { text = await File.ReadAllTextAsync(source.Path, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { continue; }
            Add(text, Path.GetDirectoryName(source.Path)!, source.Path);
        }
        if (Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT") is { } content) Add(content, location, null);
        return new ConfigSnapshot(sources.AsReadOnly(), diagnostics.AsReadOnly());

        void Add(string text, string root, string? path)
        {
            try { sources.Add(new ConfigSource.Document(path, ParseSourceDocument(text, root))); }
            catch (JsonException)
            {
                diagnostics.Add(new(path, "$", "invalid", "Rejected malformed JSON or JSONC document."));
            }
        }
    }

    internal static JsonObject MergeSources(IEnumerable<ConfigSource.Document> sources)
    {
        var result = new JsonObject();
        foreach (var source in sources) MergeDocument(result, NormalizeDocument(source.Info));
        return result;
    }

    private static IEnumerable<(string Type, string Path)> Discover(string location, bool includeDirectories)
    {
        var global = Path.GetFullPath(GetDefaultConfigDirectory());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ancestors = new List<string>();
        for (var ancestor = new DirectoryInfo(location); ancestor is not null; ancestor = ancestor.Parent) ancestors.Add(ancestor.FullName);
        ancestors.Reverse();
        var disabled = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_PROJECT_DISABLE")
            ?? Environment.GetEnvironmentVariable("OPENCODE_DISABLE_PROJECT_CONFIG");
        var project = disabled != "1" && !string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase)
            && !location.Equals(global, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (includeDirectories)
        {
            foreach (var (name, type) in new[] { (".claude", "claude"), (".agents", "agents") })
            {
                var paths = new[] { Path.Combine(home, name) }.Where(Directory.Exists)
                    .Concat(project ? ancestors.Select(root => Path.Combine(root, name)).Where(Path.Exists) : [])
                    .Distinct(StringComparer.Ordinal);
                foreach (var path in paths) yield return (type, path);
            }
        }
        foreach (var name in new[] { "opencode.json", "opencode.jsonc" }) yield return ("document", Path.Combine(global, name));
        // Config.loadDirectory declares the global root even when it has no documents.
        if (includeDirectories) yield return ("directory", global);
        if (Environment.GetEnvironmentVariable("OPENCODE_CONFIG") is { Length: > 0 } explicitFile)
            yield return ("document", Path.GetFullPath(explicitFile));
        if (!project) yield break;
        foreach (var root in ancestors)
            foreach (var name in new[] { "opencode.json", "opencode.jsonc" }) yield return ("document", Path.Combine(root, name));
        foreach (var root in ancestors)
        {
            var path = Path.Combine(root, ".opencode");
            foreach (var name in new[] { "opencode.json", "opencode.jsonc" }) yield return ("document", Path.Combine(path, name));
            if (includeDirectories && Path.Exists(path)) yield return ("directory", path);
        }
    }

    // Pure normalization for remote documents: never expand local env/file references.
    internal static JsonObject NormalizeDocument(JsonObject input, bool legacyProviderIds = true)
    {
        var document = input.DeepClone().AsObject();
        var policies = new JsonArray();
        if (document["enabled_providers"] is { } enabled)
        {
            policies.Add(new JsonObject { ["action"] = "provider.use", ["resource"] = "*", ["effect"] = "deny" });
            foreach (var item in enabled.AsArray()) policies.Add(ProviderPolicy(item, "allow"));
        }
        if (document["disabled_providers"] is { } disabled)
            foreach (var item in disabled.AsArray()) policies.Add(ProviderPolicy(item, "deny"));
        if (document["experimental"] is JsonObject experimental && experimental["policies"] is { } nativePolicies)
            foreach (var item in nativePolicies.AsArray())
            {
                if (item is not JsonObject policy || policy["action"]?.GetValue<string>() != "provider.use"
                    || policy["resource"] is not JsonValue resource || !resource.TryGetValue<string>(out _)
                    || policy["effect"]?.GetValue<string>() is not ("allow" or "deny")) throw new JsonException("Invalid provider policy.");
                policies.Add(policy.DeepClone());
            }
        if (policies.Count > 0)
        {
            document["experimental"] ??= new JsonObject();
            document["experimental"]!["policies"] = policies;
        }
        document.Remove("enabled_providers");
        document.Remove("disabled_providers");

        if (document["provider"] is JsonObject legacy)
        {
            var providers = new JsonObject();
            foreach (var (id, value) in legacy)
            {
                if (legacyProviderIds && id is ("azure-cognitive-services" or "google-vertex-anthropic"))
                    throw new NotSupportedException("This legacy provider requires a transport migration that is not implemented.");
                var provider = value?.DeepClone() as JsonObject ?? throw new JsonException("Provider must be an object.");
                if (provider["npm"] is { } npm)
                {
                    var package = npm.GetValue<string>();
                    provider["package"] = package.StartsWith("aisdk:", StringComparison.Ordinal) ? package : "aisdk:" + package;
                }
                provider.Remove("npm");
                var settings = provider["options"]?.DeepClone() as JsonObject ?? new JsonObject();
                provider["headers"] = settings["headers"]?.DeepClone();
                provider["body"] = settings["body"]?.DeepClone();
                settings.Remove("headers");
                settings.Remove("body");
                if (provider["api"] is { } api) settings["baseURL"] = api.DeepClone();
                provider["settings"] = settings;
                provider.Remove("options");
                provider.Remove("api");
                if (provider["models"] is JsonObject models)
                {
                    foreach (var (_, modelValue) in models)
                    {
                        var model = modelValue as JsonObject ?? throw new JsonException("Model must be an object.");
                        model["modelID"] = model["id"]?.DeepClone();
                        model.Remove("id");
                        model["settings"] = model["options"]?.DeepClone();
                        model.Remove("options");
                        if (model["provider"] is JsonObject transport)
                        {
                            if (transport["npm"] is { } modelNpm)
                            {
                                var package = modelNpm.GetValue<string>();
                                model["package"] = package.StartsWith("aisdk:", StringComparison.Ordinal) ? package : "aisdk:" + package;
                            }
                            if (transport["api"] is { } endpoint)
                            {
                                model["settings"] ??= new JsonObject();
                                model["settings"]!["baseURL"] = endpoint.DeepClone();
                            }
                            model.Remove("provider");
                        }
                        if (model.ContainsKey("interleaved"))
                            throw new NotSupportedException("Legacy model compatibility mapping is not implemented.");
                        if (model["status"]?.GetValue<string>() == "deprecated") model["disabled"] = true;
                        var capabilities = model["capabilities"] as JsonObject ?? new JsonObject();
                        if (model["tool_call"] is { } tools) capabilities["tools"] = tools.DeepClone();
                        if (model["modalities"] is JsonObject modalities)
                            foreach (var field in new[] { "input", "output" })
                                if (modalities[field] is { } values) capabilities[field] = values.DeepClone();
                        if (capabilities.Count > 0 && !ReferenceEquals(model["capabilities"], capabilities)) model["capabilities"] = capabilities;
                        if (model["cost"] is JsonObject cost)
                        {
                            var tiers = new JsonArray();
                            foreach (var tier in new[] { cost, cost["context_over_200k"] as JsonObject }.OfType<JsonObject>())
                            {
                                var projected = new JsonObject
                                {
                                    ["input"] = tier["input"]?.DeepClone(), ["output"] = tier["output"]?.DeepClone(),
                                    ["cache"] = new JsonObject { ["read"] = tier["cache_read"]?.DeepClone() ?? JsonValue.Create(0),
                                        ["write"] = tier["cache_write"]?.DeepClone() ?? JsonValue.Create(0) }
                                };
                                if (!ReferenceEquals(tier, cost)) projected["tier"] = new JsonObject { ["type"] = "context", ["size"] = 200_000 };
                                tiers.Add(projected);
                            }
                            model["cost"] = tiers;
                        }
                        if (model["limit"] is JsonObject limits)
                            foreach (var field in new[] { "context", "input", "output" })
                                if (limits[field] is { } limit)
                                {
                                    var number = limit.GetValue<double>();
                                    if (!double.IsFinite(number)) throw new JsonException("Model limits must be finite.");
                                    limits[field] = Math.Clamp(Math.Truncate(number), -9_007_199_254_740_991d, 9_007_199_254_740_991d);
                                }
                        if (model["variants"] is JsonObject variants)
                            model["variants"] = new JsonArray(variants.Select(pair => (JsonNode)new JsonObject
                            {
                                ["id"] = pair.Key,
                                ["settings"] = pair.Value?.DeepClone()
                            }).ToArray());
                    }
                }
                providers[id] = provider;
            }
            // Within one document the canonical provider replaces the legacy entry;
            // overlays apply only between separate configuration documents.
            if (document["providers"] is JsonObject native)
                foreach (var (id, provider) in native) providers[id] = provider?.DeepClone();
            document["providers"] = providers;
            document.Remove("provider");
        }
        return document;
    }

    private static JsonNode? Substitute(JsonNode? node, string directory)
    {
        if (node is JsonObject map)
        {
            foreach (var key in map.Select(pair => pair.Key).ToArray())
            {
                var value = Substitute(map[key], directory);
                if (!ReferenceEquals(value, map[key])) map[key] = value;
            }
        }
        if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
            {
                var value = Substitute(array[index], directory);
                if (!ReferenceEquals(value, array[index])) array[index] = value;
            }
        if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) return node;
        // Substitute decoded string values, never JSON source. Quotes, backslashes,
        // and newlines remain data; comments cannot trigger file reads.
        var expanded = Regex.Replace(text, @"\{env:(?<name>[^}]+)\}", match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? "", RegexOptions.NonBacktracking);
        return JsonValue.Create(Regex.Replace(expanded, @"\{file:(?<path>[^}]+)\}", match =>
        {
            var path = match.Groups[1].Value;
            if (path.StartsWith("~/", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            return File.ReadAllText(Path.GetFullPath(path, directory)).Trim();
        }, RegexOptions.NonBacktracking));
    }

    private static void MergeDocument(JsonObject target, JsonObject overlay)
    {
        var previousPolicies = target["experimental"]?["policies"]?.DeepClone() as JsonArray;
        var nextPolicies = overlay["experimental"]?["policies"]?.DeepClone() as JsonArray;
        foreach (var (key, value) in overlay)
        {
            if (key == "providers" && value is JsonObject providers)
            {
                target[key] ??= new JsonObject();
                MergeProviders(target[key]!.AsObject(), providers);
                continue;
            }
            // Model selections are atomic; an omitted variant must not survive from
            // a lower-priority default selection.
            target[key] = value?.DeepClone();
        }
        // ConfigPolicyPlugin reverses document order but preserves rule order within
        // each document, so user-global policy wins over repository-authored policy.
        if (previousPolicies is not null || nextPolicies is not null)
        {
            var policies = nextPolicies ?? new JsonArray();
            if (previousPolicies is not null)
                foreach (var policy in previousPolicies) policies.Add(policy?.DeepClone());
            target["experimental"] ??= new JsonObject();
            target["experimental"]!["policies"] = policies;
        }
    }

    private static JsonObject ProviderPolicy(JsonNode? value, string effect)
    {
        var resource = value?.GetValue<string>() ?? throw new JsonException("Provider policy IDs must be strings.");
        resource = resource switch { "azure-cognitive-services" => "azure", "google-vertex-anthropic" => "google-vertex", _ => resource };
        return new JsonObject { ["action"] = "provider.use", ["resource"] = resource, ["effect"] = effect };
    }

    internal static void MergeProviders(JsonObject target, JsonObject overlay)
    {
        foreach (var (id, value) in overlay)
        {
            if (value is not JsonObject provider) throw new JsonException("Provider must be an object.");
            target[id] ??= new JsonObject();
            MergeProvider(target[id]!.AsObject(), provider);
        }
    }

    private static void MergeProvider(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is null) continue;
            if (key == "models" && value is JsonObject models)
            {
                target[key] ??= new JsonObject();
                foreach (var (id, model) in models)
                {
                    target[key]![id] ??= new JsonObject();
                    MergeProvider(target[key]![id]!.AsObject(), model?.AsObject() ?? throw new JsonException("Model must be an object."));
                }
                continue;
            }
            if (key == "variants" && value is JsonArray variants)
            {
                target[key] ??= new JsonArray();
                var current = target[key]!.AsArray();
                foreach (var variant in variants)
                {
                    var id = variant?["id"]?.GetValue<string>() ?? throw new JsonException("Variant requires an id.");
                    var existing = current.FirstOrDefault(item => item?["id"]?.GetValue<string>() == id);
                    if (existing is null) current.Add(variant!.DeepClone());
                    else MergeProvider(existing.AsObject(), variant!.AsObject());
                }
                continue;
            }
            if (key == "headers" && value is JsonObject headers)
            {
                target[key] ??= new JsonObject();
                foreach (var (name, header) in headers)
                {
                    var previous = target[key]!.AsObject().Select(pair => pair.Key)
                        .FirstOrDefault(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (previous is not null) target[key]!.AsObject().Remove(previous);
                    target[key]![name] = header?.DeepClone();
                }
                continue;
            }
            if ((key is "settings" or "body" or "compatibility" or "limit" or "capabilities") && value is JsonObject right && target[key] is JsonObject left)
                MergeOverlay(left, right);
            else target[key] = value.DeepClone();
        }
    }

    internal static void MergeOverlay(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (target[key] is JsonObject left && value is JsonObject right) MergeOverlay(left, right);
            else target[key] = value?.DeepClone();
        }
    }

    public static Dictionary<string, AuthEntry> LoadAuth(string? authPath = null)
    {
        authPath ??= Path.Combine(GetDefaultDataDirectory(), "auth.json");
        if (!File.Exists(authPath)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, AuthEntry>>(File.ReadAllText(authPath),
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
    }
}
