namespace OpenCode.Cli.Tui.Theme;

using System.Text.Json.Nodes;

internal static class BuiltinThemeData
{
    // Exact key/insertion order from packages/tui/src/theme/v1.ts DEFAULT_THEMES.
    // Resource names are explicit so incidental JSON files cannot become installed themes.
    internal static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[]
    {
        "aura", "ayu", "catppuccin", "catppuccin-frappe", "catppuccin-macchiato", "cobalt2", "cursor", "dracula",
        "everforest", "flexoki", "github", "gruvbox", "kanagawa", "material", "matrix", "mercury", "monokai",
        "nightowl", "nord", "one-dark", "osaka-jade", "opencode", "orng", "lucent-orng", "palenight", "rosepine",
        "solarized", "synthwave84", "tokyonight", "vesper", "vercel", "zenburn", "carbonfox"
    });

    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> Documents = new(() =>
        Names.ToDictionary(name => name, Read, StringComparer.Ordinal));

    internal static Dictionary<string, JsonObject> All() => Documents.Value.ToDictionary(
        pair => pair.Key, pair => pair.Value.DeepClone().AsObject(), StringComparer.Ordinal);

    // Retain the previous internal entry point; it now reads the byte-preserved asset.
    internal static JsonObject Opencode() => Documents.Value["opencode"].DeepClone().AsObject();

    internal static JsonObject Provenance() => Read("catalog-provenance");

    private static JsonObject Read(string name)
    {
        using var stream = typeof(BuiltinThemeData).Assembly.GetManifestResourceStream(
            $"OpenCode.Cli.Tui.Theme.Assets.{name}.json")
            ?? throw new InvalidOperationException($"Bundled theme resource is missing: {name}");
        return JsonNode.Parse(stream) as JsonObject ?? throw new FormatException($"Bundled theme resource is not an object: {name}");
    }
}
