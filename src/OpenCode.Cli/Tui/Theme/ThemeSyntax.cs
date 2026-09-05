namespace OpenCode.Cli.Tui.Theme;

using OpenTui.Native;

/// <summary>Pure scope/style data from packages/theme/src/tui/syntax.ts.
/// Native syntax-style allocation, replacement, and disposal belong to the renderer owner.</summary>
public sealed record ThemeSyntaxRule(IReadOnlyList<string> Scopes, ThemeColor Foreground,
    ThemeColor? Background = null, bool Bold = false, bool Italic = false, bool Underline = false)
{
    // OpenTUI 0.5.9 TextAttributes: BOLD=1<<0, ITALIC=1<<2, UNDERLINE=1<<3.
    public uint Attributes => (Bold ? 1u : 0u) | (Italic ? 4u : 0u) | (Underline ? 8u : 0u);
    public NativeThemeSyntaxRule ToNative() => new(Array.AsReadOnly(Scopes.ToArray()), Foreground.Native, Background?.Native, Attributes);
}

/// <summary>Native-ready values only, not a native syntax handle. No interop calls.</summary>
public sealed record NativeThemeSyntaxRule(IReadOnlyList<string> Scopes, NativeRgba Foreground, NativeRgba? Background, uint Attributes)
{
    public NativeTextRun ToRun(ReadOnlyMemory<byte> text) => new(text, Foreground, Background, Attributes);
}

public static class ThemeSyntax
{
    /// <summary>Preserves declaration order and scope groups for the Transcript/native owner.</summary>
    public static IReadOnlyList<NativeThemeSyntaxRule> GenerateNative(ThemeTokens theme) =>
        Generate(theme).Select(rule => rule.ToNative()).ToArray();

    public static IReadOnlyList<ThemeSyntaxRule> Generate(ThemeTokens theme)
    {
        var step = theme.Mode == ThemeMode.Light ? 800 : 200;
        return [
            Rule(["default"], "text.default"),
            new(["prompt"], theme.Hue["accent"][step]),
            Rule(["extmark.file"], "text.feedback.warning.default", bold: true),
            new(["extmark.agent"], theme.Categorical[0][step], Bold: true),
            new(["extmark.skill"], theme.Categorical[Math.Min(1, theme.Categorical.Count - 1)][step], Bold: true),
            Rule(["extmark.paste"], "text.action.primary.focused", "text.feedback.warning.default", bold: true),
            Rule(["comment", "comment.documentation"], "syntax.comment", italic: true),
            Rule(["string", "symbol", "character.special", "character"], "syntax.string"),
            Rule(["number", "boolean", "constant", "float"], "syntax.number"),
            Rule(["keyword.return", "keyword.conditional", "keyword.repeat", "keyword.coroutine"], "syntax.keyword", italic: true),
            Rule(["keyword.type"], "syntax.type", bold: true, italic: true),
            Rule(["keyword.function", "function.method"], "syntax.function"),
            Rule(["keyword"], "syntax.keyword", italic: true),
            Rule(["keyword.import", "string.escape", "string.regexp", "tag.attribute", "keyword.export"], "syntax.keyword"),
            Rule(["operator", "keyword.operator", "punctuation.delimiter", "keyword.conditional.ternary"], "syntax.operator"),
            Rule(["variable", "variable.parameter", "function.method.call", "function.call", "property", "parameter", "field"], "syntax.variable"),
            Rule(["variable.member", "function", "constructor"], "syntax.function"),
            Rule(["type", "module", "class", "namespace"], "syntax.type"),
            Rule(["type.definition"], "syntax.type", bold: true),
            Rule(["punctuation", "punctuation.bracket"], "syntax.punctuation"),
            Rule(["variable.builtin", "type.builtin", "function.builtin", "module.builtin", "constant.builtin", "variable.super"], "text.feedback.error.default"),
            Rule(["keyword.directive", "keyword.modifier", "keyword.exception"], "syntax.keyword", italic: true),
            Rule(["punctuation.special", "tag.delimiter"], "syntax.operator"),
            Rule(["markup.heading", "markup.heading.2", "markup.heading.3", "markup.heading.4", "markup.heading.5", "markup.heading.6"], "markdown.heading", bold: true),
            Rule(["markup.heading.1"], "markdown.heading", bold: true, underline: true),
            Rule(["markup.bold", "markup.strong"], "markdown.strong", bold: true),
            Rule(["markup.italic"], "markdown.emphasis", italic: true),
            Rule(["markup.list"], "markdown.listItem"),
            Rule(["markup.quote"], "markdown.blockQuote", italic: true),
            Rule(["markup.raw", "markup.raw.block"], "markdown.code"),
            Rule(["markup.raw.inline"], "markdown.code", "background.default"),
            Rule(["markup.link", "markup.link.url", "string.special", "string.special.url"], "markdown.link", underline: true),
            Rule(["markup.link.label"], "markdown.linkText", underline: true),
            Rule(["label"], "markdown.linkText"),
            Rule(["spell", "nospell"], "text.default"),
            Rule(["markup.underline"], "text.default", underline: true),
            Rule(["comment.error"], "text.feedback.error.default", bold: true, italic: true),
            Rule(["comment.warning"], "text.feedback.warning.default", bold: true, italic: true),
            Rule(["comment.todo", "comment.note"], "text.feedback.info.default", bold: true, italic: true),
            Rule(["attribute", "annotation"], "text.feedback.warning.default"),
            Rule(["tag"], "text.feedback.error.default"),
            Rule(["markup.strikethrough", "markup.list.unchecked", "debug"], "text.subdued"),
            Rule(["markup.list.checked"], "text.feedback.success.default"),
            Rule(["diff.plus"], "diff.text.added", "diff.background.added"),
            Rule(["diff.minus"], "diff.text.removed", "diff.background.removed"),
            Rule(["diff.delta"], "diff.text.context", "diff.background.context"),
            Rule(["error"], "text.feedback.error.default", bold: true),
            Rule(["warning"], "text.feedback.warning.default", bold: true),
            Rule(["info"], "text.feedback.info.default")
        ];
        ThemeSyntaxRule Rule(string[] scopes, string foreground, string? background = null, bool bold = false, bool italic = false, bool underline = false) =>
            new(scopes, theme.Color(foreground), background is null ? null : theme.Color(background), bold, italic, underline);
    }
}
