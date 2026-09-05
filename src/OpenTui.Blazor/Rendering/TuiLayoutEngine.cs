namespace OpenTui.Blazor.Rendering;

using System.Globalization;
using System.Text;
using OpenTui.Blazor.Nodes;
using OpenTui.Native;
using OpenTui.Blazor.TextMarks;

public sealed partial class TuiLayoutEngine
{
    public TerminalRenderColors Colors { get; set; } = TerminalRenderColors.Default;
    public TerminalImageContext ImageContext { get; set; } = new(null, 0, 0);
    private readonly Dictionary<string, int> _widths = new(StringComparer.Ordinal);
    private static readonly uint[] SingleBorder = [0x250C, 0x2510, 0x2514, 0x2518, 0x2500, 0x2502, 0x252C, 0x2534, 0x251C, 0x2524, 0x253C];
    private static readonly uint[] DoubleBorder = [0x2554, 0x2557, 0x255A, 0x255D, 0x2550, 0x2551, 0x2566, 0x2569, 0x2560, 0x2563, 0x256C];
    private static readonly uint[] HeavyBorder = [0x250F, 0x2513, 0x2517, 0x251B, 0x2501, 0x2503, 0x2533, 0x253B, 0x2523, 0x252B, 0x254B];
    private static readonly uint[] RoundedBorder = [0x256D, 0x256E, 0x2570, 0x256F, 0x2500, 0x2502, 0x252C, 0x2534, 0x251C, 0x2524, 0x253C];
    private byte _widthMethod;
    private readonly List<TuiNode> _modals = [];
    private NativeCursorAppearance? _applicationCursor;

    public void InvalidateTextMetrics(TuiNode root)
    {
        _widths.Clear();
        Clear(root);
        static void Clear(TuiNode node)
        {
            node.InputLayout = null;
            node.InputTextMap = null;
            foreach (var child in node.Children) Clear(child);
        }
    }

    public void LayoutAndRender(TuiNode root, int width, int height, uint renderer, uint buffer)
    {
        UpdateLayout(root, width, height, buffer);
        Render(root, renderer, buffer);
    }

