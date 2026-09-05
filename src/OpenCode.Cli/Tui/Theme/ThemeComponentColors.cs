namespace OpenCode.Cli.Tui.Theme;

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
