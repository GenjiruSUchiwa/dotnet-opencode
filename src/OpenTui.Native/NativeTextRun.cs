namespace OpenTui.Native;

using System.Text;

/// <summary>A styled UTF-8 run. Memory is borrowed only until SetStyledText returns.</summary>
/// <remarks>
/// Keep run boundaries on grapheme boundaries. Empty Link means no hyperlink; nonempty URLs
/// must be at most 512 UTF-8 bytes. Null colors inherit the view's defaults. Attributes are
/// the native u32 mask (base style bits 0-7), OR'd with buffer defaults; a Link supplies its
/// own native hyperlink ID. Do not mutate the run array or backing memory during submission.
/// </remarks>
public readonly record struct NativeTextRun(
    ReadOnlyMemory<byte> Text,
    NativeRgba? Foreground = null,
    NativeRgba? Background = null,
    uint Attributes = 0,
    ReadOnlyMemory<byte> Link = default)
{
    /// <summary>Encodes and allocates UTF-8 storage once. Prefer the memory constructor for already encoded content.</summary>
    public NativeTextRun(string Text, NativeRgba? Foreground = null, NativeRgba? Background = null,
        uint Attributes = 0, string? Link = null)
        : this(Encoding.UTF8.GetBytes(Text), Foreground, Background, Attributes,
            Link is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(Link))
    {
    }
}
