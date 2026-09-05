namespace OpenCode.Cli.Tui.ToolViews;

using OpenCode.Cli.Tui.Theme;
using OpenCode.Schema;
using OpenTui.Blazor.Code;
using OpenTui.Native;

public enum ToolDiffView { Auto, Unified, Split }

/// <summary>Optional host-owned cascade through SessionTranscript. No configuration reads or server ownership.</summary>
public sealed record ToolViewBindings
{
    public ToolDiffView DiffView { get; init; } = ToolDiffView.Auto;
    public NativeTextWrapMode DiffWrap { get; init; } = NativeTextWrapMode.Word;
    public ToolDiffColors? DiffColors { get; init; }
    public ICodeHighlighter? CodeHighlighter { get; init; }
    public IReadOnlyList<NativeSyntaxRule> SyntaxRules { get; init; } = [];
    public Func<string, string?>? Filetype { get; init; }
    public Func<SessionId, Task>? NavigateSession { get; init; }
    public Func<SessionId, bool?>? IsSessionRunning { get; init; }
}

/// <summary>Exact existing diff roles; missing host colors remain unset, not synthesized from status colors.</summary>
public sealed record ToolDiffColors(string AddedBackground, string RemovedBackground, string ContextBackground,
    string AddedSign, string RemovedSign, string LineNumber, string AddedLineNumberBackground, string RemovedLineNumberBackground)
{
    public static ToolDiffColors From(ThemeTokens theme) => new(
        theme.Color("diff.background.added").Hex, theme.Color("diff.background.removed").Hex, theme.Color("diff.background.context").Hex,
        theme.Color("diff.highlight.added").Hex, theme.Color("diff.highlight.removed").Hex, theme.Color("diff.lineNumber.text").Hex,
        theme.Color("diff.lineNumber.background.added").Hex, theme.Color("diff.lineNumber.background.removed").Hex);
}
