namespace OpenTui.Native;

using System.Runtime.InteropServices;

public enum NativeTextWrapMode : byte
{
    None = 0,
    Character = 1,
    Word = 2
}

public enum NativeSelectionBehavior : byte { Cell = 0, Word = 1, Line = 2 }

/// <summary>Half-open native display offsets, including logical line breaks. Not UTF-16 or UTF-8 indexes.</summary>
public readonly record struct NativeTextSelectionRange(uint Start, uint End);

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeTextMeasure
{
    public readonly uint LineCount;
    public readonly uint WidthColumns;
}

/// <summary>Owns a retained native text buffer and its view. Text is copied into native storage.</summary>
/// <remarks>
/// Serialize updates, measurement, drawing, and disposal. No handles or native memory escape.
/// This owner is independent of renderer size; update its viewport after layout changes.
/// </remarks>
public sealed class NativeTextView : IDisposable
{
    private uint _buffer;
    private uint _view;
    private uint _syntaxStyle;
    private bool _styled;
    private NativeSyntaxStyle.Lease? _externalStyle;

    /// <summary>Immutable native width method: 0=wcwidth, 1=unicode, 3=unicode-wide.</summary>
    public byte WidthMethod { get; }
    internal uint ViewHandle { get { Guard(); return _view; } }

    public NativeTextView(byte widthMethod = 1)
    {
        if (widthMethod is not (0 or 1 or 3)) throw new ArgumentOutOfRangeException(nameof(widthMethod));
        WidthMethod = widthMethod;
        _buffer = OpenTuiNative.CreateTextBuffer(widthMethod);
        if (_buffer == 0) throw new InvalidOperationException("OpenTUI could not create a text buffer.");
        try
        {
            _view = OpenTuiNative.CreateTextBufferView(_buffer);
            if (_view == 0) throw new InvalidOperationException("OpenTUI could not create a text buffer view.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetText(text.AsSpan());
    }

    public void SetText(ReadOnlySpan<char> text)
    {
        Guard();
        UsePlainStyle();
        OpenTuiNative.SetTextBufferText(_buffer, text);
    }

    /// <summary>Copies UTF-8 synchronously; the caller can release its input immediately afterward.</summary>
    public void SetText(ReadOnlySpan<byte> utf8)
    {
        Guard();
        UsePlainStyle();
        OpenTuiNative.SetTextBufferText(_buffer, utf8);
    }

    /// <summary>Replaces content with copied styled runs. The caller can release all run memory after returning.</summary>
    public void SetStyledText(ReadOnlySpan<NativeTextRun> runs)
    {
        Guard();
        if (_externalStyle is not null) throw new InvalidOperationException("Detach the external syntax style before supplying independent styled runs.");
        if (runs.IsEmpty)
        {
            SetText(ReadOnlySpan<byte>.Empty);
            return;
        }
        if (_syntaxStyle == 0)
        {
            _syntaxStyle = OpenTuiNative.CreateSyntaxStyle();
            if (_syntaxStyle == 0) throw new InvalidOperationException("OpenTUI could not create a text syntax style.");
        }
        OpenTuiNative.SetTextBufferRuns(_buffer, runs, _styled ? 0 : _syntaxStyle);
        _styled = true;
    }

    /// <summary>Sets whole-buffer defaults without uploading text again. Null resets a native default.</summary>
    /// <remarks>Explicit run colors override defaults; run attributes are OR'd with default attributes.</remarks>
    public void SetStyle(NativeRgba? foreground = null, NativeRgba? background = null, uint? attributes = null)
    {
        Guard();
        OpenTuiNative.SetTextBufferStyle(_buffer, foreground, background, attributes);
    }

    public void SetWrapMode(NativeTextWrapMode mode)
    {
        Guard();
        if (mode is not (NativeTextWrapMode.None or NativeTextWrapMode.Character or NativeTextWrapMode.Word))
            throw new ArgumentOutOfRangeException(nameof(mode));
        OpenTuiNative.TextBufferViewSetWrapMode(_view, (byte)mode);
    }

    public void SetTruncate(bool enabled) { Guard(); OpenTuiNative.SetTextViewTruncate(_view, enabled); }

    /// <summary>Sets a wrap budget in display columns; null or zero removes the budget.</summary>
    public void SetWrapWidth(int? width)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfNegative(width ?? 0, nameof(width));
        OpenTuiNative.TextBufferViewSetWrapWidth(_view, (uint)(width ?? 0));
    }

    /// <summary>Reduces the first wrapped line's column budget; this is not a UTF-8/UTF-16 text offset.</summary>
    public void SetFirstLineOffset(uint columns)
    {
        Guard();
        OpenTuiNative.TextBufferViewSetFirstLineOffset(_view, columns);
    }

    /// <summary>Sets a window in virtual-line space: x is a display column, y a wrapped row, both zero-based.</summary>
    /// <remarks>Also sets wrap width to width. Horizontal scrolling applies to unwrapped text.</remarks>
    public void SetViewport(int x, int y, int width, int height)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        OpenTuiNative.TextBufferViewSetViewport(_view, (uint)x, (uint)y, (uint)width, (uint)height);
    }

