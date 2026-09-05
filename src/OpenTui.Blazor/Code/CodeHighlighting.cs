namespace OpenTui.Blazor.Code;

using OpenTui.Native;

/// <summary>Parser output offsets index the request's original UTF-16 string, not native display columns.</summary>
public sealed record CodeCapture(int Start, int End, string Scope, CodeCaptureMetadata? Metadata = null);
public sealed record CodeCaptureMetadata(bool IsInjection = false, string? InjectionLanguage = null,
    bool ContainsInjection = false, bool HasConceal = false, string? Conceal = null, bool ConcealLines = false);
public sealed record CodeHighlightRequest(string Content, string Filetype, long Revision);
public sealed record CodeHighlightResult(bool HasParser, IReadOnlyList<CodeCapture> Captures, string? Warning = null)
{
    /// <summary>The base parser succeeded, but one or more requested injections could not be highlighted.</summary>
    public bool IsPartial { get; init; }
}

/// <summary>Caller-supplied real parser. The renderer has no built-in heuristic lexer or JS runtime.</summary>
public interface ICodeHighlighter
{
    Task<CodeHighlightResult> HighlightAsync(CodeHighlightRequest request, CancellationToken cancellationToken);
}

public sealed record CodeOptions(string Content, string? Filetype = null, ICodeHighlighter? Highlighter = null,
    IReadOnlyList<NativeSyntaxRule>? SyntaxRules = null, bool Conceal = true, bool DrawUnstyledText = true,
    bool Streaming = false, string? BaseHighlight = null);
public sealed record CodeStyleSpan(int Start, int End, string Style);
public sealed record CodeDocument(string Text, IReadOnlyList<NativeSyntaxRule> Styles,
    IReadOnlyList<CodeStyleSpan> Spans, IReadOnlyList<int> SourceLines)
{
    public static CodeDocument Plain(string text) => new(text, [], [], Array.AsReadOnly(Enumerable.Range(0, text.Count(character => character == '\n') + 1).ToArray()));
}
