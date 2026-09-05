namespace OpenTui.Native;

using System.Buffers;
using System.Runtime.InteropServices;

public static unsafe partial class OpenTuiNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StyledChunkData
    {
        public byte* Text;
        public nuint TextLength;
        public NativeRgba* Foreground;
        public NativeRgba* Background;
        public uint Attributes;
        public byte* Link;
        public nuint LinkLength;
    }

    [LibraryImport(LibName, EntryPoint = "createTextBuffer")]
    internal static partial uint CreateTextBuffer(byte widthMethod);

    [LibraryImport(LibName, EntryPoint = "destroyTextBuffer")]
    internal static partial void DestroyTextBuffer(uint buffer);

    [LibraryImport(LibName, EntryPoint = "createTextBufferView")]
    internal static partial uint CreateTextBufferView(uint buffer);

    [LibraryImport(LibName, EntryPoint = "destroyTextBufferView")]
    internal static partial void DestroyTextBufferView(uint view);

    [LibraryImport(LibName, EntryPoint = "textBufferClear")]
    private static partial void TextBufferClear(uint buffer);

    [LibraryImport(LibName, EntryPoint = "textBufferSetStyledText")]
    private static partial void TextBufferSetStyledText(uint buffer, StyledChunkData* chunks, uint count);

    [LibraryImport(LibName, EntryPoint = "createSyntaxStyle")]
    internal static partial uint CreateSyntaxStyle();

    [LibraryImport(LibName, EntryPoint = "destroySyntaxStyle")]
    internal static partial void DestroySyntaxStyle(uint style);

    [LibraryImport(LibName, EntryPoint = "textBufferSetSyntaxStyle")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool TextBufferSetSyntaxStyle(uint buffer, uint style);

    [LibraryImport(LibName, EntryPoint = "textBufferClearAllHighlights")]
    private static partial void TextBufferClearAllHighlights(uint buffer);

    [LibraryImport(LibName, EntryPoint = "textBufferSetDefaultFg")]
    private static partial void TextBufferSetDefaultFg(uint buffer, NativeRgba* color);

    [LibraryImport(LibName, EntryPoint = "textBufferSetDefaultBg")]
    private static partial void TextBufferSetDefaultBg(uint buffer, NativeRgba* color);

    [LibraryImport(LibName, EntryPoint = "textBufferSetDefaultAttributes")]
    private static partial void TextBufferSetDefaultAttributes(uint buffer, uint* attributes);

    [LibraryImport(LibName, EntryPoint = "textBufferSetTabWidth")]
    internal static partial void TextBufferSetTabWidth(uint buffer, byte width);

    [LibraryImport(LibName, EntryPoint = "textBufferViewSetWrapWidth")]
    internal static partial void TextBufferViewSetWrapWidth(uint view, uint width);

    [LibraryImport(LibName, EntryPoint = "textBufferViewSetWrapMode")]
    internal static partial void TextBufferViewSetWrapMode(uint view, byte mode);

    [LibraryImport(LibName, EntryPoint = "textBufferViewSetFirstLineOffset")]
    internal static partial void TextBufferViewSetFirstLineOffset(uint view, uint offset);

    [LibraryImport(LibName, EntryPoint = "textBufferViewSetViewport")]
    internal static partial void TextBufferViewSetViewport(uint view, uint x, uint y, uint width, uint height);

    [LibraryImport(LibName, EntryPoint = "textBufferViewSetViewportSize")]
    internal static partial void TextBufferViewSetViewportSize(uint view, uint width, uint height);

    [LibraryImport(LibName, EntryPoint = "textBufferViewMeasureForDimensions")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool TextBufferViewMeasureForDimensions(uint view, uint width, uint height, out NativeTextMeasure result);

    [LibraryImport(LibName, EntryPoint = "textBufferViewGetVirtualLineCount")]
    internal static partial uint TextBufferViewGetVirtualLineCount(uint view);

    [LibraryImport(LibName, EntryPoint = "bufferDrawTextBufferView")]
    internal static partial void BufferDrawTextBufferView(uint buffer, uint view, int x, int y);

    internal static void SetTextBufferText(uint buffer, ReadOnlySpan<char> text)
    {
        Span<byte> scratch = stackalloc byte[512];
        byte[]? rented = null;
        try
        {
            SetTextBufferText(buffer, EncodeUtf8(text, scratch, out rented));
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static void SetTextBufferText(uint buffer, ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            TextBufferClear(buffer);
            TextBufferClearAllHighlights(buffer);
            return;
        }
        fixed (byte* text = utf8)
        {
            // Unlike append/FromMem, setStyledText copies into a reusable native-owned buffer.
            // With no syntax-style attached, this plain chunk uses the buffer's default style.
            var chunk = new StyledChunkData { Text = text, TextLength = (nuint)utf8.Length };
            TextBufferSetStyledText(buffer, &chunk, 1);
        }
    }

    internal static void SetTextBufferRuns(uint buffer, ReadOnlySpan<NativeTextRun> runs, uint attachStyle)
    {
        var byteCount = 0;
        foreach (var run in runs)
        {
            if (run.Link.Length > 512)
                throw new ArgumentException("Link URLs must be at most 512 UTF-8 bytes.", nameof(runs));
            byteCount = checked(byteCount + run.Text.Length + run.Link.Length);
        }
        var colorCount = checked(runs.Length * 2);
        byte[]? rentedBytes = null;
        StyledChunkData[]? rentedChunks = null;
        NativeRgba[]? rentedColors = null;
        try
        {
            // Bound stack use independently of payload/run count; pin the batch, not each input memory.
            Span<byte> bytes = byteCount <= 1024 ? stackalloc byte[Math.Max(1, byteCount)]
                : (rentedBytes = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
            Span<StyledChunkData> chunks = runs.Length <= 8 ? stackalloc StyledChunkData[runs.Length]
                : (rentedChunks = ArrayPool<StyledChunkData>.Shared.Rent(runs.Length)).AsSpan(0, runs.Length);
            Span<NativeRgba> colors = runs.Length <= 8 ? stackalloc NativeRgba[colorCount]
                : (rentedColors = ArrayPool<NativeRgba>.Shared.Rent(colorCount)).AsSpan(0, colorCount);

            fixed (byte* data = bytes)
            fixed (StyledChunkData* chunkData = chunks)
            fixed (NativeRgba* colorData = colors)
            {
                var offset = 0;
                for (var i = 0; i < runs.Length; i++)
                {
                    var run = runs[i];
                    run.Text.Span.CopyTo(bytes.Slice(offset, run.Text.Length));
                    colors[i * 2] = run.Foreground.GetValueOrDefault();
                    colors[i * 2 + 1] = run.Background.GetValueOrDefault();
                    chunks[i] = new StyledChunkData
                    {
                        Text = data + offset,
                        TextLength = (nuint)run.Text.Length,
                        Foreground = run.Foreground.HasValue ? colorData + i * 2 : null,
                        Background = run.Background.HasValue ? colorData + i * 2 + 1 : null,
                        Attributes = run.Attributes,
                        Link = run.Link.IsEmpty ? null : data + offset + run.Text.Length,
                        LinkLength = (nuint)run.Link.Length
                    };
                    offset += run.Text.Length;
                    run.Link.Span.CopyTo(bytes.Slice(offset, run.Link.Length));
                    offset += run.Link.Length;
                }
                if (attachStyle != 0 && !TextBufferSetSyntaxStyle(buffer, attachStyle))
                    throw new InvalidOperationException("OpenTUI could not attach the text syntax style.");
                // Native copies text, colors/attributes, and URLs before this call returns.
                TextBufferSetStyledText(buffer, chunkData, (uint)runs.Length);
            }
        }
        finally
        {
            if (rentedChunks is not null)
            {
                // Do not leave call-scoped addresses in the metadata pool.
                rentedChunks.AsSpan(0, runs.Length).Clear();
                ArrayPool<StyledChunkData>.Shared.Return(rentedChunks);
            }
            if (rentedColors is not null) ArrayPool<NativeRgba>.Shared.Return(rentedColors);
            if (rentedBytes is not null) ArrayPool<byte>.Shared.Return(rentedBytes);
        }
    }

    internal static void SetTextBufferStyle(uint buffer, NativeRgba? foreground, NativeRgba? background, uint? attributes)
    {
        var fg = foreground.GetValueOrDefault();
        var bg = background.GetValueOrDefault();
        var attr = attributes.GetValueOrDefault();
        TextBufferSetDefaultFg(buffer, foreground.HasValue ? &fg : null);
        TextBufferSetDefaultBg(buffer, background.HasValue ? &bg : null);
        TextBufferSetDefaultAttributes(buffer, attributes.HasValue ? &attr : null);
    }
}
