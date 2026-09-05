namespace OpenCode.Core.Config;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

/// <summary>Config.normalize projection for observed documents. Existing runtime loader
/// values/merge semantics remain untouched. Validation uses the canonical Schema codecs.</summary>
internal static class ConfigEntryProjection
{
    internal static OpenCodeConfiguration Normalize(JsonObject input, Action<string, string, string> diagnostic)
    {
        var output = new JsonObject();
        foreach (var key in new[] { "logLevel", "server", "subagent_depth", "layout" })
            if (input.ContainsKey(key)) diagnostic("$." + key, "unsupported", "Omitted unsupported legacy setting.");

        if (input.ContainsKey("snapshot")) Put("snapshots", Field("snapshots", input["snapshot"]));
        if (input["autoshare"] is JsonValue share && share.TryGetValue<bool>(out var automatic) && automatic) output["share"] = "auto";
        if (input.ContainsKey("attachment")) Put("media", Field("media", input["attachment"]));

        MergeMap("references", Map("references", input["reference"]), Map("references", input["references"]),
            input["reference"] is JsonObject || input["references"] is JsonObject);
        MergeMap("commands", Map("commands", input["command"], LegacyCommand), Map("commands", input["commands"]),
            input["command"] is JsonObject || input["commands"] is JsonObject);
        var agents = Map("agents", input["agent"], value => LegacyAgent(value, false));
        if (LegacyModel(input["small_model"], null) is { } small)
        {
            var title = new JsonObject { ["model"] = small };
            if (agents["title"] is JsonObject existing)
                foreach (var pair in existing) title[pair.Key] = pair.Value?.DeepClone();
            agents["title"] = title;
        }
        var modes = Map("agents", input["mode"], value => LegacyAgent(value, true));
        foreach (var mode in modes) agents[mode.Key] = mode.Value?.DeepClone();
        MergeMap("agents", agents, Map("agents", input["agents"]), agents.Count > 0
            || input["agent"] is JsonObject || input["mode"] is JsonObject || input["agents"] is JsonObject);

        var providers = Map("providers", input["provider"], LegacyProvider);
        if (input["provider"] is JsonObject legacyProviders && legacyProviders.Any(pair => pair.Key is "azure-cognitive-services" or "google-vertex-anthropic"))
            throw new NotSupportedException("Special legacy provider transport migration is not implemented for the configuration entry catalog.");
        MergeMap("providers", providers, Map("providers", input["providers"]), input["provider"] is JsonObject || input["providers"] is JsonObject);

        var permissions = PermissionRules(input["permission"], input["tools"]);
        foreach (var rule in List("permissions", input["permissions"])) permissions.Add(rule?.DeepClone());
        if (permissions.Count > 0 || input["permissions"] is JsonArray) output["permissions"] = permissions;

        var plugins = new JsonArray();
        if (input["plugin"] is JsonArray legacyPlugins)
            foreach (var plugin in legacyPlugins)
            {
                var migrated = plugin is JsonArray tuple && tuple.Count == 2
                    ? new JsonObject { ["package"] = tuple[0]?.DeepClone(), ["options"] = tuple[1]?.DeepClone() } : plugin;
                foreach (var item in List("plugins", new JsonArray(migrated?.DeepClone()))) plugins.Add(item?.DeepClone());
            }
        foreach (var plugin in List("plugins", input["plugins"])) plugins.Add(plugin?.DeepClone());
        if (plugins.Count > 0 || input["plugin"] is JsonArray || input["plugins"] is JsonArray) output["plugins"] = plugins;

        if (input["skills"] is JsonObject skillPaths)
            output["skills"] = new JsonArray(List("skills", skillPaths["paths"]).Concat(List("skills", skillPaths["urls"]))
                .Select(value => value?.DeepClone()).ToArray());
        else if (input["skills"] is JsonArray) output["skills"] = List("skills", input["skills"]);
        else if (input.ContainsKey("skills")) Invalid("skills");
        if (input["instructions"] is JsonArray) output["instructions"] = List("instructions", input["instructions"]);
        else if (input.ContainsKey("instructions")) Invalid("instructions");

        if (input["compaction"] is JsonObject compaction)
        {
            var normalized = new JsonObject();
            if (compaction.ContainsKey("auto")) AddPart("auto", compaction["auto"]);
            if (compaction.ContainsKey("preserve_recent_tokens")) AddPart("keep", new JsonObject { ["tokens"] = compaction["preserve_recent_tokens"]?.DeepClone() });
            if (compaction["keep"] is JsonObject keep && keep.ContainsKey("tokens")) AddPart("keep", new JsonObject { ["tokens"] = keep["tokens"]?.DeepClone() });
            if (compaction.ContainsKey("reserved")) AddPart("buffer", compaction["reserved"]);
            if (compaction.ContainsKey("buffer")) AddPart("buffer", compaction["buffer"]);
            if (normalized.Count > 0 || compaction.Count == 0) output["compaction"] = normalized;
            void AddPart(string key, JsonNode? value)
            {
                if (Field("compaction", new JsonObject { [key] = value?.DeepClone() }) is JsonObject valid && valid.ContainsKey(key)) normalized[key] = valid[key]?.DeepClone();
            }
        }
        else if (input.ContainsKey("compaction")) Invalid("compaction");

        var experimental = new JsonObject();
        var policies = new JsonArray();
        if (input["enabled_providers"] is JsonArray enabled)
        {
            var names = enabled.OfType<JsonValue>().Where(value => value.TryGetValue<string>(out _)).Select(value => value.GetValue<string>()).ToArray();
            if (enabled.Count == 0 || names.Length > 0)
            {
                Policy("*", "deny");
                foreach (var name in names) Policy(ProviderId(name), "allow");
            }
        }
        if (input["disabled_providers"] is JsonArray disabled)
            foreach (var name in disabled.OfType<JsonValue>().Where(value => value.TryGetValue<string>(out _))) Policy(ProviderId(name.GetValue<string>()), "deny");
        if (input["experimental"] is JsonObject settings)
        {
            foreach (var key in new[] { "portable_shell_scanner", "subagent_depth" })
                if (settings.ContainsKey(key) && Field("experimental", new JsonObject { [key] = settings[key]?.DeepClone() }) is JsonObject valid)
                    foreach (var pair in valid) experimental[pair.Key] = pair.Value?.DeepClone();
            if (settings["policies"] is JsonArray native)
                foreach (var policy in native)
                    if (Field("experimental", new JsonObject { ["policies"] = new JsonArray(policy?.DeepClone()) })?["policies"] is JsonArray valid)
                        foreach (var item in valid) policies.Add(item?.DeepClone());
        }
        if (policies.Count > 0 || input["experimental"] is JsonObject { } policySource && policySource["policies"] is JsonArray) experimental["policies"] = policies;
        if (experimental.Count > 0 || input["experimental"] is JsonObject { Count: 0 }) output["experimental"] = experimental;

        var mcp = NormalizeMcp(input, value => Field("mcp", value));
        if (mcp is not null) Put("mcp", Field("mcp", mcp));
        if (input["watcher"] is JsonObject watcher)
        {
            var ignore = Strings(watcher["ignore"]);
            output["watcher"] = ignore.Count > 0 || watcher["ignore"] is JsonArray ? new JsonObject { ["ignore"] = ignore } : new JsonObject();
        }
        foreach (var key in new[] { "formatter", "lsp" })
        {
            if (!input.ContainsKey(key)) continue;
            if (input[key] is JsonObject entries)
            {
                var valid = Map(key, entries);
                if (valid.Count > 0 || entries.Count == 0) output[key] = valid;
            }
            else Put(key, Field(key, input[key]));
        }
        foreach (var key in new[] { "$schema", "shell", "model", "default_agent", "autoupdate", "share", "enterprise", "username",
            "snapshots", "media", "tool_output", "websearch", "warming" })
            if (input.ContainsKey(key)) Put(key, Field(key, input[key]));
        return output.Deserialize(OpenCodeJsonContext.Default.OpenCodeConfiguration)
            ?? throw new JsonException("Canonical configuration projection is empty.");

        void Invalid(string path) => diagnostic("$." + path, "invalid", "Skipped malformed recognized value.");
        void Put(string key, JsonNode? value)
        {
            if (value is null) return;
            if (output.TryGetPropertyValue(key, out var previous) && !JsonNode.DeepEquals(previous, value))
                diagnostic("$." + key, "conflict", "Retained native value over legacy value.");
            output[key] = value.DeepClone();
        }
        JsonNode? Field(string key, JsonNode? value)
        {
            try
            {
                var decoded = new JsonObject { [key] = value?.DeepClone() }.Deserialize(OpenCodeJsonContext.Default.OpenCodeConfiguration)!;
                return JsonSerializer.SerializeToNode(decoded, OpenCodeJsonContext.Default.OpenCodeConfiguration)![key]?.DeepClone();
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                Invalid(key);
                return null;
            }
        }
        JsonObject Map(string key, JsonNode? value, Func<JsonObject, JsonObject>? migrate = null)
        {
            var result = new JsonObject();
            if (value is null) return result;
            if (value is not JsonObject map) { Invalid(key); return result; }
            foreach (var pair in map)
            {
                JsonNode? candidate = pair.Value;
                if (migrate is not null)
                {
                    if (candidate is not JsonObject document) { Invalid(key); continue; }
                    try { candidate = migrate(document); }
                    catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
                    { Invalid(key); continue; }
                }
                if (Field(key, new JsonObject { [pair.Key] = candidate?.DeepClone() }) is JsonObject valid && valid.ContainsKey(pair.Key))
                    result[pair.Key] = valid[pair.Key]?.DeepClone();
            }
            return result;
        }
        JsonArray List(string key, JsonNode? value)
        {
            var result = new JsonArray();
            if (value is null) return result;
            if (value is not JsonArray list) { Invalid(key); return result; }
            foreach (var item in list)
                if (Field(key, new JsonArray(item?.DeepClone())) is JsonArray valid)
                    foreach (var candidate in valid) result.Add(candidate?.DeepClone());
            return result;
        }
        void MergeMap(string key, JsonObject legacy, JsonObject native, bool present)
        {
            foreach (var pair in native)
            {
                if (legacy.ContainsKey(pair.Key) && !JsonNode.DeepEquals(legacy[pair.Key], pair.Value))
                    diagnostic("$." + key, "conflict", "Retained native entry over legacy entry.");
                legacy[pair.Key] = pair.Value?.DeepClone();
            }
            if (present) output[key] = legacy;
        }
        void Policy(string resource, string effect) => policies.Add(new JsonObject { ["action"] = "provider.use", ["resource"] = resource, ["effect"] = effect });
    }

