namespace OpenCode.Cli.Tui.Theme;

using System.Text.Json.Nodes;

/// <summary>Source v1-migrate.ts: resolve V1 references, infer hue scales in OKLCH,
/// and project legacy colors into the real V2 semantic roles.</summary>
public static class LegacyThemeMigration
{
    public static JsonObject Migrate(JsonObject source)
    {
        var light = Resolve(source, ThemeMode.Light);
        var dark = Resolve(source, ThemeMode.Dark);
        var result = new JsonObject { ["version"] = 2, ["standalone"] = true };
        var sameBackground = light["background"].Alpha > 0 && dark["background"].Alpha > 0 && light["background"].SameValue(dark["background"]);
        var lightMode = light["text"].Luminance > light["background"].Luminance ? ThemeMode.Dark : ThemeMode.Light;
        var darkMode = dark["text"].Luminance > dark["background"].Luminance ? ThemeMode.Dark : ThemeMode.Light;
        var selected = source["theme"]!.AsObject().ContainsKey("selectedListItemText");
        if (sameBackground && lightMode == darkMode)
        {
            result[ThemeResolver.Key(lightMode)] = Mode(lightMode == ThemeMode.Light ? light : dark, lightMode, selected);
            return result;
        }
        result["light"] = Mode(light, ThemeMode.Light, selected);
        result["dark"] = Mode(dark, ThemeMode.Dark, selected);
        return result;
    }

    private static Dictionary<string, ThemeColor> Resolve(JsonObject source, ThemeMode mode)
    {
        var theme = source["theme"] as JsonObject ?? throw new FormatException("V1 theme must provide a theme object.");
        var definitions = source["defs"] as JsonObject ?? new JsonObject();
        var result = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
        foreach (var pair in theme.Where(pair => pair.Key is not ("selectedListItemText" or "backgroundMenu" or "thinkingOpacity")))
            result[pair.Key] = Color(pair.Value, []);
        result["selectedListItemText"] = theme.ContainsKey("selectedListItemText") ? Color(theme["selectedListItemText"], []) : result["background"];
        result["backgroundMenu"] = theme.ContainsKey("backgroundMenu") ? Color(theme["backgroundMenu"], []) : result["backgroundElement"];
        return result;
        ThemeColor Color(JsonNode? value, HashSet<string> stack)
        {
            if (value is JsonValue scalar)
            {
                if (scalar.TryGetValue<int>(out var index)) return ThemeColor.Ansi(index);
                if (scalar.TryGetValue<string>(out var text))
                {
                    if (text is "transparent" or "none" || text.StartsWith('#')) return ThemeColor.Parse(text);
                    if (!stack.Add(text)) throw new FormatException($"Circular V1 color reference: {text}");
                    var resolved = Color(definitions[text] ?? theme[text] ?? throw new FormatException($"V1 color reference not found: {text}"), stack);
                    stack.Remove(text);
                    return resolved;
                }
            }
            if (value is JsonObject variant && variant.ContainsKey(ThemeResolver.Key(mode))) return Color(variant[ThemeResolver.Key(mode)], stack);
            throw new FormatException("Invalid V1 theme color.");
        }
    }

