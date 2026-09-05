namespace OpenCode.Cli.Tui.Theme;

using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

public static class ThemeResolver
{
    public static ResolvedTheme Resolve(string json, ThemeMode requested = ThemeMode.Light) => Resolve(
        JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Theme document must be an object."), requested);

    public static ResolvedTheme Resolve(JsonObject source, ThemeMode requested = ThemeMode.Light)
    {
        var document = source["version"]?.GetValue<int>() switch
        {
            null or 1 => LegacyThemeMigration.Migrate(source),
            2 => source.DeepClone().AsObject(),
            _ => throw new NotSupportedException("Unsupported theme version.")
        };
        var modes = Modes(document);
        if (modes.Count == 0) throw new FormatException("Theme must provide at least one mode.");
        foreach (var available in modes)
            document[Key(available)] = ValidateDefinition(document[Key(available)] as JsonObject ?? throw new FormatException("Theme mode must be an object."));
        var mode = modes.Contains(requested) ? requested : modes[0];
        var selected = document[Key(mode)]!.AsObject();
        if (Merges(document["light"]) && Merges(document["dark"])) throw new FormatException("Light and dark themes cannot both merge modes.");
        var definition = Expand(selected);
        if (Merges(selected))
        {
            var other = document[mode == ThemeMode.Light ? "dark" : "light"] as JsonObject ?? throw new FormatException("mergeMode requires the other theme mode.");
            definition = Merge(Expand(ValidateDefinition(other)), definition);
            if (definition["hue"] is null) throw new FormatException("The other theme must provide hues when merging modes.");
        }
        var defaults = Expand(ThemeDefaults.Definition(mode));
        var standalone = document["standalone"]?.GetValue<bool>() ?? false;
        var merged = standalone ? Merge(Fallback(mode), definition) : Merge(Fallback(mode), defaults, definition);
        if (merged["hue"] is not JsonObject hueDefinition) throw new FormatException("Standalone themes must provide hues.");
        var hue = ResolveHues(hueDefinition);
        var categorical = (merged["categorical"] as JsonArray ?? new JsonArray(ThemeDefaults.Categories.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()))
            .Select(value => hue[value!.GetValue<string>()]).ToArray();
        var sources = new Dictionary<ThemeColor, ThemeHueSource>(ReferenceEqualityComparer.Instance);
        foreach (var pair in hue)
            foreach (var step in pair.Value) sources[step.Value] = new(pair.Key, step.Key);
        var tokens = OnlyTokens(merged);
        var basis = View(tokens);
        return new(mode, modes, basis, Context("elevated"), Context("overlay"));

        ThemeTokens Context(string name)
        {
            if (merged["@context:" + name] is not JsonObject context) return basis;
            var result = Merge(tokens, context);
            foreach (var area in new[] { "text", "background" })
                foreach (var variant in ThemeDefaults.Variants)
                {
                    var baseVariant = tokens[area]?["action"]?[variant] as JsonObject;
                    var contextVariant = context[area]?["action"]?[variant] as JsonObject;
                    var colors = new JsonObject();
                    foreach (var state in new[] { "default" }.Concat(ThemeDefaults.States.Select(state => "$" + state)))
                        colors[state] = (contextVariant?[state] ?? contextVariant?["default"] ?? baseVariant?[state] ?? baseVariant?["default"])?.DeepClone();
                    result[area]!["action"]![variant] = colors;
                }
            return View(result);
        }
        ThemeTokens View(JsonObject raw)
        {
            var cache = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
            var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
            foreach (var pair in Flatten(raw)) colors[ResolvedPath(pair.Key)] = Color(pair.Value, pair.Key, []);
            return new(mode, new ReadOnlyDictionary<string, ThemeColor>(colors), hue, Array.AsReadOnly(categorical), new ReadOnlyDictionary<ThemeColor, ThemeHueSource>(sources));

            ThemeColor Color(JsonNode? value, string path, HashSet<string> stack)
            {
                if (value is not JsonValue leaf || !leaf.TryGetValue<string>(out var text)) throw new FormatException($"Invalid theme value at {path}.");
                if (ThemeColor.IsHex(text) || text == "transparent") return ThemeColor.Parse(text);
                if (!text.StartsWith('$')) throw new FormatException($"Invalid theme color at {path}.");
                var target = text[1..];
                if (cache.TryGetValue(target, out var cached)) return cached;
                if (!stack.Add(target)) throw new FormatException($"Circular theme reference: {string.Join(" -> ", stack)} -> {target}");
                var parts = target.Split('.');
                ThemeColor resolved;
                if (parts.Length == 3 && parts[0] == "hue" && hue.TryGetValue(parts[1], out var scale)
                    && int.TryParse(parts[2], System.Globalization.CultureInfo.CurrentCulture, out var step) && scale.TryGetValue(step, out var color)) resolved = color;
                else
                {
                    JsonNode? node = raw;
                    foreach (var part in parts) node = node is JsonObject obj ? obj[part] : null;
                    if (node is null) throw new FormatException($"Theme reference ${target} was not found.");
                    resolved = Color(node, target, stack);
                }
                stack.Remove(target);
                cache[target] = resolved;
                return resolved;
            }
        }
    }

    public static IReadOnlyList<ThemeMode> Modes(JsonObject document)
    {
        if (Merges(document["light"]) && !document.ContainsKey("dark")) throw new FormatException("Light cannot merge without dark.");
        if (Merges(document["dark"]) && !document.ContainsKey("light")) throw new FormatException("Dark cannot merge without light.");
        return new[] { ThemeMode.Light, ThemeMode.Dark }.Where(mode => document.ContainsKey(Key(mode))).ToArray();
    }
    private static bool Merges(JsonNode? node) => node is JsonObject obj && obj["mergeMode"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;
    internal static string Key(ThemeMode mode) => mode switch { ThemeMode.Light => "light", ThemeMode.Dark => "dark", _ => throw new ArgumentOutOfRangeException(nameof(mode)) };

    internal static JsonObject Merge(params JsonObject[] values)
    {
        var result = new JsonObject();
        foreach (var value in values)
            foreach (var pair in value)
            {
                if (pair.Key == "mergeMode") continue;
                result[pair.Key] = pair.Value is JsonObject obj ? Merge(result[pair.Key] as JsonObject ?? new(), obj) : pair.Value?.DeepClone();
            }
        return result;
    }

    internal static JsonObject Expand(JsonObject input)
    {
        var result = input.DeepClone().AsObject();
        ExpandTokens(result);
        foreach (var pair in result.Where(pair => pair.Key.StartsWith("@context:", StringComparison.Ordinal)))
            if (pair.Value is JsonObject context) ExpandTokens(context);
        return result;
    }
    private static void ExpandTokens(JsonObject value)
    {
        if (value["text"] is JsonObject text)
        {
            if (text["default"] is not null && text["subdued"] is null) text["subdued"] = "$text.default";
            if (text["feedback"] is JsonObject feedback)
                foreach (var pair in feedback)
                    if (pair.Value is JsonObject item && item["default"] is not null && item["subdued"] is null) item["subdued"] = $"$text.feedback.{pair.Key}.default";
        }
        foreach (var area in new[] { "text", "background" })
        {
            if (value[area] is not JsonObject group) continue;
            if (group["formfield"] is JsonObject form) States(form, $"{area}.formfield");
            if (group["action"] is JsonObject actions)
                foreach (var pair in actions) if (pair.Value is JsonObject action) States(action, $"{area}.action.{pair.Key}");
        }
        static void States(JsonObject value, string path)
        {
            if (value["default"] is null) return;
            foreach (var state in ThemeDefaults.States) if (value["$" + state] is null) value["$" + state] = "$" + path + ".default";
        }
    }

    private static JsonObject Fallback(ThemeMode mode)
    {
        var result = OnlyTokens(ThemeDefaults.Definition(mode));
        foreach (var pair in Flatten(result).ToArray()) Set(result, pair.Key, "#ff0000");
        result["text"]!["status"] = ThemeDefaults.Definition(mode)["text"]!["status"]!.DeepClone();
        // Expansion of only default state values is significant for aliases in standalone themes.
        foreach (var area in new[] { "text", "background" })
        {
            foreach (var variant in ThemeDefaults.Variants) result[area]!["action"]![variant] = new JsonObject { ["default"] = "#ff0000" };
            result[area]!["formfield"] = new JsonObject { ["default"] = "#ff0000" };
        }
        result["text"]!.AsObject().Remove("subdued");
        foreach (var kind in ThemeDefaults.Feedback) result["text"]!["feedback"]![kind] = new JsonObject { ["default"] = "#ff0000" };
        return Expand(result);
    }

    private static JsonObject ValidateDefinition(JsonObject input)
    {
        var result = new JsonObject();
        var schema = OnlyTokens(Expand(ThemeDefaults.Definition(ThemeMode.Light)));
        foreach (var pair in input)
        {
            if (pair.Key == "mergeMode") { if (Merges(input)) result[pair.Key] = true; continue; }
            if (pair.Key == "categorical")
            {
                if (pair.Value is not JsonArray { Count: > 0 } categories || categories.Any(item => item is not JsonValue scalar
                    || !scalar.TryGetValue<string>(out var name) || !ThemeDefaults.HueNames.Contains(name, StringComparer.Ordinal))) throw new FormatException("Invalid categorical hue list.");
                result[pair.Key] = categories.DeepClone(); continue;
            }
            if (pair.Key == "hue")
            {
                if (pair.Value is not JsonObject hues) throw new FormatException("Hue must be an object.");
                var valid = new JsonObject();
                foreach (var hue in hues.Where(hue => ThemeDefaults.HueNames.Contains(hue.Key, StringComparer.Ordinal)))
                {
                    if (hue.Value is JsonValue alias && alias.TryGetValue<string>(out var reference) && reference.StartsWith("$hue.", StringComparison.Ordinal)
                        && ThemeDefaults.HueNames.Contains(reference[5..], StringComparer.Ordinal)) valid[hue.Key] = reference;
                    else
                    {
                        if (hue.Value is not JsonObject scale || scale.Count != 9) throw new FormatException($"Hue {hue.Key} requires all nine steps.");
                        for (var step = 100; step <= 900; step += 100)
                            if (scale[step.ToString(System.Globalization.CultureInfo.CurrentCulture)] is not JsonValue color || !color.TryGetValue<string>(out var hex) || !ThemeColor.IsHex(hex)) throw new FormatException($"Invalid hue {hue.Key}.{step}.");
                        valid[hue.Key] = scale.DeepClone();
                    }
                }
                result[pair.Key] = valid; continue;
            }
            if (pair.Key is "@context:elevated" or "@context:overlay") result[pair.Key] = ValidateTokens(pair.Value, schema, "");
            else if (schema.ContainsKey(pair.Key)) result[pair.Key] = ValidateTokens(pair.Value, schema[pair.Key]!, pair.Key);
        }
        return result;
    }

    private static JsonNode ValidateTokens(JsonNode? input, JsonNode schema, string path)
    {
        if (schema is JsonObject shape)
        {
            if (input is not JsonObject source) throw new FormatException($"Invalid theme object at {path}.");
            return new JsonObject(source.Where(pair => shape.ContainsKey(pair.Key)).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key,
                ValidateTokens(pair.Value, shape[pair.Key]!, path.Length == 0 ? pair.Key : path + "." + pair.Key))));
        }
        if (input is not JsonValue leaf || !leaf.TryGetValue<string>(out var value)) throw new FormatException($"Invalid theme color at {path}.");
        var hueOnly = path.StartsWith("syntax.", StringComparison.Ordinal) || path.StartsWith("markdown.", StringComparison.Ordinal);
        var parts = value.Split('.');
        var hueReference = parts.Length == 3 && parts[0] == "$hue" && ThemeDefaults.HueNames.Contains(parts[1], StringComparer.Ordinal)
            && int.TryParse(parts[2], System.Globalization.CultureInfo.CurrentCulture, out var step) && step is >= 100 and <= 900 && step % 100 == 0;
        if (!ThemeColor.IsHex(value) && !(hueOnly ? hueReference : value == "transparent" || value.StartsWith('$') && value.Length > 1))
            throw new FormatException($"Invalid theme color {value} at {path}.");
        return JsonValue.Create(value)!;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, ThemeColor>> ResolveHues(JsonObject definition)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<int, ThemeColor>>(StringComparer.Ordinal);
        foreach (var name in ThemeDefaults.HueNames) Scale(name, []);
        return new ReadOnlyDictionary<string, IReadOnlyDictionary<int, ThemeColor>>(result);
        IReadOnlyDictionary<int, ThemeColor> Scale(string name, HashSet<string> stack)
        {
            if (result.TryGetValue(name, out var cached)) return cached;
            if (!stack.Add(name)) throw new FormatException($"Circular hue reference: {name}.");
            var colors = new Dictionary<int, ThemeColor>();
            if (definition[name] is JsonValue alias && alias.TryGetValue<string>(out var reference))
            {
                if (!reference.StartsWith("$hue.", StringComparison.Ordinal)) throw new FormatException("Hue aliases must reference a scale.");
                foreach (var pair in Scale(reference[5..], stack)) colors[pair.Key] = pair.Value.Clone();
            }
            else if (definition[name] is JsonObject scale)
                for (var step = 100; step <= 900; step += 100) colors[step] = ThemeColor.Parse(scale[step.ToString(System.Globalization.CultureInfo.CurrentCulture)]?.GetValue<string>() ?? throw new FormatException($"Missing hue {name}.{step}."));
            else throw new FormatException($"Hue {name} was not found.");
            stack.Remove(name);
            return result[name] = new ReadOnlyDictionary<int, ThemeColor>(colors);
        }
    }

    private static JsonObject OnlyTokens(JsonObject input) => new(ThemeDefaults.TokenRoots.Where(input.ContainsKey)
        .Select(key => new KeyValuePair<string, JsonNode?>(key, input[key]?.DeepClone())));
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> Flatten(JsonObject obj, string prefix = "") => obj.SelectMany(pair =>
        pair.Value is JsonObject nested ? Flatten(nested, prefix + pair.Key + ".") : [new KeyValuePair<string, JsonNode?>(prefix + pair.Key, pair.Value)]);
    internal static void Set(JsonObject obj, string path, string value)
    {
        var parts = path.Split('.');
        foreach (var part in parts[..^1]) { obj[part] ??= new JsonObject(); obj = obj[part]!.AsObject(); }
        obj[parts[^1]] = value;
    }
    private static string ResolvedPath(string path) => string.Join('.', path.Split('.').Select(part => part.StartsWith('$') && ThemeDefaults.States.Contains(part[1..], StringComparer.Ordinal) ? part[1..] : part));
}
