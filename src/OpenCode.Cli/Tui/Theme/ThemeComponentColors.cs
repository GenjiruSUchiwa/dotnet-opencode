namespace OpenCode.Cli.Tui.Theme;

/// <summary>Colors for the production Home composer; no component-owned palette.</summary>
public sealed record ComposerTheme(
    string PageBackground, string PromptBackground, string Border, string InputText, string PlaceholderText,
    string Agent, string Model, string Subdued, string Variant, string ActionText, bool OpaquePrompt)
{
    public static ComposerTheme From(ThemeTokens tokens, string? agentColor = null, bool disabled = false, bool shell = false)
    {
        var surface = tokens.Raise(tokens.Color("background.surface.offset"));
        var highlight = disabled ? tokens.Border : shell ? tokens.ActionText(ThemeActionVariant.Primary, ThemeActionState.Selected)
            : agentColor is null ? tokens.Border : ThemeColor.Parse(agentColor);
        return new(tokens.Background.Hex, surface.Hex, highlight.Hex,
            // Upstream prompt textColor/focusedTextColor use text.default (or
            // text.subdued when muted), not a form-field option state.
            (disabled ? tokens.Subdued : tokens.Text).Hex, tokens.Subdued.Hex,
            highlight.Hex, disabled ? tokens.Subdued.Hex : tokens.Text.Hex, tokens.Subdued.Hex,
            // A selected model variant is configuration metadata, not warning feedback.
            tokens.FormfieldText(disabled ? ThemeActionState.Disabled : ThemeActionState.Selected).Hex,
            tokens.ActionText(ThemeActionVariant.Secondary).Hex, surface.Alpha != 0);
    }

    public static ComposerTheme Default => From(ThemeComponentDefaults.Tokens);
}

/// <summary>Logo text and backdrop roles. Shadows are calculated from these colors, not a dark-only palette.</summary>
public sealed record WordmarkTheme(ThemeColor Text, ThemeColor Subdued, ThemeColor Background)
{
    public static WordmarkTheme From(ThemeTokens tokens) => new(tokens.Text, tokens.Subdued, tokens.Background);
    public static WordmarkTheme Default => From(ThemeComponentDefaults.Tokens);
}

internal static class ThemeComponentDefaults
{
    private static readonly Lazy<ThemeTokens> Default = new(() => new ThemeCatalog().Resolve("opencode", ThemeMode.Dark).Base);
    internal static ThemeTokens Tokens => Default.Value;
}
