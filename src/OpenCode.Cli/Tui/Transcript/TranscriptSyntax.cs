namespace OpenCode.Cli.Tui.Transcript;

using OpenCode.Cli.Tui.Theme;
using OpenTui.Native;

/// <summary>Theme-owner data adapter only. Native allocation belongs to the code view.</summary>
public static class TranscriptSyntax
{
    public static IReadOnlyList<NativeSyntaxRule> Rules(ThemeTokens tokens) => ThemeSyntax.GenerateNative(tokens)
        // The source theme generator sets positive flags only; absent flags do
        // not erase attributes from another active capture during scope merging.
        .SelectMany(rule => rule.Scopes.Select(scope => new NativeSyntaxRule(scope, rule.Foreground, rule.Background,
            rule.Attributes, AttributeMask: rule.Attributes))).ToArray();
}
