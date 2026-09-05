namespace OpenCode.Core.Llm;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>The bundled public models-dev floor. No network, credentials, database cache, or provider activation occurs here.</summary>
internal static class ModelsDevCatalog
{
    private const string Resource = "OpenCode.Core.ModelsDev.PublicSnapshot.json";
    private static readonly Lazy<JsonObject> Bundled = new(() =>
    {
        using var stream = typeof(ModelsDevCatalog).Assembly.GetManifestResourceStream(Resource)
            ?? throw new CatalogMetadataUnavailableException("The embedded public model snapshot is missing.");
        var resource = JsonNode.Parse(stream)!.AsObject();
        return Normalize(resource["catalog"]!.AsObject());
    });

    internal static JsonObject Load()
    {
        // Mirrors the explicit file override before the bundled floor. Loading this
        // method is application behavior; the import build target never reads it.
        var file = Environment.GetEnvironmentVariable("OPENCODE_MODELS_PATH");
        if (!string.IsNullOrEmpty(file))
        {
            JsonObject? source = null;
            try { source = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
            if (source is not null) return Normalize(source);
        }
        return Bundled.Value.DeepClone().AsObject();
    }

    private static JsonObject Normalize(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var item in source.Select(pair => pair.Value!.AsObject()))
        {
            var id = Required(item, "id");
            if (id is "azure-cognitive-services" or "google-vertex-anthropic") continue;
            var npm = Required(item, "npm");
            var env = item["env"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            var names = id == "azure" ? env.Where(name => name.EndsWith("_API_KEY", StringComparison.Ordinal)).Append("AZURE_COGNITIVE_SERVICES_API_KEY")
                : id == "google-vertex" ? ["GOOGLE_VERTEX_API_KEY"] : env.AsEnumerable();
            var provider = new JsonObject
            {
                ["name"] = Required(item, "name"), ["package"] = Package(npm),
                ["env"] = new JsonArray(names.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            };
            if (item["api"]?.GetValue<string>() is { Length: > 0 } api) provider["settings"] = new JsonObject { ["baseURL"] = api };
            var models = new JsonObject();
            foreach (var model in item["models"]!.AsObject().Select(pair => pair.Value!.AsObject()))
            {
                if (model["status"]?.GetValue<string>() == "deprecated") continue;
                var modelId = Required(model, "id");
                var variants = Variants(model["provider"]?["npm"]?.GetValue<string>() ?? npm, model);
                var info = ModelInfo(model, Required(model, "name"), variants, Cost(model["cost"] as JsonObject));
                models[modelId] = info;
                if (model["experimental"]?["modes"] is not JsonObject modes) continue;
                foreach (var (mode, options) in modes)
                {
                    var modeId = modelId + "-" + mode;
                    var name = Required(model, "name") + " " + (mode.Length == 0 ? "" : mode[..1].ToUpperInvariant() + mode[1..]);
                    var variant = ModelInfo(model, name, variants, MergeCost(info["cost"]!.AsArray(), options?["cost"] as JsonObject));
                    if (options?["provider"]?["headers"] is { } headers) variant["headers"] = headers.DeepClone();
                    if (options?["provider"]?["body"] is { } body) variant["body"] = body.DeepClone();
                    models[modeId] = variant;
                }
            }
            provider["models"] = models;
            result[id] = provider;
        }
        return result;
    }

    private static JsonObject ModelInfo(JsonObject source, string name, JsonArray variants, JsonArray cost)
    {
        var limit = source["limit"]!.AsObject();
        var limits = new JsonObject
        {
            ["context"] = CatalogNumbers.Integer(limit["context"]!), ["output"] = CatalogNumbers.Integer(limit["output"]!)
        };
        if (limit["input"] is { } input) limits["input"] = CatalogNumbers.Integer(input);
        var result = new JsonObject
        {
            ["modelID"] = Required(source, "id"), ["name"] = name,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = source["tool_call"]!.GetValue<bool>(),
                ["input"] = source["modalities"]?["input"]?.DeepClone() ?? new JsonArray(),
                ["output"] = source["modalities"]?["output"]?.DeepClone() ?? new JsonArray()
            },
            ["variants"] = variants.DeepClone(), ["cost"] = cost,
            ["time"] = new JsonObject { ["released"] = DateTimeOffset.TryParse(source["release_date"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time) ? time.ToUnixTimeMilliseconds() : 0 },
            ["status"] = source["status"]?.DeepClone() ?? JsonValue.Create("active"), ["disabled"] = false, ["limit"] = limits
        };
        if (source["family"]?.GetValue<string>() is { Length: > 0 } family) result["family"] = family;
        if (source["provider"]?["npm"]?.GetValue<string>() is { Length: > 0 } npm) result["package"] = Package(npm);
        if (source["provider"]?["api"]?.GetValue<string>() is { Length: > 0 } api) result["settings"] = new JsonObject { ["baseURL"] = api };
        var interleaved = source["interleaved"];
        var field = interleaved is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text
            : interleaved is JsonObject value ? value["field"]?.GetValue<string>() : null;
        if (field is not null) result["compatibility"] = new JsonObject { ["reasoningField"] = field };
        return result;
    }

    private static JsonArray Cost(JsonObject? source)
    {
        var result = new JsonArray { Price(source) };
        if (source?["tiers"] is JsonArray tiers)
            foreach (var item in tiers)
            {
                var price = Price(item!.AsObject());
                price["tier"] = item["tier"]!.DeepClone();
                result.Add(price);
            }
        if (source?["context_over_200k"] is JsonObject over)
        {
            var price = Price(over);
            price["tier"] = new JsonObject { ["type"] = "context", ["size"] = 200_000 };
            result.Add(price);
        }
        return result;
    }

    private static JsonObject Price(JsonObject? source) => new()
    {
        ["input"] = source?["input"]?.DeepClone() ?? JsonValue.Create(0),
        ["output"] = source?["output"]?.DeepClone() ?? JsonValue.Create(0),
        ["cache"] = new JsonObject
        {
            ["read"] = source?["cache_read"]?.DeepClone() ?? JsonValue.Create(0),
            ["write"] = source?["cache_write"]?.DeepClone() ?? JsonValue.Create(0)
        }
    };

    private static JsonArray MergeCost(JsonArray baseline, JsonObject? source)
    {
        if (source is null) return baseline.DeepClone().AsArray();
        var result = baseline.DeepClone().AsArray();
        var overlay = Cost(source);
        result[0] = overlay[0]!.DeepClone();
        foreach (var item in overlay.Skip(1))
        {
            var existing = result.Skip(1).FirstOrDefault(value => JsonNode.DeepEquals(value?["tier"], item?["tier"]));
            if (existing is null) result.Add(item!.DeepClone());
            else result[result.IndexOf(existing)] = item!.DeepClone();
        }
        return result;
    }

    private static JsonArray Variants(string npm, JsonObject model)
    {
        if (model["reasoning_options"] is not JsonArray options || options.Count == 0) return new JsonArray();
        var modelId = Required(model, "id");
        var toggle = options.Any(option => option?["type"]?.GetValue<string>() == "toggle");
        var effort = options.FirstOrDefault(option => option?["type"]?.GetValue<string>() == "effort");
        var result = new JsonObject();
        void Add(string id, JsonObject? settings)
        {
            if (settings is not null) result[id] = new JsonObject { ["id"] = id, ["settings"] = settings };
        }
        if (effort is not null)
        {
            if (toggle) Add("none", Toggle(npm, modelId, false));
            foreach (var value in effort["values"]!.AsArray())
            {
                if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var id) || id == "null" || id == "none" && result.ContainsKey("none")) continue;
                Add(id, Effort(npm, modelId, id));
            }
        }
        else if (options.FirstOrDefault(option => option?["type"]?.GetValue<string>() == "budget_tokens") is { } budget)
        {
            if (toggle) Add("none", Toggle(npm, modelId, false));
            var maximum = Math.Min(Math.Min(budget["max"] is { } max ? CatalogNumbers.Finite(max) : 31_999, CatalogNumbers.Finite(model["limit"]!["output"]!) - 1), 31_999);
            if (maximum > 0)
            {
                Add("high", Budget(npm, modelId, Math.Min(Math.Max(budget["min"] is { } min ? CatalogNumbers.Finite(min) : 0, Math.Floor((maximum + 1) / 2)), maximum)));
                Add("max", Budget(npm, modelId, maximum));
            }
        }
        else if (toggle) { Add("none", Toggle(npm, modelId, false)); Add("thinking", Toggle(npm, modelId, true)); }
        return new JsonArray(result.Select(pair => pair.Value!.DeepClone()).ToArray());
    }