    private static JsonObject Mode(Dictionary<string, ThemeColor> theme, ThemeMode mode, bool hasSelected)
    {
        var byHue = new Dictionary<string, (ThemeColor Color, double Distance)>(StringComparer.Ordinal);
        var byToken = new Dictionary<string, string>(StringComparer.Ordinal);
        var standards = ThemeDefaults.Hues();
        foreach (var token in new[] { "accent", "success", "warning", "primary", "error", "info", "secondary" }) Infer(token, false);
        Infer("accent", true); Infer("primary", true);
        var hues = new JsonObject { ["gray"] = Neutral(theme, mode) };
        foreach (var name in ThemeDefaults.Chromatic) hues[name] = byHue.TryGetValue(name, out var match) ? Scale(match.Color, mode) : JsonValue.Create("$hue.gray");
        hues["accent"] = byToken.TryGetValue("accent", out var accent) ? "$hue." + accent : "$hue.gray";
        hues["interactive"] = byToken.TryGetValue("primary", out var interactive) ? "$hue." + interactive : "$hue.gray";
        hues["neutral"] = "$hue.gray";
        var categories = new[] { "secondary", "accent", "success", "warning", "primary", "error" }
            .Where(byToken.ContainsKey).Select(token => byToken[token]).Distinct(StringComparer.Ordinal).ToArray();
        var result = new JsonObject { ["hue"] = hues,
            ["categorical"] = new JsonArray((categories.Length > 0 ? categories : ThemeDefaults.Categories).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) };
        var light = mode == ThemeMode.Light;
        var text = light ? "$hue.neutral.800" : "$hue.neutral.200";
        var subdued = light ? "$hue.neutral.600" : "$hue.neutral.400";
        var primary = light ? "$hue.interactive.800" : "$hue.interactive.200";
        var panel = light ? "$hue.neutral.300" : "$hue.neutral.700";
        Set("text.default", text); Set("text.subdued", subdued);
        Set("text.action.primary.default", "$text.default"); Set("text.action.primary.$disabled", subdued);
        Set("text.action.primary.$focused", Selected(theme["primary"]).Hex); Set("text.action.primary.$selected", primary);
        Set("text.action.secondary.default", "$text.subdued"); Set("text.action.secondary.$hovered", "$text.default");
        Set("text.action.destructive.default", Selected(theme["error"]).Hex); Set("text.action.destructive.$disabled", subdued);
        Set("text.formfield.default", text);
        foreach (var state in ThemeDefaults.States) Set("text.formfield.$" + state, state == "disabled" ? subdued : primary);
        Set("background.default", light ? "$hue.neutral.200" : "$hue.neutral.800");
        Set("background.surface.offset", panel); Set("background.surface.overlay", light ? "$hue.neutral.400" : "$hue.neutral.600");
        Set("background.action.primary.default", "transparent"); Set("background.action.primary.$hovered", panel);
        Set("background.action.primary.$focused", primary); Set("background.action.primary.$selected", "transparent");
        Set("background.action.secondary.default", "transparent"); Set("background.action.destructive.default", theme["error"].Hex);
        Set("background.formfield.default", "$background.default");
        foreach (var kind in ThemeDefaults.Feedback)
        {
            Set($"text.feedback.{kind}.default", theme[kind].Hex);
            Set($"background.feedback.{kind}.default", "$background.default");
        }
        Set("border.default", theme["border"].Hex); Set("scrollbar.default", theme["borderActive"].Hex);
        foreach (var (path, key) in new (string, string)[] {
            ("diff.text.added","diffAdded"),("diff.text.removed","diffRemoved"),("diff.text.context","diffContext"),("diff.text.hunkHeader","diffHunkHeader"),
            ("diff.background.added","diffAddedBg"),("diff.background.removed","diffRemovedBg"),("diff.background.context","diffContextBg"),
            ("diff.highlight.added","diffHighlightAdded"),("diff.highlight.removed","diffHighlightRemoved"),("diff.lineNumber.text","diffLineNumber"),
            ("diff.lineNumber.background.added","diffAddedLineNumberBg"),("diff.lineNumber.background.removed","diffRemovedLineNumberBg") }) Set(path, theme[key].Hex);
        foreach (var token in new[] { "comment", "keyword", "function", "variable", "string", "number", "type", "operator", "punctuation" })
            Set("syntax." + token, theme["syntax" + char.ToUpperInvariant(token[0]) + token[1..]].Hex);
        foreach (var token in new[] { "text", "heading", "link", "linkText", "code", "blockQuote", "emphasis", "strong", "horizontalRule", "listItem", "listEnumeration", "image", "imageText", "codeBlock" })
            Set("markdown." + token, theme[token == "emphasis" ? "markdownEmph" : "markdown" + char.ToUpperInvariant(token[0]) + token[1..]].Hex);
        Set("@context:elevated.background.default", "$background.surface.offset");
        Set("@context:elevated.background.action.primary.$hovered", "$background.surface.overlay");
        Set("@context:overlay.background.default", "$background.surface.overlay");
        ReferenceHues(result);
        return result;

        void Set(string path, string value) => ThemeResolver.Set(result, path, value);
        ThemeColor Selected(ThemeColor background) => hasSelected ? theme["selectedListItemText"] : theme["background"].Alpha != 0
            ? theme["background"] : ThemeColor.Parse(background.Luminance > .5 ? "#000000" : "#ffffff");
        void Infer(string token, bool replace)
        {
            var color = theme[token]; var oklch = ThemeOklch.FromColor(color);
            if (color.Alpha == 0 || oklch.C < .03) return;
            var anchor = oklch.L >= .6 ? "300" : "700";
            var nearest = ThemeDefaults.Chromatic.Select(name =>
            {
                var difference = Math.Abs(oklch.H - ThemeOklch.FromColor(ThemeColor.Parse(standards[name]![anchor]!.GetValue<string>())).H);
                return (Name: name, Distance: Math.Min(difference, 360 - difference));
            }).OrderBy(item => item.Distance).First();
            byToken[token] = nearest.Name;
            if (replace || !byHue.TryGetValue(nearest.Name, out var prior) || prior.Distance > nearest.Distance) byHue[nearest.Name] = (color, nearest.Distance);
        }
    }

