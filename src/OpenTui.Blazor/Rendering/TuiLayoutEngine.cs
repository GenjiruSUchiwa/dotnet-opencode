namespace OpenTui.Blazor.Rendering;

using System.Globalization;
using System.Text;
using OpenTui.Blazor.Nodes;
using OpenTui.Native;
using OpenTui.Blazor.TextMarks;

public sealed class TuiLayoutEngine
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
        Layout(root, 0, 0, width, height);
        _modals.Clear();
        FindModals(root);
        var ordered = _modals.OrderBy(modal => modal.ZIndex).ToArray();
        _modals.Clear();
        _modals.AddRange(ordered);
        foreach (var modal in _modals)
        {
            modal.X = modal.Y = 0;
            modal.LayoutWidth = width;
            modal.LayoutHeight = height;
            foreach (var panel in modal.FlowChildren)
            {
                var panelWidth = Math.Clamp(panel.Width ?? 60, 0, Math.Max(0, width - 2));
                var desiredHeight = NaturalHeight(panel, Math.Max(1, panelWidth));
                var top = modal.Center ? Math.Max(0, (height - desiredHeight) / 2) : height / 4;
                Layout(panel, Math.Max(0, (width - panelWidth) / 2), top, panelWidth, Math.Min(desiredHeight, Math.Max(0, height - top - 1)));
            }
        }
    }

    private static bool FocusedTerminal(TuiNode node) => node.Focused && node.EmbeddedTerminal is not null || node.LayoutChildren.Any(FocusedTerminal);

    private void FindModals(TuiNode node)
    {
        if (node.TagName == "modal") _modals.Add(node);
            foreach (var child in node.LayoutChildren) FindModals(child);
    }

    private int NaturalWidth(TuiNode node) => node.Width ?? (node.TagName is "text" or "#text"
        ? IntrinsicTextWidth(node)
        : node.TagName == "input" ? node.Content.Split('\n').Select(CellWidth).DefaultIfEmpty(0).Max()
        : Cells((node.Direction == TuiFlexDirection.Row
            ? node.FlowChildren.Sum(child => (long)NaturalWidth(child)) + (long)Math.Max(0, node.FlowChildren.Count - 1) * node.Gap
            : node.FlowChildren.Select(child => (long)NaturalWidth(child)).DefaultIfEmpty(0).Max())
          + node.PaddingLeft + node.PaddingRight + (node.BorderStyle is null ? 0 : node.BorderStyle == "left" ? 1 : 2)));

    private int NaturalHeight(TuiNode node, int width)
    {
        if (node.Height is int height) return Math.Max(0, height);
        if (node.TagName == "input") return Math.Min(node.MaxHeight, InputLayout(node, Math.Max(1, width)).Lines.Count);
        if (node.TagName is "text" or "#text") return Math.Max(1, checked((int)TextView(node).Measure(Math.Max(1, width)).LineCount));
        var inset = node.BorderStyle is null or "left" ? 0 : 2;
        var inner = Math.Max(1, Cells((long)width - node.PaddingLeft - node.PaddingRight - (node.BorderStyle == "left" ? 1 : inset)));
        var content = node.Direction == TuiFlexDirection.Column
            ? node.FlowChildren.Sum(child => child.Grow > 0 ? 0L : NaturalHeight(child, Math.Min(child.Width ?? inner, inner))) + (long)Math.Max(0, node.FlowChildren.Count - 1) * node.Gap
            : AllocateAxis(node, inner, inner, true).Select((childWidth, index) => (long)NaturalHeight(node.FlowChildren[index], childWidth)).DefaultIfEmpty(0).Max();
        return Cells((long)node.PaddingTop + node.PaddingBottom + inset + content);
    }

    private int[] AllocateAxis(TuiNode node, int available, int contentWidth, bool row)
    {
        if (row && SharedColumnWidths(node, available) is { } tracks) return tracks;
        var sizes = node.FlowChildren.Select(child => child.Grow > 0 ? 0
            : row ? NaturalWidth(child) : NaturalHeight(child, Math.Min(child.Width ?? contentWidth, contentWidth))).ToArray();
        var requested = sizes.Sum(size => (long)size) + (long)Math.Max(0, sizes.Length - 1) * node.Gap;
        var remaining = Math.Max(0L, available - requested);
        var deficit = Math.Max(0L, requested - available);
        var shrinkWeight = sizes.Select((size, index) => (decimal)size * node.FlowChildren[index].Shrink).Sum();
        var weight = node.FlowChildren.Sum(child => (long)child.Grow);
        long position = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            var child = node.FlowChildren[i];
            var size = child.Grow > 0 && weight > 0 ? remaining * child.Grow / weight : sizes[i];
            if (child.Shrink > 0 && child.Grow == 0 && shrinkWeight > 0)
            {
                var share = (decimal)size * child.Shrink;
                var reduction = (long)Math.Min(size, Math.Ceiling(deficit * (share / shrinkWeight)));
                shrinkWeight -= share;
                deficit -= reduction;
                size -= reduction;
            }
            if (child.Grow > 0) { remaining -= size; weight -= child.Grow; }
            sizes[i] = Cells(Math.Clamp(size, 0, Math.Max(0, available - position)));
            position += (long)sizes[i] + node.Gap;
        }
        return sizes;
    }

    // Rows in a shared-column container use the same measured tracks during
    // both height measurement and placement, including after terminal resize.
    private int[]? SharedColumnWidths(TuiNode row, int available)
    {
        var parent = row.Parent;
        while (parent?.TagName == "#component") parent = parent.Parent;
        if (parent?.SharedColumns != true) return null;
        var columns = parent.FlowChildren.Select(item => item.FlowChildren.Count).DefaultIfEmpty(0).Max();
        if (columns == 0) return [];
        var widths = Enumerable.Range(0, columns).Select(column => parent.FlowChildren
            .Where(item => item.FlowChildren.Count > column)
            .Select(item => NaturalWidth(item.FlowChildren[column])).DefaultIfEmpty(1).Max()).ToArray();
        var budget = Math.Max(0L, available - (long)Math.Max(0, columns - 1) * row.Gap);
        var total = widths.Sum(width => (long)width);
        if (total > budget)
        {
            // Keep short columns readable; long cells wrap instead of taking
            // nearly the entire pane under proportional shrinking.
            var order = Enumerable.Range(0, columns).OrderBy(column => widths[column]).ToArray();
            for (var index = 0; index < order.Length; index++)
            {
                var column = order[index];
                widths[column] = Cells(Math.Min(widths[column], budget / (columns - index)));
                budget -= widths[column];
            }
            return widths.Take(row.FlowChildren.Count).ToArray();
        }
        var extra = Math.Max(0, budget - total);
        for (var column = 0; column < widths.Length; column++)
        {
            var share = extra / (columns - column);
            widths[column] = Cells(widths[column] + share);
            extra -= share;
        }
        return widths.Take(row.FlowChildren.Count).ToArray();
    }

    private void Layout(TuiNode node, int x, int y, int width, int height, bool growWidth = false, bool growHeight = false)
    {
        node.X = x;
        node.Y = y;
        node.LayoutWidth = growWidth ? width : Math.Max(0, Math.Min(node.Width ?? width, width));
        node.LayoutHeight = growHeight ? height : Math.Max(0, Math.Min(node.Height ?? height, height));
        if (node.ScrollState is { } scroll)
        {
            var rows = new List<TerminalScrollState.Row>(node.FlowChildren.Count);
            long total = 0;
            foreach (var child in node.FlowChildren)
            {
                var childHeight = NaturalHeight(child, Math.Min(child.Width ?? node.LayoutWidth, node.LayoutWidth));
                rows.Add(new(child, Cells(total), childHeight));
                total += childHeight;
            }
            scroll.Update(rows, Cells(total), node.LayoutHeight);
            foreach (var entry in rows)
                Layout(entry.Node, x, Coordinate((long)y + entry.Start - scroll.Offset), node.LayoutWidth, entry.Height);
            LayoutAbsoluteChildren(node, x, y, node.LayoutWidth, node.LayoutHeight);
            return;
        }
        var border = node.BorderStyle is null or "left" ? 0 : 1;
        var leftBorder = node.BorderStyle is null ? 0 : 1;
        var innerX = Coordinate((long)x + Math.Min(node.LayoutWidth, (long)leftBorder + node.PaddingLeft));
        var innerY = Coordinate((long)y + Math.Min(node.LayoutHeight, (long)border + node.PaddingTop));
        var innerWidth = Cells((long)node.LayoutWidth - leftBorder - border - node.PaddingLeft - node.PaddingRight);
        var innerHeight = Cells((long)node.LayoutHeight - 2 * border - node.PaddingTop - node.PaddingBottom);
        var row = node.Direction == TuiFlexDirection.Row;
        var available = row ? innerWidth : innerHeight;
        var sizes = AllocateAxis(node, available, innerWidth, row);
        long position = 0;
        for (var index = 0; index < node.FlowChildren.Count; index++)
        {
            var child = node.FlowChildren[index];
            var size = sizes[index];
            var cross = row ? Math.Min(child.Height ?? innerHeight, innerHeight) : Math.Min(child.Width ?? innerWidth, innerWidth);
            if (node.CrossAlignment != TuiCrossAlignment.Stretch)
                cross = Math.Min(cross, row ? NaturalHeight(child, size) : NaturalWidth(child));
            var freeCross = Math.Max(0, (row ? innerHeight : innerWidth) - cross);
            var offset = node.CrossAlignment == TuiCrossAlignment.End ? freeCross
                : node.Center || node.CrossAlignment == TuiCrossAlignment.Center ? freeCross / 2 : 0;
            Layout(child, Coordinate((long)innerX + (row ? Math.Min(position, available) : offset)),
                Coordinate((long)innerY + (row ? offset : Math.Min(position, available))), row ? Cells(size) : cross, row ? cross : Cells(size),
                growWidth: row && child.Grow > 0, growHeight: !row && child.Grow > 0);
            position += (long)size + node.Gap;
        }
        LayoutAbsoluteChildren(node, innerX, innerY, innerWidth, innerHeight);
    }

    private void LayoutAbsoluteChildren(TuiNode parent, int x, int y, int width, int height)
    {
        foreach (var child in parent.LayoutChildren.Where(child => child.Position == TuiPosition.Absolute && child.TagName != "modal"))
        {
            var availableWidth = Cells((long)width - (child.Left ?? 0) - (child.Right ?? 0));
            var childWidth = Math.Min(availableWidth, child.Width ?? (child.Left.HasValue && child.Right.HasValue ? availableWidth : NaturalWidth(child)));
            var availableHeight = Cells((long)height - (child.Top ?? 0) - (child.Bottom ?? 0));
            var childHeight = Math.Min(availableHeight, child.Height ?? (child.Top.HasValue && child.Bottom.HasValue ? availableHeight : NaturalHeight(child, childWidth)));
            var left = child.Left ?? (child.Right.HasValue ? Math.Max(0, width - child.Right.Value - childWidth) : 0);
            var top = child.Top ?? (child.Bottom.HasValue ? Math.Max(0, height - child.Bottom.Value - childHeight) : 0);
            Layout(child, Coordinate((long)x + left), Coordinate((long)y + top), childWidth, childHeight);
        }
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
        left = Math.Max(left, node.X);
        top = Math.Max(top, node.Y);
        right = Math.Min(right, (long)node.X + node.LayoutWidth);
        bottom = Math.Min(bottom, (long)node.Y + node.LayoutHeight);
        if (x < left || x >= right || y < top || y >= bottom) return null;
        foreach (var child in node.PaintChildren.Reverse())
            if (Hit(child, left, top, right, bottom, x, y) is { } hit) return hit;
        return node;
    }

    private void Paint(TuiNode node, uint buffer, uint renderer, NativeRgba foreground, NativeRgba background,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        var left = Math.Max(clipLeft, node.X);
        var top = Math.Max(clipTop, node.Y);
        var right = (int)Math.Min(clipRight, (long)node.X + node.LayoutWidth);
        var bottom = (int)Math.Min(clipBottom, (long)node.Y + node.LayoutHeight);
        if (right <= left || bottom <= top) return;
        var fg = node.Fg ?? foreground;
        var bg = node.Bg ?? background;
        OpenTuiNative.PushScissor(buffer, left, top, (uint)(right - left), (uint)(bottom - top));
        try
        {
            if (node.Bg.HasValue) OpenTuiNative.FillRect(buffer, left, top, right - left, bottom - top, bg);
            void Draw(int x, int y, ReadOnlySpan<char> text, uint? attributes = null, NativeRgba? foreground = null, NativeRgba? background = null)
            {
                var style = attributes ?? ((node.Bold ? 1u : 0) | (node.Dim ? 2u : 0));
                if (text.Contains('\t')) OpenTuiNative.DrawText(buffer, text.ToString().Replace("\t", "  "), (uint)x, (uint)y, foreground ?? fg, background ?? bg, style);
                else OpenTuiNative.DrawText(buffer, text, (uint)x, (uint)y, foreground ?? fg, background ?? bg, style);
            }
            var selectionFg = node.SelectionForeground ?? Colors.SelectionForeground;
            var selectionBg = node.SelectionBackground ?? Colors.SelectionBackground;
            if (node.BorderStyle is not null)
            {
                // Native side flags: left=1, bottom=2, right=4, top=8.
                OpenTuiNative.DrawBox(buffer, node.X, node.Y, (uint)node.LayoutWidth, (uint)node.LayoutHeight,
                    node.BorderStyle is "left" or "heavy" ? HeavyBorder : node.BorderStyle == "double" ? DoubleBorder : node.BorderStyle == "rounded" ? RoundedBorder : SingleBorder,
                    node.BorderStyle == "left" ? 1u : 15u, node.BorderFg ?? fg, bg);
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
                if (!Nullable.Equals(node.NativeForeground, fg) || !Nullable.Equals(node.NativeBackground, bg) || node.NativeAttributes != attributes)
                {
                    view.SetStyle(fg, bg, attributes);
                    node.NativeForeground = fg; node.NativeBackground = bg; node.NativeAttributes = attributes;
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
                // Literal one-row controls have no wrapping viewport. Draw their exact
                // cells into the native buffer; parent fill/scissors own the remainder.
                if (node.Height == 1 && node.TextRuns.IsDefault && !node.Tail && node.Scroll == 0 && !node.Selectable)
                {
                    var line = node.Content.AsSpan();
                    var newline = line.IndexOfAny('\r', '\n');
                    if (newline >= 0) line = line[..newline];
                    Draw(node.X, node.Y, line);
                    return;
                }
                var view = SelectionView(node);
                var attributes = (node.Bold ? 1u : 0) | (node.Dim ? 2u : 0);
                if (!Nullable.Equals(node.NativeForeground, fg) || !Nullable.Equals(node.NativeBackground, bg) || node.NativeAttributes != attributes)
                {
                    view.SetStyle(fg, bg, attributes);
                    node.NativeForeground = fg;
                    node.NativeBackground = bg;
                    node.NativeAttributes = attributes;
                }
                // Selection and paint share this exact viewport. Ancestor scissor
                // rectangles clip drawing, not the view's local coordinate space.
                view.Draw(buffer, node.X, node.Y);
            }
            foreach (var child in node.PaintChildren) Paint(child, buffer, renderer, fg, bg, left, top, right, bottom);
        }
        finally
        {
            OpenTuiNative.PopScissor(buffer);
        }
    }

    public int CellWidth(string text) => CellWidth(text.AsSpan());

    private int IntrinsicTextWidth(TuiNode node)
    {
        if (node.IntrinsicWidth is { } cached && node.IntrinsicWidthMethod == _widthMethod
            && node.IntrinsicText == node.Content && node.IntrinsicRuns == node.TextRuns) return cached;
        var text = node.TextRuns.IsDefault ? node.Content : string.Concat(node.TextRuns.Select(run => Encoding.UTF8.GetString(run.Text.Span)));
        var width = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var remaining = line;
            var columns = 0;
            while (remaining.IndexOf('\t') is var tab && tab >= 0)
            {
                columns = checked(columns + CellWidth(remaining[..tab]) + 2);
                remaining = remaining[(tab + 1)..];
            }
            width = Math.Max(width, checked(columns + CellWidth(remaining)));
        }
        node.IntrinsicText = node.Content;
        node.IntrinsicRuns = node.TextRuns;
        node.IntrinsicWidthMethod = _widthMethod;
        node.IntrinsicWidth = width;
        return width;
    }

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