    private static JsonObject? Effort(string npm, string id, string effort)
    {
        if (npm == "@ai-sdk/gateway") return Effort(Gateway(id) ?? "@ai-sdk/openai-compatible", id, effort);
        if (npm == "@openrouter/ai-sdk-provider") return new() { ["reasoning"] = new JsonObject { ["effort"] = effort } };
        if (npm is "@ai-sdk/google" or "@ai-sdk/google-vertex") return new() { ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true, ["thinkingLevel"] = effort } };
        if (npm is "@ai-sdk/anthropic" or "@ai-sdk/google-vertex/anthropic")
        {
            var result = new JsonObject { ["effort"] = effort };
            if (!ManualThinking(id)) result["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
            return result;
        }
        if (npm == "@ai-sdk/amazon-bedrock")
        {
            var reasoning = new JsonObject { ["maxReasoningEffort"] = effort };
            if (!id.Contains("anthropic", StringComparison.Ordinal)) reasoning["type"] = "enabled";
            else if (!ManualThinking(id)) { reasoning["type"] = "adaptive"; reasoning["display"] = "summarized"; }
            return new() { ["reasoningConfig"] = reasoning };
        }
        if (npm == "@ai-sdk/github-copilot" && id.Contains("gemini", StringComparison.Ordinal)) return null;
        if (npm is "@ai-sdk/openai" or "@ai-sdk/amazon-bedrock/mantle" or "@ai-sdk/azure"
            || npm == "@ai-sdk/github-copilot" && !id.Contains("claude", StringComparison.Ordinal))
            return new() { ["reasoningEffort"] = effort, ["reasoningSummary"] = "auto", ["include"] = new JsonArray(JsonValue.Create("reasoning.encrypted_content")) };
        if (npm == "@jerome-benoit/sap-ai-provider-v2")
        {
            if (id.Contains("anthropic", StringComparison.Ordinal))
            {
                var fields = new JsonObject { ["output_config"] = new JsonObject { ["effort"] = effort } };
                if (!ManualThinking(id)) fields["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
                return new() { ["modelParams"] = new JsonObject { ["additionalModelRequestFields"] = fields } };
            }
            if (id.Contains("gemini", StringComparison.Ordinal)) return new() { ["modelParams"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true, ["thinkingLevel"] = effort } } };
            if (id.Contains("amazon--nova", StringComparison.Ordinal)) return new() { ["modelParams"] = new JsonObject { ["additionalModelRequestFields"] = new JsonObject { ["output_config"] = new JsonObject { ["effort"] = effort } } } };
            return new() { ["modelParams"] = new JsonObject { ["reasoning_effort"] = effort } };
        }
        return npm is "@ai-sdk/openai-compatible" or "@ai-sdk/xai" or "@ai-sdk/mistral" or "@ai-sdk/groq" or "@ai-sdk/cerebras"
            or "@ai-sdk/deepinfra" or "@ai-sdk/togetherai" or "venice-ai-sdk-provider" or "ai-gateway-provider" or "@ai-sdk/github-copilot"
            ? new JsonObject { ["reasoningEffort"] = effort } : null;
    }