    /// <summary>Resizes the viewport, preserving its scroll offsets, and updates the wrap width.</summary>
    public void Resize(int width, int height)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        OpenTuiNative.TextBufferViewSetViewportSize(_view, (uint)width, (uint)height);
    }

    /// <summary>Measures without changing the viewport. Width zero measures intrinsic width; height is currently ignored upstream.</summary>
    public NativeTextMeasure Measure(int width, int height = 0)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (!OpenTuiNative.TextBufferViewMeasureForDimensions(_view, (uint)width, (uint)height, out var result))
            throw new InvalidOperationException("OpenTUI could not measure the text view.");
        return result;
    }

    public uint GetVirtualLineCount()
    {
        Guard();
        return OpenTuiNative.TextBufferViewGetVirtualLineCount(_view);
    }

    /// <summary>Map viewport-local terminal cells using this view's actual wrap, tab, and grapheme layout.</summary>
    public bool SetLocalSelection(int anchorX, int anchorY, int focusX, int focusY,
        NativeRgba? background = null, NativeRgba? foreground = null, bool update = false,
        NativeSelectionBehavior behavior = NativeSelectionBehavior.Cell)
    {
        Guard();
        if (!Enum.IsDefined(behavior)) throw new ArgumentOutOfRangeException(nameof(behavior));
        return OpenTuiNative.SetLocalTextSelection(_view, anchorX, anchorY, focusX, focusY, background, foreground, update, (byte)behavior);
    }

    public NativeTextSelectionRange? GetSelection()
    {
        Guard();
        var packed = OpenTuiNative.GetTextSelectionInfo(_view);
        return packed == ulong.MaxValue ? null : new((uint)(packed >> 32), (uint)packed);
    }

    /// <summary>Restore a native range after reflow without remapping it through screen coordinates.</summary>
    public void SetSelection(NativeTextSelectionRange range, NativeRgba? background = null, NativeRgba? foreground = null)
    {
        Guard();
        OpenTuiNative.SetTextSelection(_view, range.Start, range.End, background, foreground);
    }

    public string GetSelectedText()
    {
        Guard();
        return OpenTuiNative.GetSelectedText(_buffer, _view);
    }

    public void ResetSelection()
    {
        Guard();
        OpenTuiNative.ResetTextSelection(_view);
    }

    public void SetSyntaxStyle(NativeSyntaxStyle? style)
    {
        Guard();
        var lease = style?.Retain();
        if (!OpenTuiNative.TextBufferSetSyntaxStyle(_buffer, lease?.Handle ?? 0))
        {
            lease?.Dispose();
            throw new InvalidOperationException("Could not attach native syntax style.");
        }
        var previous = _externalStyle;
        _externalStyle = lease;
        _styled = false;
        previous?.Dispose();
    }
    /// <summary>Line-local display columns, not UTF-16 or UTF-8 offsets.</summary>
    public void AddLineHighlight(uint line, NativeTextHighlight highlight)
    {
        Guard();
        if (_externalStyle is null) throw new InvalidOperationException("Attach a syntax style before adding highlights.");
        OpenTuiNative.AddTextLineHighlight(_buffer, line, in highlight);
    }
    /// <summary>Native cumulative display offsets excluding newlines, as used by addHighlightByCharRange.</summary>
    public void AddDisplayHighlight(NativeTextHighlight highlight)
    {
        Guard();
        if (_externalStyle is null) throw new InvalidOperationException("Attach a syntax style before adding highlights.");
        OpenTuiNative.AddTextDisplayHighlight(_buffer, in highlight);
    }
    public void RemoveHighlights(ushort reference) { Guard(); OpenTuiNative.RemoveTextHighlights(_buffer, reference); }
    public void ClearLineHighlights(uint line) { Guard(); OpenTuiNative.ClearTextLineHighlights(_buffer, line); }
    public void ClearHighlights() { Guard(); OpenTuiNative.ClearTextHighlights(_buffer); }
    public uint HighlightCount { get { Guard(); return OpenTuiNative.TextHighlightCount(_buffer); } }

    /// <summary>Native tab width clamps to [2, 254] and rounds odd values upward. Default is 2.</summary>
    public void SetTabWidth(byte columns)
    {
        Guard();
        OpenTuiNative.TextBufferSetTabWidth(_buffer, columns);
    }

    /// <summary>Draws at zero-based destination cell coordinates. The target handle is borrowed only for this call.</summary>
    public void Draw(uint targetBuffer, int x, int y)
    {
        Guard();
        ArgumentOutOfRangeException.ThrowIfZero(targetBuffer);
        OpenTuiNative.BufferDrawTextBufferView(targetBuffer, _view, x, y);
    }

    public void Dispose()
    {
        var view = _view;
        var buffer = _buffer;
        var style = _syntaxStyle;
        _view = 0;
        _buffer = 0;
        _syntaxStyle = 0;
        _styled = false;
        try
        {
            if (view != 0) OpenTuiNative.DestroyTextBufferView(view);
        }
        finally
        {
            try
            {
                // Buffer destruction unregisters its syntax-style destruction observer.
                if (buffer != 0) OpenTuiNative.DestroyTextBuffer(buffer);
            }
            finally
            {
                try { if (style != 0) OpenTuiNative.DestroySyntaxStyle(style); }
                finally { _externalStyle?.Dispose(); _externalStyle = null; }
            }
        }
    }

    private void UsePlainStyle()
    {
        if (!_styled) return;
        if (!OpenTuiNative.TextBufferSetSyntaxStyle(_buffer, 0))
            throw new InvalidOperationException("OpenTUI could not detach the text syntax style.");
        _styled = false;
    }

    private void Guard() => ObjectDisposedException.ThrowIf(_buffer == 0, this);
}
