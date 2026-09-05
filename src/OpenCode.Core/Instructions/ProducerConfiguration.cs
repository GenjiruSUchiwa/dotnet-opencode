namespace OpenCode.Core.Instructions;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Schema;

internal sealed record ProducerConfiguration(
    IReadOnlyList<(string? Path, JsonObject Info)> Documents,
    IReadOnlyList<string> SkillRoots,
    IReadOnlyList<string> ConfigurationDirectories,
    bool SkillsAvailable,
    bool ReferencesAvailable)
{
    /// <summary>Catalog-only observation; no state survives this call.</summary>
    internal static ProducerConfiguration Read(string location, string home, string global, JsonObject merged)
    {
        using var observations = new InstructionLocationState(location);
        try { return Read(location, home, global, merged, observations); }
        catch (InstructionInitializationBlockedException error)
        {
            throw new IOException("The complete local producer configuration is unavailable.", error);
        }
    }

    /// <summary>
    /// Local Config.entries provenance adapter. Parsing, substitutions and
    /// normalization stay in ConfigLoader; producers never re-merge documents.
    /// </summary>
    internal static ProducerConfiguration Read(string location, string home, string global, JsonObject merged, InstructionLocationState observations)
    {
        var ancestors = new List<string>();
        for (var directory = new DirectoryInfo(location); directory is not null; directory = directory.Parent) ancestors.Add(directory.FullName);
        ancestors.Reverse();
        var disabled = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_PROJECT_DISABLE") ?? Environment.GetEnvironmentVariable("OPENCODE_DISABLE_PROJECT_CONFIG");
        var project = disabled != "1" && !string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFullPath(location), Path.GetFullPath(global), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var skillsAvailable = true;
        var referencesAvailable = true;
        bool ProbeDirectory(string path, bool documents = false)
        {
            try { return IsDirectory(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skillsAvailable = false;
                if (documents) referencesAvailable = false;
                return false;
            }
        }
        var directories = new[] { global }.Concat(project ? ancestors.Select(path => Path.Combine(path, ".opencode")).Where(path => ProbeDirectory(path, true)) : [] ).ToArray();
        var documents = new List<(string? Path, JsonObject Info)>();
        void Add(string file)
        {
            try
            {
                if (!Exists(file)) return;
                var path = Path.GetFullPath(file);
                documents.Add((path, ConfigLoader.LoadDocument(configPath: path)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skillsAvailable = false;
                referencesAvailable = false;
            }
        }
        foreach (var name in new[] { "opencode.json", "opencode.jsonc" }) Add(Path.Combine(global, name));
        if (Environment.GetEnvironmentVariable("OPENCODE_CONFIG") is { Length: > 0 } explicitFile) Add(explicitFile);
        if (project)
        {
            foreach (var path in ancestors)
                foreach (var name in new[] { "opencode.json", "opencode.jsonc" }) Add(Path.Combine(path, name));
            foreach (var path in directories.Skip(1))
                foreach (var name in new[] { "opencode.json", "opencode.jsonc" }) Add(Path.Combine(path, name));
        }
        if (Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT") is { } content)
        {
            var virtualDocument = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new JsonException("Virtual configuration must be an object.");
            var keys = ConfigLoader.NormalizeDocument(virtualDocument).Select(pair => pair.Key);
            // Non-provider fields are atomic in LoadDocument. Its last, already
            // substituted value is the virtual document's value, not a guessed origin.
            var info = new JsonObject();
            foreach (var key in keys) info[key] = merged[key]?.DeepClone();
            documents.Add((null, info));
        }
        documents = observations.Observe(home, global, documents, referencesAvailable).ToList();
        var roots = new List<string>();
        foreach (var ecosystem in new[] { ".claude", ".agents" })
            roots.AddRange(new[] { Path.Combine(home, ecosystem) }
                .Concat(project ? ancestors.Select(path => Path.Combine(path, ecosystem)) : [])
                .Where(path => ProbeDirectory(path)).Distinct(StringComparer.Ordinal).Select(path => Path.Combine(path, "skills")));
        roots.AddRange(directories.SelectMany(path => new[] { Path.Combine(path, "skill"), Path.Combine(path, "skills") }));
        return new ProducerConfiguration(documents, roots, directories, skillsAvailable, referencesAvailable);
    }

    internal IReadOnlyList<string> Skills() => Documents.SelectMany(document => document.Info["skills"] switch
    {
        null => Array.Empty<string>(),
        JsonArray values => values.Select(value => value is JsonValue scalar && scalar.TryGetValue<string>(out var path)
            ? path : throw new JsonException("Configured skill sources must be strings.")).ToArray(),
        _ => throw new JsonException("Configured skills must be an array of source strings.")
    }).ToArray();

    internal void RequireNoPluginSources()
    {
        foreach (var document in Documents)
            foreach (var key in new[] { "plugins", "plugin" })
                if (document.Info[key] is { } value && value is not JsonArray { Count: 0 } && value is not JsonObject { Count: 0 })
                    throw new NotSupportedException("Configured plugins require the native plugin runtime before source catalogs can be complete.");
        foreach (var directory in ConfigurationDirectories)
            foreach (var name in new[] { "plugin", "plugins" })
            {
                var path = Path.Combine(directory, name);
                if (Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    throw new NotSupportedException("Auto-discovered plugins require the native plugin runtime before source catalogs can be complete.");
            }
    }

    internal IReadOnlyList<PermissionRule> SkillPermissions(string agent)
    {
        var rules = new List<PermissionRule>();
        foreach (var document in Documents)
        {
            // ConfigNormalize orders legacy tools, legacy permission, then native rules.
            if (document.Info["tools"] is { } tools)
            {
                if (tools is not JsonObject enabled) throw new JsonException("Legacy tools must be a boolean map.");
                foreach (var item in enabled)
                {
                    if (item.Value is not JsonValue flag || !flag.TryGetValue<bool>(out var allow)) throw new JsonException("Legacy tool flags must be boolean.");
                    rules.Add(new PermissionRule(Action(item.Key), "*", allow ? PermissionEffect.Allow : PermissionEffect.Deny));
                }
            }
            if (document.Info["permission"] is { } permission)
            {
                if (permission is JsonValue) rules.Add(new PermissionRule("*", "*", Effect(permission)));
                else if (permission is JsonObject actions)
                    foreach (var action in actions)
                    {
                        if (action.Value is JsonObject resources)
                            foreach (var resource in resources) rules.Add(new PermissionRule(Action(action.Key), resource.Key, Effect(resource.Value)));
                        else rules.Add(new PermissionRule(Action(action.Key), "*", Effect(action.Value)));
                    }
                else throw new JsonException("Legacy permission must be an effect or rule map.");
            }
            Add(document.Info["permissions"]);
        }
        foreach (var document in Documents) Add(document.Info["agents"]?[agent]?["permissions"]);
        return rules;

        void Add(JsonNode? value)
        {
            if (value is null) return;
            if (value is not JsonArray array) throw new NotSupportedException("Skill visibility requires canonical permission rule arrays.");
            foreach (var item in array)
            {
                if (item is not JsonObject rule || rule["action"] is not JsonValue action || !action.TryGetValue<string>(out var actionText) ||
                    rule["resource"] is not JsonValue resource || !resource.TryGetValue<string>(out var resourceText) ||
                    rule["effect"] is not JsonValue)
                    throw new JsonException("Permission rules require action, resource, and effect strings.");
                rules.Add(new PermissionRule(actionText, resourceText, Effect(rule["effect"])));
            }
        }
    }

    private static string Action(string action) => action switch { "write" or "patch" => "edit", "task" => "subagent", "bash" => "shell", _ => action };
    private static PermissionEffect Effect(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var effect)
        ? effect switch
        {
            "allow" => PermissionEffect.Allow, "deny" => PermissionEffect.Deny, "ask" => PermissionEffect.Ask,
            _ => throw new JsonException("Unknown permission effect.")
        } : throw new JsonException("Permission effect must be a string.");

    private static bool IsDirectory(string path) => Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) != FileAttributes.None;
    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
