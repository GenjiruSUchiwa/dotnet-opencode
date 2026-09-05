namespace OpenCode.Cli.Tui.Theme;

using System.Text.Json.Nodes;

/// <summary>Exact semantic defaults from packages/theme/src/tui/defaults.ts.
/// These are fallback tokens, NOT the production "opencode" palette.</summary>
public static class ThemeDefaults
{
    internal static readonly string[] HueNames = ["gray", "red", "orange", "yellow", "green", "cyan", "blue", "purple", "accent", "interactive", "neutral"];
    internal static readonly string[] Chromatic = ["red", "orange", "yellow", "green", "cyan", "blue", "purple"];
    internal static readonly string[] States = ["disabled", "pressed", "focused", "selected", "hovered"];
    internal static readonly string[] Variants = ["primary", "secondary", "destructive"];
    internal static readonly string[] Feedback = ["error", "warning", "success", "info"];
    internal static readonly string[] Categories = ["blue", "purple", "green", "orange", "red", "cyan"];
    internal static readonly string[] TokenRoots = ["text", "background", "border", "scrollbar", "diff", "syntax", "markdown"];
    public static JsonObject Document() => new() { ["version"] = 2, ["light"] = Definition(ThemeMode.Light), ["dark"] = Definition(ThemeMode.Dark) };

