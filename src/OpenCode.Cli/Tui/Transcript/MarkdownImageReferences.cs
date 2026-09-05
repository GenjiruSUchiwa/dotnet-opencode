namespace OpenCode.Cli.Tui.Transcript;

using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using OpenCode.Cli.Tui.Images;

/// <summary>Finds real Markdown image references without fetching or authorizing their sources.</summary>
public static class MarkdownImageReferences
{
    public static IReadOnlyList<ImagePreviewItem> Read(ContainerBlock document) => document
        .SelectMany(block => block is LeafBlock leaf ? Read(leaf.Inline) : block is ContainerBlock container ? Read(container) : []).ToArray();

    public static IReadOnlyList<ImagePreviewItem> Read(ContainerInline? inline) => inline is null ? [] : inline
        .SelectMany(node => node is LinkInline { IsImage: true, Url: { } uri } image
            ? new[] { Item(uri, image) }
            : node is ContainerInline container ? Read(container) : []).ToArray();

    public static ImagePreviewItem? Single(ContainerInline? inline)
    {
        if (inline is null) return null;
        var children = inline.Where(node => node is not LiteralInline literal || !string.IsNullOrWhiteSpace(literal.Content.ToString())).ToArray();
        return children is [LinkInline { IsImage: true, Url: { } uri } image] ? Item(uri, image) : null;
    }
    private static ImagePreviewItem Item(string uri, ContainerInline inline)
    {
        var text = Text(inline);
        return new(uri, text.Length == 0 ? null : text);
    }
    private static string Text(ContainerInline inline) => string.Concat(inline.Select(node => node switch
    {
        LiteralInline literal => literal.Content.ToString(), CodeInline code => code.Content,
        ContainerInline container => Text(container), LineBreakInline => "\n", _ => ""
    }));
}