    public void Render(TuiNode root, uint renderer, uint buffer)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderer);
        ArgumentOutOfRangeException.ThrowIfZero(buffer);
        if (FocusedTerminal(root)) _applicationCursor ??= OpenTuiNative.GetCursorAppearance(renderer);
        else if (_applicationCursor is { } cursor)
        {
            OpenTuiNative.SetCursorAppearance(renderer, cursor);
            _applicationCursor = null;
        }
        OpenTuiNative.SetCursorPosition(renderer, 1, 1, false);
        OpenTuiNative.SetCursorColor(renderer, Colors.Cursor ?? Colors.Foreground);
        Paint(root, buffer, renderer, Colors.Foreground, Colors.Background, 0, 0, root.LayoutWidth, root.LayoutHeight);
        foreach (var modal in _modals)
        {
            OpenTuiNative.SetCursorPosition(renderer, 1, 1, false);
            Paint(modal, buffer, renderer, Colors.Foreground, Colors.Background, 0, 0, root.LayoutWidth, root.LayoutHeight);
        }
    }

    public void UpdateLayout(TuiNode root, int width, int height, uint buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfZero(buffer);
        var method = OpenTuiNative.GetBufferWidthMethod(buffer);
        if (method != _widthMethod) _widths.Clear();
        _widthMethod = method;
        LayoutWithYoga(root, width, height);
    }

    private static bool FocusedTerminal(TuiNode node) => node.Focused && node.EmbeddedTerminal is not null || node.LayoutChildren.Any(FocusedTerminal);

    private void FindModals(TuiNode node)
    {
        if (node.TagName == "modal") _modals.Add(node);
            foreach (var child in node.LayoutChildren) FindModals(child);
    }

    /// <summary>Returns the topmost cell hit within the supplied focus scope, using paint order and ancestor clipping.</summary>
    public TuiNode? HitTest(TuiNode scope, int x, int y) => Hit(scope, scope.X, scope.Y,
        (long)scope.X + scope.LayoutWidth, (long)scope.Y + scope.LayoutHeight, x, y);

    internal NativeTextView SelectionView(TuiNode node)
    {
        var view = TextView(node);
        var count = checked((int)view.Measure(Math.Max(1, node.LayoutWidth)).LineCount);
        node.Scroll = Math.Clamp(node.Scroll, 0, Math.Max(0, count - node.LayoutHeight));
        var start = node.Tail ? Math.Max(0, count - node.LayoutHeight - node.Scroll) : node.Scroll;
        if (node.NativeViewportTop != start || node.NativeViewportWidth != node.LayoutWidth || node.NativeViewportHeight != node.LayoutHeight)
        {
            view.SetViewport(0, start, Math.Max(1, node.LayoutWidth), Math.Max(1, node.LayoutHeight));
            node.NativeViewportTop = start;
            node.NativeViewportWidth = node.LayoutWidth;
            node.NativeViewportHeight = node.LayoutHeight;
        }
        return view;
    }

    private TuiNode? Hit(TuiNode node, long left, long top, long right, long bottom, int x, int y)
    {
        if (!node.PointerEvents) return null;
        if (x < left || x >= right || y < top || y >= bottom) return null;
        var inside = x >= node.X && x < (long)node.X + node.LayoutWidth &&
            y >= node.Y && y < (long)node.Y + node.LayoutHeight;
        if (node.Overflow != TuiOverflow.Visible)
        {
            if (!inside) return null;
            left = Math.Max(left, (long)node.X + node.BorderLeft);
            top = Math.Max(top, (long)node.Y + node.BorderTop);
            right = Math.Min(right, (long)node.X + node.LayoutWidth - node.BorderRight);
            bottom = Math.Min(bottom, (long)node.Y + node.LayoutHeight - node.BorderBottom);
        }
        foreach (var child in node.PaintChildren.Reverse())
            if (Hit(child, left, top, right, bottom, x, y) is { } hit) return hit;
        return inside ? node : null;
    }

    private void Paint(TuiNode node, uint buffer, uint renderer, NativeRgba foreground, NativeRgba background,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        var left = Math.Max(clipLeft, node.X);
        var top = Math.Max(clipTop, node.Y);
        var right = (int)Math.Min(clipRight, (long)node.X + node.LayoutWidth);
        var bottom = (int)Math.Min(clipBottom, (long)node.Y + node.LayoutHeight);
        var fg = node.Fg ?? foreground;
        var bg = node.ShouldFill ? node.Bg ?? background : background;
        if (right <= left || bottom <= top)
        {
            if (node.Overflow == TuiOverflow.Visible)
                foreach (var child in node.PaintChildren) Paint(child, buffer, renderer, fg, bg, clipLeft, clipTop, clipRight, clipBottom);
            return;
        }
        var borderSides = node.EffectiveBorderSides;
        OpenTuiNative.PushScissor(buffer, left, top, (uint)(right - left), (uint)(bottom - top));
        try
        {
            // Bordered boxes use the native primitive's interior fill and border
            // background handling, not an opaque prefill followed by glyph text.
            if (borderSides == TuiBorderSides.None && node.ShouldFill && node.Bg.HasValue)
                OpenTuiNative.FillRect(buffer, left, top, right - left, bottom - top, bg);
            var selectionFg = node.SelectionForeground ?? Colors.SelectionForeground;
            var selectionBg = node.SelectionBackground ?? Colors.SelectionBackground;
            // The ancestor canvas is already painted. OpenTUI text/textarea
            // defaults are transparent unless this view supplies its own bg.
            // Passing the inherited canvas as an opaque text default changes
            // native selection inversion (and styled-run background composition).
            var textBackground = node.Bg ?? new NativeRgba(0, 0, 0, 0);
            if (borderSides != TuiBorderSides.None)
            {
                var characters = node.BorderCodepoints ?? (node.BorderStyle is "left" or "heavy" ? HeavyBorder
                    : node.BorderStyle == "double" ? DoubleBorder : node.BorderStyle == "rounded" ? RoundedBorder : SingleBorder);
                var borderColor = node.BorderFg ?? (node.BorderSides.HasValue || node.BorderCodepoints is not null ? NativeRgba.White : fg);
                OpenTuiNative.DrawBox(buffer, node.X, node.Y, (uint)node.LayoutWidth, (uint)node.LayoutHeight,
                    characters, (uint)borderSides | (node.ShouldFill ? 16u : 0u), borderColor,
                    node.Bg ?? new NativeRgba(0, 0, 0, 0));
            }
            if (node.EmbeddedTerminal is { } embedded)
            {
                // The host clears its frame every paint. Invalidate the real
                // compositor so clean emulator rows also reach this fresh target.
                embedded.Terminal.Invalidate();
                embedded.Terminal.Compose(buffer, node.X, node.Y);
                if (node.Focused)
                {
                    var cursor = embedded.Terminal.Cursor();
                    var cursorX = node.X + (cursor.WideTail && cursor.X > 0 ? cursor.X - 1 : cursor.X);
                    var cursorY = node.Y + cursor.Y;
                    var visible = cursor.HasValue && cursor.Visible && cursorX >= left && cursorX < right && cursorY >= top && cursorY < bottom;
                    OpenTuiNative.SetCursorPosition(renderer, cursorX + 1, cursorY + 1, visible);
                    if (visible) OpenTuiNative.ApplyEmbeddedCursorStyle(renderer, cursor);
                }
                return;
            }
            if (node.Image is { } image)
            {
                image.Draw(buffer, node.X, node.Y, node.LayoutWidth, node.LayoutHeight, node.ImageFit, node.ImageProtocol, ImageContext);
                return;
            }
            if (node.TagName == "input")
            {
                var text = node.Content;
                var input = InputLayout(node, node.LayoutWidth);
                var cursor = input.Position(Math.Clamp(node.Cursor ?? 0, 0, text.Length));
                node.InputTop = Math.Clamp(node.InputTop, Math.Max(0, cursor.Row - node.LayoutHeight + 1), cursor.Row);
                node.InputTop = Math.Min(node.InputTop, Math.Max(0, input.Lines.Count - node.LayoutHeight));
                var view = TextView(node);
                var attributes = (node.Bold ? 1u : 0) | (node.Dim ? 2u : 0);
                if (!Nullable.Equals(node.NativeForeground, fg) || !Nullable.Equals(node.NativeBackground, textBackground) || node.NativeAttributes != attributes)
                {
                    view.SetStyle(fg, textBackground, attributes);
                    node.NativeForeground = fg; node.NativeBackground = textBackground; node.NativeAttributes = attributes;
                }
                if (node.NativeViewportTop != node.InputTop || node.NativeViewportWidth != node.LayoutWidth || node.NativeViewportHeight != node.LayoutHeight)
                {
                    view.SetViewport(0, node.InputTop, node.LayoutWidth, node.LayoutHeight);
                    node.NativeViewportTop = node.InputTop; node.NativeViewportWidth = node.LayoutWidth; node.NativeViewportHeight = node.LayoutHeight;
                }
                var anchor = input.Position(Math.Clamp(node.SelectionAnchor ?? node.Cursor ?? 0, 0, text.Length)).Index;
                var caret = cursor.Index;
                if (anchor != caret)
                {
                    if (node.InputTextMap is null || node.InputMapText != text || node.InputMapWidthMethod != _widthMethod)
                    {
                        node.InputTextMap = new TerminalTextMap(text, element => element[0] is '\r' or '\n' ? 1 : CellWidth(element));
                        node.InputMapText = text; node.InputMapWidthMethod = _widthMethod;
                    }
                    var startOffset = checked((uint)node.InputTextMap.DisplayAtUtf16(Math.Min(anchor, caret)));
                    var endOffset = checked((uint)node.InputTextMap.DisplayAtUtf16(Math.Max(anchor, caret)));
                    view.SetSelection(new(startOffset, endOffset), selectionBg, selectionFg);
                }
                else view.ResetSelection();
                if (text.Length == 0 && node.Y >= top)
                    OpenTuiNative.DrawText(buffer, node.Placeholder ?? "", (uint)node.X, (uint)node.Y, node.PlaceholderFg ?? NativeRgba.Gray, bg);
                else view.Draw(buffer, node.X, node.Y);
                if (node.Focused && (long)node.Y + cursor.Row - node.InputTop >= top && (long)node.Y + cursor.Row - node.InputTop < bottom)
                {
                    OpenTuiNative.SetCursorColor(renderer, node.CursorColor ?? Colors.Cursor ?? Colors.Foreground);
                    OpenTuiNative.SetCursorPosition(renderer, node.X + Math.Min(cursor.Column, node.LayoutWidth - 1) + 1, node.Y + cursor.Row - node.InputTop + 1, true);
                }
            }
            else if (node.TagName is "text" or "#text" && (node.Content.Length > 0 || !node.TextRuns.IsDefaultOrEmpty))
            {
                var view = SelectionView(node);
                var attributes = (node.Bold ? 1u : 0) | (node.Dim ? 2u : 0);
                if (!Nullable.Equals(node.NativeForeground, fg) || !Nullable.Equals(node.NativeBackground, textBackground) || node.NativeAttributes != attributes)
                {
                    view.SetStyle(fg, textBackground, attributes);
                    node.NativeForeground = fg;
                    node.NativeBackground = textBackground;
                    node.NativeAttributes = attributes;
                }
                // Selection and paint share this exact viewport. Ancestor scissor
                // rectangles clip drawing, not the view's local coordinate space.
                view.Draw(buffer, node.X, node.Y);
            }
        }
        finally
        {
            OpenTuiNative.PopScissor(buffer);
        }
        // Source pushes the descendant scissor only for hidden/scroll overflow,
        // after rendering the box itself. Visible overflow inherits the ancestor clip.
        var overflowVisible = node.Overflow == TuiOverflow.Visible;
        var childLeft = overflowVisible ? clipLeft : Coordinate(Math.Max(left, (long)node.X + node.BorderLeft));
        var childTop = overflowVisible ? clipTop : Coordinate(Math.Max(top, (long)node.Y + node.BorderTop));
        var childRight = overflowVisible ? clipRight : Coordinate(Math.Min(right, (long)node.X + node.LayoutWidth - node.BorderRight));
        var childBottom = overflowVisible ? clipBottom : Coordinate(Math.Min(bottom, (long)node.Y + node.LayoutHeight - node.BorderBottom));
        foreach (var child in node.PaintChildren) Paint(child, buffer, renderer, fg, bg, childLeft, childTop, childRight, childBottom);
    }

    public int CellWidth(string text) => CellWidth(text.AsSpan());

    private NativeTextView TextView(TuiNode node)
    {
        if (node.TextView is not null && node.TextView.WidthMethod != _widthMethod) node.ReleaseNativeText();
        if (node.TextView is null)
        {
            node.TextView = new NativeTextView(_widthMethod);
        }
        if (node.NativeWrapMode != node.WrapMode)
        {
            node.TextView.SetWrapMode(node.WrapMode);
            node.NativeWrapMode = node.WrapMode;
            node.NativeViewportWidth = node.NativeViewportHeight = 0;
        }
        if (node.NativeTruncate != node.Truncate)
        {
            node.TextView.SetTruncate(node.Truncate);
            node.NativeTruncate = node.Truncate;
            node.NativeViewportWidth = node.NativeViewportHeight = 0;
        }
        if (node.CodeDocument is { } document)
        {
            if (!ReferenceEquals(node.NativeCodeDocument, document))
            {
                var style = new NativeSyntaxStyle();
                try
                {
                    foreach (var rule in document.Styles) style.Register(rule);
                    node.TextView.SetSyntaxStyle(style);
                    node.TextView.SetText(document.Text);
                    node.TextView.ClearHighlights();
                    AddCodeHighlights(node.TextView, style, document);
                    var previous = node.CodeSyntaxStyle;
                    node.CodeSyntaxStyle = style;
                    previous?.Dispose();
                }
                catch { style.Dispose(); throw; }
                node.NativeCodeDocument = document;
                node.NativeText = document.Text;
                node.NativeViewportWidth = node.NativeViewportHeight = 0;
                node.NativeSelectionColorsSet = false;
            }
        }
        else if (!node.TextRuns.IsDefault)
        {
            if (node.NativeRuns != node.TextRuns)
            {
                node.TextView.SetStyledText(node.TextRuns.AsSpan());
                node.NativeSelectionColorsSet = false;
                node.NativeViewportWidth = node.NativeViewportHeight = 0;
                node.NativeRuns = node.TextRuns;
                node.NativeText = null;
            }
        }
        else if (!node.NativeRuns.IsDefault || node.NativeText != node.Content)
        {
            node.TextView.SetText(node.Content);
            node.NativeSelectionColorsSet = false;
            node.NativeViewportWidth = node.NativeViewportHeight = 0;
            node.NativeText = node.Content;
            node.NativeRuns = default;
        }
        var foreground = node.SelectionForeground ?? Colors.SelectionForeground;
        var background = node.SelectionBackground ?? Colors.SelectionBackground;
        if (node.SelectionRange is { } range && (!node.NativeSelectionColorsSet ||
            !Nullable.Equals(node.NativeSelectionForeground, foreground) || !Nullable.Equals(node.NativeSelectionBackground, background)))
        {
            node.TextView.SetSelection(range, background, foreground);
            node.NativeSelectionForeground = foreground;
            node.NativeSelectionBackground = background;
            node.NativeSelectionColorsSet = true;
        }
        return node.TextView;
    }

    private void AddCodeHighlights(NativeTextView view, NativeSyntaxStyle style, Code.CodeDocument document)
    {
        var boundaries = new Dictionary<int, (uint Line, uint Column)> { [0] = (0, 0) };
        uint line = 0;
        uint column = 0;
        for (var index = 0; index < document.Text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(document.Text.AsSpan(index));
            var element = document.Text.AsSpan(index, length);
            if (element[0] is '\r' or '\n') { line++; column = 0; }
            else column = checked(column + (uint)CellWidth(element));
            index += length;
            boundaries[index] = (line, column);
        }
        var offsets = boundaries.Keys.Order().ToArray();
        foreach (var span in document.Spans)
        {
            if (style.Resolve(span.Style) is not { } id) continue;
            var start = Array.BinarySearch(offsets, span.Start);
            var end = Array.BinarySearch(offsets, span.End);
            start = start >= 0 ? start : Math.Max(0, ~start - 1);
            end = end >= 0 ? end : ~end;
            uint? activeLine = null;
            uint startColumn = 0;
            uint endColumn = 0;
            for (var index = start; index < end; index++)
            {
                var from = boundaries[offsets[index]];
                var to = boundaries[offsets[index + 1]];
                if (activeLine is { } current && (current != from.Line || from.Line != to.Line))
                {
                    view.AddLineHighlight(current, new(startColumn, endColumn, id));
                    activeLine = null;
                }
                if (from.Line != to.Line || from.Column == to.Column) continue;
                if (activeLine is null) { activeLine = from.Line; startColumn = from.Column; }
                endColumn = to.Column;
            }
            if (activeLine is { } lastLine) view.AddLineHighlight(lastLine, new(startColumn, endColumn, id));
        }
    }

    private int CellWidth(ReadOnlySpan<char> text)
    {
        var width = 0;
        while (!text.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(text);
            width += ElementWidth(text[..length]);
            text = text[length..];
        }
        return width;
    }

    private int ElementWidth(ReadOnlySpan<char> element)
    {
        if (element.Length == 1 && element[0] == '\t') return 2;
        if (element.Length == 1 && element[0] is >= ' ' and <= '~') return 1;
        var lookup = _widths.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(element, out var width)) return width;
        width = OpenTuiNative.MeasureCellWidth(element, _widthMethod);
        if (_widths.Count >= 4096) _widths.Clear();
        lookup[element] = width;
        return width;
    }

    private TerminalTextLayout InputLayout(TuiNode node, int width)
    {
        if (node.InputLayout is not null && node.InputLayout.Text == node.Content && node.InputLayoutWidth == width && node.InputWidthMethod == _widthMethod)
            return node.InputLayout;
        node.InputLayoutWidth = width;
        node.InputWidthMethod = _widthMethod;
        return node.InputLayout = MeasureInput(node.Content, width);
    }

    public TerminalTextLayout MeasureInput(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<TerminalTextLine>();
        var positions = new List<TerminalTextPosition> { new(0, 0, 0) };
        var start = 0;
        var column = 0;
        for (var index = 0; index < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            var element = text.AsSpan(index, length);
            if (element[0] is '\r' or '\n')
            {
                lines.Add(new(start, index));
                index += length;
                start = index;
                column = 0;
                positions.Add(new(index, lines.Count, 0));
                continue;
            }
            var cells = ElementWidth(element);
            if (column > 0 && (long)column + cells > width)
            {
                lines.Add(new(start, index));
                start = index;
                column = 0;
                positions[^1] = new(index, lines.Count, 0);
            }
            column += cells;
            index += length;
            positions.Add(new(index, lines.Count, column));
        }
        lines.Add(new(start, text.Length));
        if (column >= width)
        {
            lines.Add(new(text.Length, text.Length));
            positions[^1] = new(text.Length, lines.Count - 1, 0);
        }
        return new(text, lines, positions);
    }

    private static int Cells(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
    private static int Coordinate(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

}