    private static JsonObject Scale(ThemeColor color, ThemeMode mode)
    {
        var source = ThemeOklch.FromColor(color);
        var light = mode == ThemeMode.Light;
        var anchor = light ? 800 : 200;
        var endpoint = light ? Math.Max(.97, source.L) : Math.Min(.18, source.L);
        var result = new JsonObject();
        for (var step = 100; step <= 900; step += 100)
        {
            var progress = light ? (anchor - step) / (double)(anchor - 100) : (step - anchor) / (double)(900 - anchor);
            result[step.ToString(System.Globalization.CultureInfo.CurrentCulture)] = step == anchor ? color.Hex : new ThemeOklch(source.L + (endpoint - source.L) * progress,
                source.C * (1 - progress * .5), source.H).ToColor(color.Alpha).Hex;
        }
        return result;
    }

    private static JsonObject Neutral(Dictionary<string, ThemeColor> theme, ThemeMode mode)
    {
        var anchors = new[] { (Step: 200, Color: theme["background"]), (Step: 300, Color: theme["backgroundPanel"]),
            (Step: 400, Color: theme["backgroundElement"]), (Step: 600, Color: theme["textMuted"]), (Step: 800, Color: theme["text"]) };
        if (mode == ThemeMode.Dark) anchors = anchors.Reverse().Select(item => (1000 - item.Step, item.Color)).ToArray();
        var result = new JsonObject();
        for (var step = 100; step <= 900; step += 100)
        {
            var exact = Array.FindIndex(anchors, item => item.Step == step);
            if (exact >= 0) { result[step.ToString(System.Globalization.CultureInfo.CurrentCulture)] = anchors[exact].Color.Hex; continue; }
            var lower = step < anchors[0].Step ? 0 : step > anchors[^1].Step ? anchors.Length - 2 : Array.FindLastIndex(anchors, item => item.Step < step);
            var first = anchors[lower]; var second = anchors[lower + 1];
            var amount = (step - first.Step) / (double)(second.Step - first.Step);
            var start = ThemeOklch.FromColor(first.Color); var end = ThemeOklch.FromColor(second.Color);
            var hue = ((((end.H - start.H) % 360) + 540) % 360) - 180;
            result[step.ToString(System.Globalization.CultureInfo.CurrentCulture)] = new ThemeOklch(start.L + (end.L - start.L) * amount, start.C + (end.C - start.C) * amount, start.H + hue * amount)
                .ToColor(ThemeColor.Byte(first.Color.Alpha + (second.Color.Alpha - first.Color.Alpha) * amount)).Hex;
        }
        return result;
    }

    private static void ReferenceHues(JsonObject theme)
    {
        var hues = theme["hue"]!.AsObject();
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ThemeDefaults.Chromatic.Concat(["gray", "accent", "interactive", "neutral"]))
        {
            var value = hues[name];
            while (value is JsonValue alias) value = hues[alias.GetValue<string>()[5..]];
            foreach (var pair in value!.AsObject())
            {
                var color = pair.Value!.GetValue<string>();
                if (name is "accent" or "interactive" or "neutral" || !references.ContainsKey(color)) references[color] = $"$hue.{name}.{pair.Key}";
            }
        }
        foreach (var root in theme.Where(pair => pair.Key is not ("hue" or "categorical")).ToArray())
            if (root.Value is JsonObject tokens)
                foreach (var pair in ThemeResolver.Flatten(tokens).ToArray())
                    if (pair.Value is JsonValue color && color.TryGetValue<string>(out var value) && references.TryGetValue(value, out var reference)) ThemeResolver.Set(tokens, pair.Key, reference);
    }
}
