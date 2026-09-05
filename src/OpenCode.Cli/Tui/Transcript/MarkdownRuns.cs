namespace OpenCode.Cli.Tui.Transcript;

using System.Collections.Immutable;
using System.Text;
using Markdig.Syntax.Inlines;
using OpenTui.Native;

internal static class MarkdownRuns
{
    public static ImmutableArray<NativeTextRun> Create(ContainerInline? inline, TranscriptTheme theme, bool subdued = false)
    {
        var runs = ImmutableArray.CreateBuilder<NativeTextRun>();
        if (inline is not null) Append(inline, runs, theme, subdued, 0, null, null);
        return runs.ToImmutable();
    }

    private static void Append(Inline inline, ImmutableArray<NativeTextRun>.Builder runs,
        TranscriptTheme theme, bool subdued, uint attributes, string? color, string? link)
    {
        switch (inline)
        {
            case EmphasisInline emphasis:
                attributes |= emphasis.DelimiterChar == '~' ? 128u : emphasis.DelimiterCount >= 2 ? 1u : 4u;
                break;
            case LinkInline hyperlink:
                color = theme.MarkdownLink;
                link = hyperlink.Url;
                attributes |= 8;
                if (hyperlink.IsImage && hyperlink.FirstChild is null)
                {
                    if (link is not null && Encoding.UTF8.GetByteCount(link) > 512) link = null;
                    runs.Add(new NativeTextRun("image", TranscriptColor.Parse(subdued ? theme.Subdued : color), Attributes: attributes, Link: link));
                    return;
                }
                break;
        }
        if (inline is ContainerInline container)
        {
            foreach (var child in container) Append(child, runs, theme, subdued, attributes, color, link);
            return;
        }
        var text = inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            LineBreakInline => "\n",
            AutolinkInline autolink => autolink.Url,
            HtmlInline html => html.Tag,
            _ => inline.ToString() ?? ""
        };
        if (inline is CodeInline) color = theme.MarkdownCode;
        if (inline is AutolinkInline auto)
        {
            color = theme.MarkdownLink;
            link = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
        }
        // Native hyperlinks have a 512-byte limit; the visible link text is never discarded.
        if (link is not null && Encoding.UTF8.GetByteCount(link) > 512) link = null;
        runs.Add(new NativeTextRun(text, TranscriptColor.Parse(subdued ? theme.Subdued : color ?? theme.MarkdownText),
            Attributes: attributes, Link: link));
    }
}