    private static JsonObject? Toggle(string npm, string id, bool enabled)
    {
        if (npm == "@ai-sdk/gateway" && Gateway(id) is { } upstream) return Toggle(upstream, id, enabled);
        if (npm is "@ai-sdk/gateway" or "@openrouter/ai-sdk-provider") return new() { ["reasoning"] = new JsonObject { ["enabled"] = enabled } };
        if (npm is "@ai-sdk/google" or "@ai-sdk/google-vertex") return new() { ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = enabled, ["thinkingBudget"] = enabled ? -1 : 0 } };
        if (npm is "@ai-sdk/anthropic" or "@ai-sdk/google-vertex/anthropic") return new() { ["thinking"] = Thinking(enabled) };
        if (npm == "@ai-sdk/amazon-bedrock") return new() { ["additionalModelRequestFields"] = id.Contains("anthropic", StringComparison.Ordinal)
            ? new JsonObject { ["thinking"] = Thinking(enabled) } : new JsonObject { ["reasoningConfig"] = new JsonObject { ["type"] = enabled ? "enabled" : "disabled" } } };
        if (npm == "@ai-sdk/alibaba") return new() { ["enableThinking"] = enabled };
        if (npm == "@ai-sdk/cohere") return new() { ["thinking"] = new JsonObject { ["type"] = enabled ? "enabled" : "disabled" } };
        if (npm != "@jerome-benoit/sap-ai-provider-v2") return null;
        if (id.Contains("gemini", StringComparison.Ordinal)) return new() { ["modelParams"] = Toggle("@ai-sdk/google", id, enabled) };
        if (id.Contains("cohere", StringComparison.Ordinal)) return new() { ["modelParams"] = Toggle("@ai-sdk/cohere", id, enabled) };
        if (id.Contains("amazon--nova", StringComparison.Ordinal)) return new() { ["modelParams"] = new JsonObject { ["additionalModelRequestFields"] = Toggle("@ai-sdk/cohere", id, enabled) } };
        if (id.Contains("anthropic", StringComparison.Ordinal)) return new() { ["modelParams"] = new JsonObject { ["additionalModelRequestFields"] = new JsonObject { ["thinking"] = Thinking(enabled) } } };
        return null;
    }

    private static JsonObject Thinking(bool enabled) => enabled ? new() { ["type"] = "adaptive", ["display"] = "summarized" } : new() { ["type"] = "disabled" };

    private static JsonObject? Budget(string npm, string id, double budget)
    {
        if (npm == "@ai-sdk/gateway") return Budget(Gateway(id) ?? "@openrouter/ai-sdk-provider", id, budget);
        if (npm == "@openrouter/ai-sdk-provider") return new() { ["reasoning"] = new JsonObject { ["max_tokens"] = budget } };
        if (npm is "@ai-sdk/google" or "@ai-sdk/google-vertex") return new() { ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true, ["thinkingBudget"] = budget } };
        if (npm is "@ai-sdk/anthropic" or "@ai-sdk/google-vertex/anthropic") return new() { ["thinking"] = new JsonObject { ["type"] = "enabled", ["budgetTokens"] = budget } };
        if (npm == "@ai-sdk/amazon-bedrock") return new() { ["reasoningConfig"] = new JsonObject { ["type"] = "enabled", ["budgetTokens"] = budget } };
        if (npm == "@ai-sdk/cohere") return new() { ["thinking"] = new JsonObject { ["type"] = "enabled", ["tokenBudget"] = budget } };
        if (npm == "@ai-sdk/alibaba") return new() { ["enableThinking"] = true, ["thinkingBudget"] = budget };
        if (npm != "@jerome-benoit/sap-ai-provider-v2") return null;
        if (id.Contains("anthropic", StringComparison.Ordinal)) return new() { ["modelParams"] = new JsonObject { ["additionalModelRequestFields"] = new JsonObject { ["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget } } } };
        if (id.Contains("gemini", StringComparison.Ordinal)) return new() { ["modelParams"] = Budget("@ai-sdk/google", id, budget) };
        if (id.Contains("cohere", StringComparison.Ordinal)) return new() { ["modelParams"] = new JsonObject { ["thinking"] = new JsonObject { ["type"] = "enabled", ["token_budget"] = budget } } };
        return null;
    }

    private static string? Gateway(string id) => id.IndexOf('/') <= 0 ? null : id[..id.IndexOf('/')] switch
    { "anthropic" => "@ai-sdk/anthropic", "google" => "@ai-sdk/google", "amazon" => "@ai-sdk/amazon-bedrock", "alibaba" => "@ai-sdk/alibaba", _ => null };

    private static bool ManualThinking(string id)
    {
        var family = Regex.Match(id, @"(?:claude-)?(?:opus|sonnet|haiku)-(\d+)(?:[.-](\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var version = family.Success ? family : Regex.Match(id, @"claude-(\d+)(?:[.-](\d+))?-(?:opus|sonnet|haiku)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!version.Success || !double.TryParse(version.Groups[1].Value, CultureInfo.InvariantCulture, out var major)) return false;
        var minor = double.TryParse(version.Groups[2].Value, CultureInfo.InvariantCulture, out var parsed) && parsed <= 9 ? parsed : 0;
        return major < 4 || major == 4 && minor < 6;
    }

    private static string Required(JsonObject value, string key) => value[key]?.GetValue<string>() ?? throw new JsonException("Required public model metadata is missing.");
    private static string Package(string npm) => npm.StartsWith("aisdk:", StringComparison.Ordinal) ? npm : "aisdk:" + npm;
}