    public static JsonObject Definition(ThemeMode mode)
    {
        var tokens = JsonNode.Parse(mode == ThemeMode.Light ? Light : Dark)!.AsObject();
        tokens["hue"] = Hues();
        tokens["categorical"] = new JsonArray(Categories.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return tokens;
    }

    internal static JsonObject Hues()
    {
        string[][] values =
        [
            ["#f3f4f6", "#e5e7eb", "#d1d5db", "#9ca3af", "#6b7280", "#4b5563", "#374151", "#1f2937", "#111827"],
            ["#fee2e2", "#fecaca", "#fca5a5", "#f87171", "#ef4444", "#dc2626", "#b91c1c", "#991b1b", "#7f1d1d"],
            ["#ffedd5", "#fed7aa", "#fdba74", "#fb923c", "#f97316", "#ea580c", "#c2410c", "#9a3412", "#7c2d12"],
            ["#fef9c3", "#fef08a", "#fde047", "#facc15", "#eab308", "#ca8a04", "#a16207", "#854d0e", "#713f12"],
            ["#dcfce7", "#bbf7d0", "#86efac", "#4ade80", "#22c55e", "#16a34a", "#15803d", "#166534", "#14532d"],
            ["#cffafe", "#a5f3fc", "#67e8f9", "#22d3ee", "#06b6d4", "#0891b2", "#0e7490", "#155e75", "#164e63"],
            ["#dbeafe", "#bfdbfe", "#93c5fd", "#60a5fa", "#3b82f6", "#2563eb", "#1d4ed8", "#1e40af", "#1e3a8a"],
            ["#f3e8ff", "#e9d5ff", "#d8b4fe", "#c084fc", "#a855f7", "#9333ea", "#7e22ce", "#6b21a8", "#581c87"]
        ];
        var result = new JsonObject();
        for (var hue = 0; hue < values.Length; hue++)
            result[HueNames[hue]] = new JsonObject(values[hue].Select((value, index) => new KeyValuePair<string, JsonNode?>(((index + 1) * 100).ToString(System.Globalization.CultureInfo.CurrentCulture), JsonValue.Create(value))));
        result["accent"] = "$hue.blue"; result["interactive"] = "$hue.blue"; result["neutral"] = "$hue.gray";
        return result;
    }

    private const string Light = """
    {
      "text": {
        "default":"$hue.neutral.800","subdued":"$hue.neutral.600",
        "action": {
          "primary":{"default":"$hue.neutral.200","$disabled":"$hue.neutral.500"},
          "secondary":{"default":"$text.subdued","$hovered":"$text.default"},
          "destructive":{"default":"$hue.red.200","$disabled":"$hue.neutral.500"}},
        "formfield":{"default":"$hue.neutral.800","$focused":"$text.action.primary.default","$pressed":"$hue.neutral.200","$disabled":"$hue.neutral.500","$selected":"$hue.interactive.700"},
        "status":{"running":"$hue.interactive.800","question":"$text.status.unread","permission":"$text.status.unread","unread":"$hue.accent.800"},
        "feedback":{"error":{"default":"$hue.red.700","subdued":"$hue.red.600"},"warning":{"default":"$hue.yellow.800","subdued":"$hue.yellow.700"},"success":{"default":"$hue.green.700","subdued":"$hue.green.600"},"info":{"default":"$hue.cyan.700","subdued":"$hue.cyan.600"}}},
      "background": {
        "default":"$hue.neutral.200","surface":{"offset":"$hue.neutral.300","overlay":"$hue.neutral.400"},
        "action": {
          "primary":{"default":"$hue.interactive.600","$hovered":"$hue.interactive.700","$focused":"$hue.interactive.700","$pressed":"$hue.interactive.800","$selected":"$hue.interactive.700","$disabled":"$hue.neutral.300"},
          "secondary":{"default":"transparent"},
          "destructive":{"default":"$hue.red.600","$hovered":"$hue.red.700","$focused":"$hue.red.700","$pressed":"$hue.red.800","$selected":"$hue.red.700","$disabled":"$hue.neutral.300"}},
        "formfield":{"default":"$background.default","$hovered":"$background.surface.offset","$focused":"$background.action.primary.default","$pressed":"$hue.interactive.800","$disabled":"$background.default","$selected":"$background.formfield.default"},
        "feedback":{"error":{"default":"$background.default"},"warning":{"default":"$background.default"},"success":{"default":"$background.default"},"info":{"default":"$background.default"}}},
      "border":{"default":"$hue.neutral.300"},"scrollbar":{"default":"$hue.neutral.400"},
      "diff":{"text":{"added":"$hue.green.700","removed":"$hue.red.700","context":"$hue.neutral.900","hunkHeader":"$hue.purple.600"},"background":{"added":"$hue.green.100","removed":"$hue.red.100","context":"$hue.neutral.100"},"highlight":{"added":"$hue.green.600","removed":"$hue.red.600"},"lineNumber":{"text":"$hue.neutral.600","background":{"added":"$hue.green.200","removed":"$hue.red.200"}}},
      "syntax":{"comment":"$hue.neutral.600","keyword":"$hue.purple.600","function":"$hue.accent.600","variable":"$hue.neutral.900","string":"$hue.green.700","number":"$hue.yellow.800","type":"$hue.yellow.500","operator":"$hue.cyan.600","punctuation":"$hue.neutral.900"},
      "markdown":{"text":"$hue.neutral.900","heading":"$hue.purple.600","link":"$hue.accent.600","linkText":"$hue.cyan.600","code":"$hue.green.700","blockQuote":"$hue.neutral.600","emphasis":"$hue.yellow.500","strong":"$hue.neutral.900","horizontalRule":"$hue.neutral.300","listItem":"$hue.accent.600","listEnumeration":"$hue.cyan.600","image":"$hue.accent.600","imageText":"$hue.cyan.600","codeBlock":"$hue.neutral.900"},
      "@context:elevated":{"text":{"action":{"primary":{"default":"$hue.neutral.100"}}},"background":{"default":"$background.surface.offset","action":{"primary":{"default":"$hue.interactive.500","$hovered":"$background.surface.overlay"}}}},
      "@context:overlay":{"text":{"action":{"primary":{"default":"$hue.neutral.100"}}},"background":{"default":"$background.surface.overlay","action":{"primary":{"default":"$hue.interactive.500"}}}}
    }
    """;

    private const string Dark = """
    {
      "text": {
        "default":"$hue.neutral.200","subdued":"$hue.neutral.400",
        "action": {
          "primary":{"default":"$hue.neutral.200","$disabled":"$hue.neutral.500"},
          "secondary":{"default":"$text.subdued","$hovered":"$text.default"},
          "destructive":{"default":"$hue.red.200","$disabled":"$hue.neutral.500"}},
        "formfield":{"default":"$hue.neutral.200","$focused":"$text.action.primary.default","$pressed":"$hue.neutral.200","$disabled":"$hue.neutral.500","$selected":"$hue.interactive.500"},
        "status":{"running":"$hue.interactive.200","question":"$text.status.unread","permission":"$text.status.unread","unread":"$hue.accent.200"},
        "feedback":{"error":{"default":"$hue.red.300","subdued":"$hue.red.400"},"warning":{"default":"$hue.yellow.200","subdued":"$hue.yellow.300"},"success":{"default":"$hue.green.300","subdued":"$hue.green.400"},"info":{"default":"$hue.cyan.300","subdued":"$hue.cyan.400"}}},
      "background": {
        "default":"$hue.neutral.800","surface":{"offset":"$hue.neutral.700","overlay":"$hue.neutral.600"},
        "action": {
          "primary":{"default":"$hue.interactive.500","$hovered":"$hue.interactive.600","$focused":"$hue.interactive.600","$pressed":"$hue.interactive.800","$selected":"$hue.interactive.600","$disabled":"$hue.neutral.800"},
          "secondary":{"default":"transparent"},
          "destructive":{"default":"$hue.red.600","$hovered":"$hue.red.700","$focused":"$hue.red.700","$pressed":"$hue.red.800","$selected":"$hue.red.700","$disabled":"$hue.neutral.800"}},
        "formfield":{"default":"$background.default","$hovered":"$background.surface.offset","$focused":"$background.action.primary.default","$pressed":"$hue.interactive.800","$disabled":"$background.default","$selected":"$background.formfield.default"},
        "feedback":{"error":{"default":"$background.default"},"warning":{"default":"$background.default"},"success":{"default":"$background.default"},"info":{"default":"$background.default"}}},
      "border":{"default":"$hue.neutral.700"},"scrollbar":{"default":"$hue.neutral.600"},
      "diff":{"text":{"added":"$hue.green.300","removed":"$hue.red.300","context":"$hue.neutral.100","hunkHeader":"$hue.purple.400"},"background":{"added":"$hue.green.900","removed":"$hue.red.900","context":"$hue.neutral.900"},"highlight":{"added":"$hue.green.400","removed":"$hue.red.400"},"lineNumber":{"text":"$hue.neutral.400","background":{"added":"$hue.green.800","removed":"$hue.red.800"}}},
      "syntax":{"comment":"$hue.neutral.400","keyword":"$hue.purple.400","function":"$hue.accent.400","variable":"$hue.neutral.100","string":"$hue.green.300","number":"$hue.yellow.200","type":"$hue.yellow.500","operator":"$hue.cyan.400","punctuation":"$hue.neutral.100"},
      "markdown":{"text":"$hue.neutral.100","heading":"$hue.purple.400","link":"$hue.accent.400","linkText":"$hue.cyan.400","code":"$hue.green.300","blockQuote":"$hue.neutral.400","emphasis":"$hue.yellow.500","strong":"$hue.neutral.100","horizontalRule":"$hue.neutral.700","listItem":"$hue.accent.400","listEnumeration":"$hue.cyan.400","image":"$hue.accent.400","imageText":"$hue.cyan.400","codeBlock":"$hue.neutral.100"},
      "@context:elevated":{"text":{"action":{"primary":{"default":"$hue.neutral.200"}}},"background":{"default":"$background.surface.offset","action":{"primary":{"default":"$hue.interactive.400","$hovered":"$background.surface.overlay"}}}},
      "@context:overlay":{"text":{"action":{"primary":{"default":"$hue.neutral.200"}}},"background":{"default":"$background.surface.overlay","action":{"primary":{"default":"$hue.interactive.400"}}}}
    }
    """;
}
