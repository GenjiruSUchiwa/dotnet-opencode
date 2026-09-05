namespace OpenCode.Core.Agent;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Core.Config;
using OpenCode.Core.Instructions;
using OpenCode.Schema;

/// <summary>
/// Native built-ins plus ordered local document transforms. Catalog visibility
/// does not activate tools or imply runner support for every agent setting.
/// Plugin transforms remain unsupported; Markdown agents use the native document loader.
/// </summary>
public static class AgentCatalog
{
    public static Task<IReadOnlyList<AgentInfo>> ListAsync(string directory, CancellationToken ct = default) => ReadAsync(directory, ct);

    public static async Task<AgentInfo?> ResolveAsync(string directory, AgentId? id = null, CancellationToken ct = default)
    {
        var agents = await ListAsync(directory, ct);
        return id is { } selected ? agents.FirstOrDefault(agent => agent.Id == selected)
            : agents.FirstOrDefault(Selectable);
    }

    private static async Task<IReadOnlyList<AgentInfo>> ReadAsync(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Agent catalogs require an absolute Location directory.");
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var global = Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory());
        var data = Path.GetFullPath(ConfigLoader.GetDefaultDataDirectory());
        // A live policy catalog must not reuse redacted, last-good instruction snapshots.
        using var observations = new InstructionLocationState(Path.GetFullPath(directory));
        ProducerConfiguration source;
        try
        {
            source = ProducerConfiguration.Read(observations.Directory, home, global,
                ConfigLoader.LoadDocument(directory: directory), observations);
        }
        catch (InstructionInitializationBlockedException error)
        {
            throw new IOException("No complete current agent configuration is available.", error);
        }
        if (!source.ReferencesAvailable) throw new IOException("The complete agent configuration could not be read.");
        source.RequireNoPluginSources();
        var documents = await AgentDocuments.LoadAsync(source.Documents, source.ConfigurationDirectories, ct);

        var external = new[] { Path.Combine(data, "shell", "*", "*"), Path.Combine(data, "tool-output", "*"),
            Path.Combine(Path.GetTempPath(), "opencode", "*"), Path.Combine(global, "*") }
            .Select(path => new PermissionRule("external_directory", path, PermissionEffect.Allow)).ToArray();
        AgentInfo Defaults(string id)
        {
            var defaults = AgentInfo.CreateDefault(AgentId.FromExisting(id));
            return defaults with { Permissions = defaults.Permissions.Concat(external).ToArray() };
        }
        var agents = new OrderedDictionary<string, AgentInfo>();
        var build = Defaults("build");
        agents.Add("build", build with { Name = "Build", Description = "The default agent. Executes tools based on configured permissions.",
            Permissions = [.. build.Permissions, new("question", "*", PermissionEffect.Allow)] });
        var general = Defaults("general");
        agents.Add("general", general with { Name = "General", Mode = AgentMode.Subagent,
            Description = "General-purpose agent for researching complex questions and executing multi-step tasks. Use this agent to execute multiple units of work in parallel.",
            Permissions = [.. general.Permissions, new("question", "*", PermissionEffect.Deny), new("subagent", "*", PermissionEffect.Deny)] });
        var explore = Defaults("explore");
        agents.Add("explore", explore with { Name = "Explore", Mode = AgentMode.Subagent, System = AgentPrompts.Explore,
            Description = "Fast agent specialized for exploring codebases. Use this when you need to quickly find files by patterns (eg. \"src/components/**/*.tsx\"), search code for keywords (eg. \"API endpoints\"), or answer questions about the codebase (eg. \"how do API endpoints work?\"). When calling this agent, specify the desired thoroughness level: \"quick\" for basic searches, \"medium\" for moderate exploration, or \"very thorough\" for comprehensive analysis across multiple locations and naming conventions.",
            Permissions = [.. explore.Permissions, new("*", "*", PermissionEffect.Deny),
                new("grep", "*", PermissionEffect.Allow), new("glob", "*", PermissionEffect.Allow),
                new("webfetch", "*", PermissionEffect.Allow), new("websearch", "*", PermissionEffect.Allow),
                new("read", "*", PermissionEffect.Allow), new("read", "*.env", PermissionEffect.Ask),
                new("read", "*.env.*", PermissionEffect.Ask), new("read", "*.env.example", PermissionEffect.Allow),
                new("subagent", "*", PermissionEffect.Deny), new("external_directory", "*", PermissionEffect.Ask), .. external] });
        foreach (var (id, name, prompt) in new[] { ("compaction", "Compaction", AgentPrompts.Compaction),
            ("title", "Title", AgentPrompts.Title), ("summary", "Summary", AgentPrompts.Summary) })
        {
            var defaults = Defaults(id);
            agents.Add(id, defaults with { Name = name, Hidden = true, System = prompt,
                Permissions = [.. defaults.Permissions, new("*", "*", PermissionEffect.Deny)] });
        }