    private static JsonObject LegacyCommand(JsonObject input)
    {
        var result = Copy(input, "template", "description", "agent", "subtask");
        if (LegacyModel(input["model"], input["variant"]) is { } model) result["model"] = model;
        return result;
    }

    private static JsonObject LegacyAgent(JsonObject input, bool primary)
    {
        var result = Copy(input, "description", "mode", "hidden");
        foreach (var (source, destination) in new[] { ("prompt", "system"), ("disable", "disabled"), ("steps", "steps") })
            if (input.ContainsKey(source)) result[destination] = input[source]?.DeepClone();
        if (!result.ContainsKey("steps") && input.ContainsKey("maxSteps")) result["steps"] = input["maxSteps"]?.DeepClone();
        if (input["color"] is JsonValue color && color.TryGetValue<string>(out var text))
            result["color"] = text.StartsWith('#') ? text : text is "primary" or "secondary" or "accent" or "success" or "warning" or "error" or "info"
                ? "#aaaaaa" : throw new JsonException("Invalid legacy agent color.");
        if (LegacyModel(input["model"], input["variant"]) is { } model) result["model"] = model;
        var body = input["options"] is JsonObject options ? options.DeepClone().AsObject() : new JsonObject();
        string[] known = ["name", "model", "variant", "temperature", "top_p", "prompt", "tools", "disable", "description", "mode", "hidden", "options", "color", "steps", "maxSteps", "permission"];
        foreach (var pair in input)
            if (!known.Contains(pair.Key, StringComparer.Ordinal) || pair.Key is "temperature" or "top_p") body[pair.Key] = pair.Value?.DeepClone();
        if (body.Count > 0) result["request"] = new JsonObject { ["body"] = body };
        var rules = new JsonObject();
        if (input["tools"] is JsonObject tools)
            foreach (var pair in tools) rules[pair.Key is "write" or "patch" ? "edit" : pair.Key] = pair.Value!.GetValue<bool>() ? "allow" : "deny";
        if (input["permission"] is JsonObject permission)
            foreach (var pair in permission) rules[pair.Key] = pair.Value?.DeepClone();
        else if (input["permission"] is JsonValue effect) rules["*"] = effect.DeepClone();
        var permissions = PermissionRules(rules, null);
        if (permissions.Count > 0) result["permissions"] = permissions;
        if (primary) result["mode"] = "primary";
        return result;
    }

