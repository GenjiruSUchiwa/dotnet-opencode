namespace OpenTui.Blazor.Code;

using System.Text;
using OpenTui.Native;

internal static class CodeChunks
{
    internal static List<CodeChunk> FromDocument(CodeDocument document)
    {
        if (document.Chunks is { } projected) return projected.ToList();
        var styles = new Dictionary<string, NativeSyntaxRule>(StringComparer.Ordinal);
        foreach (var style in document.Styles) styles[style.Scope] = style;
        var chunks = new List<CodeChunk>();
        var offset = 0;
        foreach (var span in document.Spans)
        {
            if (span.Start > offset) chunks.Add(new(document.Text[offset..span.Start]));
            var style = styles.GetValueOrDefault(span.Style);
            chunks.Add(new(document.Text[span.Start..span.End], style?.Foreground, style?.Background, style?.Attributes ?? 0));
            offset = span.End;
        }
        if (offset < document.Text.Length) chunks.Add(new(document.Text[offset..]));
        return chunks;
    }

    internal static CodeDocument ToDocument(IReadOnlyList<CodeChunk> chunks)
    {
        var text = new StringBuilder();
        var styles = new List<NativeSyntaxRule>();
        var spans = new List<CodeStyleSpan>();
        var names = new Dictionary<(NativeRgba? Foreground, NativeRgba? Background, uint Attributes), string>();
        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.Text is null)
                throw new ArgumentException("Code chunks and their text must be non-null.", nameof(chunks));
            if (chunk.Text.Length == 0) continue;
            var key = (chunk.Foreground, chunk.Background, chunk.Attributes);
            if (!names.TryGetValue(key, out var name))
            {
                name = "__transformed_chunk_" + names.Count;
                names.Add(key, name);
                styles.Add(new(name, chunk.Foreground, chunk.Background, chunk.Attributes));
            }
            var start = text.Length;
            text.Append(chunk.Text);
            spans.Add(new(start, text.Length, name));
        }
        var content = text.ToString();
        // Like source onChunks/initialStyledText, arbitrary text transformations
        // invalidate the original conceal-line mapping. Map rendered lines only.
        return new(content, Array.AsReadOnly(styles.ToArray()), Array.AsReadOnly(spans.ToArray()),
            CodeDocument.Plain(content).SourceLines);
    }
}