        // Global rules from all documents precede every per-agent transform.
        var permissions = documents.SelectMany(document => GlobalRules(document.Info, home)).ToArray();
        foreach (var id in agents.Keys.ToArray()) agents[id] = agents[id] with { Permissions = [.. agents[id].Permissions, .. permissions] };
        string? configuredDefault = null;
        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (document.Info.ContainsKey("default_agent")) configuredDefault = Text(document.Info["default_agent"]);
            if (!document.Info.TryGetPropertyValue("agents", out var entries)) continue;
            if (entries is not JsonObject configured) throw new JsonException("Agents must be an object.");
            foreach (var pair in configured)
            {
                if (pair.Value is not JsonObject item) throw new JsonException("Agent configuration must be an object.");
                if (item.Any(field => field.Key is not ("model" or "request" or "system" or "description" or "mode" or "hidden" or "color" or "steps" or "disabled" or "permissions")))
                    throw new NotSupportedException("Agent configuration contains unsupported fields; no partial catalog was returned.");
                if (item.ContainsKey("disabled") && Boolean(item["disabled"])) { agents.Remove(pair.Key); continue; }
                var exists = agents.TryGetValue(pair.Key, out var current);
                var agent = current ?? Defaults(pair.Key);
                if (!exists) agent = agent with { Permissions = [.. agent.Permissions, .. permissions] };
                if (item.ContainsKey("model")) agent = agent with { Model = Model(item["model"]) };
                if (item.TryGetPropertyValue("request", out var request))
                {
                    if (request is not JsonObject overlays || overlays.Any(pair => pair.Key is not ("headers" or "body")))
                        throw new NotSupportedException("Agent request configuration supports only headers and body overlays.");
                    var headers = agent.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    var body = agent.Request.Body.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (overlays.TryGetPropertyValue("headers", out var headerValues))
                        foreach (var header in headerValues as JsonObject ?? throw new JsonException("Headers must be a string map.")) headers[header.Key] = Text(header.Value);
                    if (overlays.TryGetPropertyValue("body", out var bodyValues))
                        foreach (var entry in bodyValues as JsonObject ?? throw new JsonException("Body must be a JSON map.")) body[entry.Key] = JsonSerializer.SerializeToElement(entry.Value);
                    agent = agent with { Request = new ProviderRequest(agent.Request.Settings, headers, body) };
                }
                if (item.ContainsKey("system")) agent = agent with { System = Text(item["system"]) };
                if (item.ContainsKey("description")) agent = agent with { Description = Text(item["description"]) };
                if (item.ContainsKey("mode")) agent = agent with { Mode = Text(item["mode"]) switch
                    { "primary" => AgentMode.Primary, "subagent" => AgentMode.Subagent, "all" => AgentMode.All, _ => throw new JsonException("Invalid agent mode.") } };
                if (item.ContainsKey("hidden")) agent = agent with { Hidden = Boolean(item["hidden"]) };
                if (item.ContainsKey("color"))
                {
                    var color = Text(item["color"]);
                    if (!Regex.IsMatch(color, "\\A#[0-9a-fA-F]{6}\\z")) throw new JsonException("Agent color must be a six-digit hex color.");
                    agent = agent with { Color = color };
                }
                if (item.TryGetPropertyValue("steps", out var steps))
                {
                    if (steps is not JsonValue number || !number.TryGetValue<double>(out var count) ||
                        !double.IsFinite(count) || count < 1 || count > 9_007_199_254_740_991 || count != Math.Truncate(count)) throw new JsonException("Agent steps must be a positive integer.");
                    agent = agent with { Steps = count };
                }
                if (item.ContainsKey("permissions")) agent = agent with { Permissions = [.. agent.Permissions, .. Rules(item["permissions"], home)] };
                agents[pair.Key] = agent;
            }
        }
        var selected = !string.IsNullOrEmpty(configuredDefault) && agents.TryGetValue(configuredDefault, out var preferred) && Selectable(preferred) ? preferred
            : agents.TryGetValue("build", out var fallback) && Selectable(fallback) ? fallback : agents.Values.FirstOrDefault(Selectable);
        return selected is null ? agents.Values.ToArray() : [selected, .. agents.Values.Where(agent => agent.Id != selected.Id)];
    }

    private static bool Selectable(AgentInfo agent) => agent.Mode != AgentMode.Subagent && !agent.Hidden;
    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : throw new JsonException("Expected a string.");
    private static bool Boolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag : throw new JsonException("Expected a boolean.");
    private static PermissionEffect Effect(JsonNode? value) => Text(value) switch
        { "allow" => PermissionEffect.Allow, "deny" => PermissionEffect.Deny, "ask" => PermissionEffect.Ask, _ => throw new JsonException("Invalid permission effect.") };
    private static string Action(string action) => action switch { "write" or "patch" => "edit", "task" => "subagent", "bash" => "shell", _ => action };

    private static IEnumerable<PermissionRule> GlobalRules(JsonObject document, string home)
    {
        if (document.TryGetPropertyValue("tools", out var tools))
            foreach (var tool in tools as JsonObject ?? throw new JsonException("Tools must be a boolean map."))
                yield return Expand(new PermissionRule(Action(tool.Key), "*", Boolean(tool.Value) ? PermissionEffect.Allow : PermissionEffect.Deny), home);
        if (document.TryGetPropertyValue("permission", out var permission))
        {
            if (permission is JsonValue) yield return new PermissionRule("*", "*", Effect(permission));
            else foreach (var action in permission as JsonObject ?? throw new JsonException("Permission must be an effect or action map."))
            {
                if (action.Value is JsonObject resources)
                    foreach (var resource in resources) yield return Expand(new PermissionRule(Action(action.Key), resource.Key, Effect(resource.Value)), home);
                else yield return Expand(new PermissionRule(Action(action.Key), "*", Effect(action.Value)), home);
            }
        }
        if (document.ContainsKey("permissions")) foreach (var rule in Rules(document["permissions"], home)) yield return rule;
    }

    private static IEnumerable<PermissionRule> Rules(JsonNode? value, string home)
    {
        foreach (var entry in value as JsonArray ?? throw new JsonException("Permissions must be a rule array."))
        {
            if (entry is not JsonObject rule) throw new JsonException("Permission rule must be an object.");
            yield return Expand(new PermissionRule(Text(rule["action"]), Text(rule["resource"]), Effect(rule["effect"])), home);
        }
    }

    private static PermissionRule Expand(PermissionRule rule, string home)
    {
        if (rule.Action is not ("external_directory" or "read" or "edit")) return rule;
        if (rule.Resource is "~" or "$HOME") return rule with { Resource = home };
        var relative = rule.Resource.StartsWith("~/", StringComparison.Ordinal) ? rule.Resource[2..]
            : rule.Resource.StartsWith("$HOME/", StringComparison.Ordinal) || rule.Resource.StartsWith("$HOME\\", StringComparison.Ordinal) ? rule.Resource[6..] : null;
        return relative is null ? rule : rule with { Resource = Path.GetFullPath(Path.Join(home, relative)) };
    }

    private static ModelRef Model(JsonNode? value)
    {
        var model = value is JsonValue ? ModelRef.Parse(Text(value)) : value is JsonObject entry
            ? new ModelRef(Text(entry["providerID"]), Text(entry["model"]), entry.ContainsKey("variant") ? Text(entry["variant"]) : null)
            : throw new JsonException("Invalid model selection.");
        if (model.ProviderId.Length == 0 || model.ProviderId.Contains('/') || model.ProviderId.Contains('#') ||
            model.Id.Length == 0 || model.Id.Contains('#') || model.Variant is { } variant && (variant.Length == 0 || variant.Contains('#')))
            throw new JsonException("Invalid model selection.");
        return model;
    }
}