    private static JsonArray PermissionRules(JsonNode? permission, JsonNode? tools)
    {
        var result = new JsonArray();
        if (tools is JsonObject toggles)
            foreach (var pair in toggles)
                if (pair.Value is JsonValue value && value.TryGetValue<bool>(out var enabled)) Add(pair.Key, "*", enabled ? "allow" : "deny");
        if (permission is JsonValue scalar && scalar.TryGetValue<string>(out var rule)) Add("*", "*", rule);
        if (permission is JsonObject actions)
            foreach (var pair in actions)
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var effect)) Add(pair.Key, "*", effect);
                if (pair.Value is JsonObject resources)
                    foreach (var resource in resources)
                        if (resource.Value is JsonValue item && item.TryGetValue<string>(out var decision)) Add(pair.Key, resource.Key, decision);
            }
        return result;
        void Add(string action, string resource, string effect)
        {
            if (effect is not ("allow" or "deny" or "ask")) return;
            result.Add(new JsonObject { ["action"] = action switch { "write" or "patch" => "edit", "task" => "subagent", "bash" => "shell", _ => action },
                ["resource"] = resource, ["effect"] = effect });
        }
    }

    private static JsonObject? LegacyModel(JsonNode? input, JsonNode? variant)
    {
        if (input is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1 || text.Contains('#')) return null;
        var result = new JsonObject { ["providerID"] = ProviderId(text[..slash]), ["model"] = text[(slash + 1)..] };
        if (variant is JsonValue selection && selection.TryGetValue<string>(out var name) && name.Length > 0 && !name.Contains('#')) result["variant"] = name;
        return result;
    }

    private static JsonObject? NormalizeMcp(JsonObject input, Func<JsonNode?, JsonNode?> decode)
    {
        var config = input["mcp"] as JsonObject;
        var servers = new JsonObject();
        var timeout = new JsonObject();
        if (input["experimental"] is JsonObject experimental && experimental["mcp_timeout"] is JsonValue duration && duration.TryGetValue<double>(out var milliseconds)
            && double.IsFinite(milliseconds) && milliseconds > 0 && Math.Truncate(milliseconds) == milliseconds)
        {
            timeout["catalog"] = milliseconds;
            timeout["execution"] = milliseconds;
        }
        foreach (var pair in config ?? new JsonObject())
        {
            if (pair.Value is not JsonObject server || !LegacyServer(server)) continue;
            var migrated = Copy(server, "type", "command", "cwd", "environment", "url", "headers");
            if (server["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var flag)) migrated["disabled"] = !flag;
            if (server["timeout"] is JsonValue time) migrated["timeout"] = new JsonObject { ["catalog"] = time.DeepClone(), ["execution"] = time.DeepClone() };
            if (server["oauth"] is JsonObject oauth)
            {
                var value = Copy(oauth, "scope");
                foreach (var (old, current) in new[] { ("clientId", "client_id"), ("clientSecret", "client_secret"), ("callbackPort", "callback_port"), ("redirectUri", "redirect_uri") })
                    if (oauth.ContainsKey(old)) value[current] = oauth[old]?.DeepClone();
                migrated["oauth"] = value;
            }
            else if (server["oauth"] is JsonValue oauthFlag && oauthFlag.TryGetValue<bool>(out var enabledOAuth) && !enabledOAuth)
                migrated["oauth"] = false;
            AddServer(pair.Key, migrated);
        }
        if (config?["servers"] is JsonObject native && !LegacyServer(native))
            foreach (var pair in native) AddServer(pair.Key, pair.Value);
        if (config?["timeout"] is JsonObject nativeTimeout && !LegacyServer(nativeTimeout))
            foreach (var pair in nativeTimeout.Where(pair => pair.Key is "startup" or "catalog" or "execution"))
                if (decode(new JsonObject { ["timeout"] = new JsonObject { [pair.Key] = pair.Value?.DeepClone() } })?["timeout"] is JsonObject valid)
                    foreach (var value in valid) timeout[value.Key] = value.Value?.DeepClone();
        var result = new JsonObject();
        if (servers.Count > 0) result["servers"] = servers;
        if (timeout.Count > 0) result["timeout"] = timeout;
        return result.Count > 0 || config is { Count: 0 } ? result : null;

        void AddServer(string name, JsonNode? server)
        {
            if (decode(new JsonObject { ["servers"] = new JsonObject { [name] = server?.DeepClone() } })?["servers"] is JsonObject valid
                && valid.ContainsKey(name)) servers[name] = valid[name]?.DeepClone();
        }
    }

    private static bool LegacyServer(JsonObject value) => value["type"] is JsonValue type
        && type.TryGetValue<string>(out var name) && name is "local" or "remote";

    private static JsonArray Strings(JsonNode? node) => node is JsonArray array
        ? new JsonArray(array.OfType<JsonValue>().Where(value => value.TryGetValue<string>(out _)).Select(value => value.DeepClone()).ToArray()) : [];
    private static string ProviderId(string id) => id switch { "azure-cognitive-services" => "azure", "google-vertex-anthropic" => "google-vertex", _ => id };
    private static JsonObject Copy(JsonObject input, params string[] keys) => new(keys.Where(input.ContainsKey).Select(key => new KeyValuePair<string, JsonNode?>(key, input[key]?.DeepClone())));
    private static JsonObject LegacyProvider(JsonObject input)
    {
        var result = Copy(input, "name", "env");
        if (input.ContainsKey("npm")) result["package"] = "aisdk:" + input["npm"]!.GetValue<string>();
        var options = input.ContainsKey("options") ? input["options"]?.DeepClone() as JsonObject
            ?? throw new JsonException("Provider options must be an object.") : new JsonObject();
        foreach (var key in new[] { "headers", "body" })
        {
            if (options.ContainsKey(key)) result[key] = options[key]?.DeepClone();
            options.Remove(key);
        }
        if (input.ContainsKey("api")) options["baseURL"] = input["api"]?.DeepClone();
        if (input.ContainsKey("api") || input.ContainsKey("options")) result["settings"] = options;
        if (input.ContainsKey("models"))
        {
            if (input["models"] is not JsonObject models) throw new JsonException("Provider models must be an object.");
            var migrated = new JsonObject();
            foreach (var pair in models)
            {
                if (pair.Value is not JsonObject model) throw new JsonException("Provider model must be an object.");
                var value = Copy(model, "family", "name", "headers");
                if (model.ContainsKey("id")) value["modelID"] = model["id"]?.DeepClone();
                if (model["interleaved"] is JsonObject interleaved && interleaved["field"] is JsonValue field && field.TryGetValue<string>(out var reasoningField))
                    value["compatibility"] = new JsonObject { ["reasoningField"] = reasoningField };
                var settings = model.ContainsKey("options") ? model["options"]?.DeepClone() as JsonObject
                    ?? throw new JsonException("Model options must be an object.") : new JsonObject();
                var hasSettings = model.ContainsKey("options");
                if (model["provider"] is JsonObject transport)
                {
                    if (transport.ContainsKey("npm")) value["package"] = "aisdk:" + transport["npm"]!.GetValue<string>();
                    if (transport.ContainsKey("api")) { settings["baseURL"] = transport["api"]?.DeepClone(); hasSettings = true; }
                }
                if (hasSettings) value["settings"] = settings;
                var modalities = model["modalities"] as JsonObject;
                if (model.ContainsKey("tool_call") || modalities?.ContainsKey("input") == true || modalities?.ContainsKey("output") == true)
                    value["capabilities"] = new JsonObject
                    {
                        ["tools"] = model.ContainsKey("tool_call") ? model["tool_call"]?.DeepClone() : JsonValue.Create(true),
                        ["input"] = modalities?.ContainsKey("input") == true ? modalities["input"]?.DeepClone() : new JsonArray("text", "image"),
                        ["output"] = modalities?.ContainsKey("output") == true ? modalities["output"]?.DeepClone() : new JsonArray("text")
                    };
                if (model.ContainsKey("variants"))
                {
                    if (model["variants"] is not JsonObject variants) throw new JsonException("Legacy variants must be an object.");
                    value["variants"] = new JsonArray(variants.Select(variant => (JsonNode)new JsonObject
                        { ["id"] = variant.Key, ["settings"] = variant.Value?.DeepClone() }).ToArray());
                }
                if (model["cost"] is JsonObject cost)
                {
                    var costs = new JsonArray(Cost(cost));
                    if (cost["context_over_200k"] is JsonObject extended)
                    {
                        var tier = Cost(extended);
                        tier["tier"] = new JsonObject { ["type"] = "context", ["size"] = 200_000 };
                        costs.Add(tier);
                    }
                    value["cost"] = costs;
                }
                if (model["status"] is JsonValue status && status.TryGetValue<string>(out var name) && name == "deprecated") value["disabled"] = true;
                if (model["limit"] is JsonObject limits)
                {
                    var converted = new JsonObject();
                    foreach (var key in new[] { "context", "input", "output" })
                    {
                        if (!limits.ContainsKey(key)) continue;
                        var number = limits[key]!.GetValue<double>();
                        if (!double.IsFinite(number)) throw new JsonException("Model limit must be finite.");
                        converted[key] = Math.Clamp(Math.Truncate(number), -9_007_199_254_740_991d, 9_007_199_254_740_991d);
                    }
                    value["limit"] = converted;
                }
                migrated[pair.Key] = value;
            }
            result["models"] = migrated;
        }
        return result;

        static JsonObject Cost(JsonObject cost)
        {
            var cache = new JsonObject();
            if (cost.ContainsKey("cache_read")) cache["read"] = cost["cache_read"]?.DeepClone();
            if (cost.ContainsKey("cache_write")) cache["write"] = cost["cache_write"]?.DeepClone();
            var result = Copy(cost, "input", "output");
            result["cache"] = cache;
            return result;
        }
    }
}
