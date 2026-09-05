namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;
using OpenTui.Native;

public sealed partial class TuiLayoutEngine
{
    private sealed class YogaEntry(nint handle, TuiNode? node)
    {
        internal nint Handle { get; } = handle;
        internal TuiNode? Node { get; } = node;
        internal List<YogaEntry> Children { get; } = [];
        internal YogaEntry? ScrollContent;
    }

    private void LayoutWithYoga(TuiNode root, int width, int height)
    {
        // A complete native transaction per layout avoids stale Yoga parentage,
        // callbacks and measure targets after a Blazor remove/reparent batch.
        // Native text views remain retained by their existing render nodes.
        using var yoga = new NativeYogaTree();
        var tree = BuildYoga(yoga, root);
        yoga.SetValue(tree.Handle, NativeYogaValue.Width, NativeYogaUnit.Point, width);
        yoga.SetValue(tree.Handle, NativeYogaValue.Height, NativeYogaUnit.Point, height);
        yoga.Calculate(tree.Handle, width, height);
        FitTables(yoga, tree, width, height);
        ApplyYoga(yoga, tree, 0, 0);

        _modals.Clear();
        FindModals(root);
        var ordered = _modals.OrderBy(modal => modal.ZIndex).ToArray();
        _modals.Clear(); _modals.AddRange(ordered);
        foreach (var modal in _modals)
        {
            modal.X = modal.Y = 0;
            modal.LayoutWidth = width; modal.LayoutHeight = height;
            // Modal placement is the existing overlay policy, expressed as Yoga
            // roots. It is not another flow allocator and does not occupy root flow.
            foreach (var panel in modal.FlowChildren)
            {
                using var overlay = new NativeYogaTree();
                var anchor = new YogaEntry(overlay.CreateNode(), null);
                overlay.SetValue(anchor.Handle, NativeYogaValue.Width, NativeYogaUnit.Point, width);
                overlay.SetValue(anchor.Handle, NativeYogaValue.Height, NativeYogaUnit.Point, height);
                overlay.SetEnum(anchor.Handle, NativeYogaEnum.AlignItems, (uint)TuiAlign.Center);
                if (modal.Center) overlay.SetEnum(anchor.Handle, NativeYogaEnum.JustifyContent, (uint)TuiJustify.Center);
                else overlay.SetValue(anchor.Handle, NativeYogaValue.Padding, NativeYogaUnit.Point, height / 4, 1);
                var child = BuildYoga(overlay, panel);
                if (panel.Width is null && panel.WidthValue is null)
                    overlay.SetValue(child.Handle, NativeYogaValue.Width, NativeYogaUnit.Point, 60);
                if (panel.MaxWidth is null) overlay.SetValue(child.Handle, NativeYogaValue.MaxWidth, NativeYogaUnit.Point, Math.Max(0, width - 2));
                if (panel.MaxHeightValue is null) overlay.SetValue(child.Handle, NativeYogaValue.MaxHeight, NativeYogaUnit.Point,
                    Math.Max(0, height - (modal.Center ? 0 : height / 4) - 1));
                anchor.Children.Add(child);
                overlay.Insert(anchor.Handle, child.Handle, 0);
                overlay.Calculate(anchor.Handle, width, height);
                FitTables(overlay, anchor, width, height);
                ApplyYoga(overlay, anchor, 0, 0);
            }
        }
    }

    private YogaEntry BuildYoga(NativeYogaTree yoga, TuiNode node)
    {
        var entry = new YogaEntry(yoga.CreateNode(), node);
        var handle = entry.Handle;
        var width = node.WidthValue ?? (node.Width is { } w ? TuiLength.Cells(w) : (TuiLength?)null);
        var height = node.HeightValue ?? (node.Height is { } h ? TuiLength.Cells(h) : (TuiLength?)null);
        SetLength(yoga, handle, NativeYogaValue.Width, width);
        SetLength(yoga, handle, NativeYogaValue.Height, height);
        SetLength(yoga, handle, NativeYogaValue.MinWidth, node.MinWidth);
        SetLength(yoga, handle, NativeYogaValue.MinHeight, node.MinHeight ?? (node.TagName == "input" ? TuiLength.Cells(1) : null));
        SetLength(yoga, handle, NativeYogaValue.MaxWidth, node.MaxWidth);
        SetLength(yoga, handle, NativeYogaValue.MaxHeight, node.MaxHeightValue ??
            (node.TagName == "input" ? TuiLength.Cells(node.MaxHeight) :
                node.TextMaxHeight is { } max && node.Height is null ? TuiLength.Cells(max) : null));
        SetLength(yoga, handle, NativeYogaValue.Basis, node.FlexBasis);
        yoga.SetFloat(handle, NativeYogaFloat.Grow, node.FlexGrow ?? node.Grow);
        yoga.SetFloat(handle, NativeYogaFloat.Shrink, node.FlexShrink ?? node.Shrink ??
            (width?.Unit == NativeYogaUnit.Point || height?.Unit == NativeYogaUnit.Point ? 0 : 1));
        yoga.SetEnum(handle, NativeYogaEnum.FlexDirection, node.Direction switch
        {
            TuiFlexDirection.Column => 0, TuiFlexDirection.ColumnReverse => 1,
            TuiFlexDirection.Row => 2, TuiFlexDirection.RowReverse => 3, _ => throw new InvalidOperationException("Invalid flex direction.")
        });
        yoga.SetEnum(handle, NativeYogaEnum.JustifyContent, (uint)node.JustifyContent);
        var align = node.AlignItems ?? (node.Center ? TuiAlign.Center : node.CrossAlignment switch
        {
            TuiCrossAlignment.Start => TuiAlign.Start, TuiCrossAlignment.Center => TuiAlign.Center,
            TuiCrossAlignment.End => TuiAlign.End, _ => TuiAlign.Stretch
        });
        yoga.SetEnum(handle, NativeYogaEnum.AlignItems, (uint)(align == TuiAlign.Auto ? TuiAlign.Stretch : align));
        yoga.SetEnum(handle, NativeYogaEnum.AlignSelf, (uint)node.AlignSelf);
        yoga.SetEnum(handle, NativeYogaEnum.AlignContent, (uint)node.AlignContent);
        yoga.SetEnum(handle, NativeYogaEnum.FlexWrap, (uint)node.FlexWrap);
        yoga.SetEnum(handle, NativeYogaEnum.Overflow, node.ScrollState is not null ? 2u : (uint)node.Overflow);
        yoga.SetEnum(handle, NativeYogaEnum.PositionType, node.Position == TuiPosition.Absolute ? 2u : 1u);
        yoga.SetValue(handle, NativeYogaValue.Gap, NativeYogaUnit.Point, node.Gap, 2);
        SetEdge(yoga, handle, NativeYogaValue.Position, 0, node.Left);
        SetEdge(yoga, handle, NativeYogaValue.Position, 1, node.Top);
        SetEdge(yoga, handle, NativeYogaValue.Position, 2, node.Right);
        SetEdge(yoga, handle, NativeYogaValue.Position, 3, node.Bottom);
        yoga.SetValue(handle, NativeYogaValue.Padding, NativeYogaUnit.Point, node.PaddingLeft, 0);
        yoga.SetValue(handle, NativeYogaValue.Padding, NativeYogaUnit.Point, node.PaddingTop, 1);
        yoga.SetValue(handle, NativeYogaValue.Padding, NativeYogaUnit.Point, node.PaddingRight, 2);
        yoga.SetValue(handle, NativeYogaValue.Padding, NativeYogaUnit.Point, node.PaddingBottom, 3);
        yoga.SetBorder(handle, 0, node.BorderLeft); yoga.SetBorder(handle, 1, node.BorderTop);
        yoga.SetBorder(handle, 2, node.BorderRight); yoga.SetBorder(handle, 3, node.BorderBottom);

        if (node.Editor is { } editor)
        {
            if (editor.IsDisposed) { yoga.SetEnum(handle, NativeYogaEnum.Display, 1); return entry; }
            editor.EnsureNative(_widthMethod);
            editor.SetWrapMode(node.WrapMode);
            editor.Editor.SetPlaceholder(node.Placeholder, node.PlaceholderFg ?? new NativeRgba(102, 102, 102));
            if (node.MinHeight is null) yoga.SetValue(handle, NativeYogaValue.MinHeight, NativeYogaUnit.Point, 1);
            if (!editor.Visible) yoga.SetEnum(handle, NativeYogaEnum.Display, 1);
            yoga.MeasureEditor(handle, editor.Editor);
            return entry;
        }

        if (node.TagName == "input")
        {
            var view = TextView(node);
            yoga.Measure(handle, (availableWidth, widthMode, availableHeight, heightMode) =>
            {
                var constrained = widthMode != NativeYogaMeasureMode.Undefined && float.IsFinite(availableWidth);
                var columns = constrained ? Math.Max(1, Cells((long)Math.Floor(availableWidth))) : 0;
                var measured = view.Measure(columns);
                // This is the existing controlled editor's sole caret map, not
                // another flex allocator. Retain its end-of-line caret row until
                // E6 replaces Input with one authoritative native EditorView owner.
                var input = InputLayout(node, columns > 0 ? columns : Math.Max(1, checked((int)measured.WidthColumns)));
                var measuredWidth = Math.Max(1f, measured.WidthColumns);
                var measuredHeight = (float)input.Lines.Count;
                if (widthMode == NativeYogaMeasureMode.AtMost && float.IsFinite(availableWidth)) measuredWidth = Math.Min(availableWidth, measuredWidth);
                if (heightMode == NativeYogaMeasureMode.AtMost && float.IsFinite(availableHeight)) measuredHeight = Math.Min(availableHeight, measuredHeight);
                return new NativeYogaSize(Math.Max(0, measuredWidth), Math.Max(0, measuredHeight));
            });
            return entry;
        }
        if (node.TagName is "text" or "#text")
        {
            yoga.MeasureText(handle, TextView(node));
            return entry;
        }
        var parent = entry;
        if (node.ScrollState is not null)
        {
            // Source scroll views separate constrained viewport from an auto-height
            // content box. Yoga computes all row offsets/heights; state adds only translation.
            parent = new(yoga.CreateNode(), null);
            entry.ScrollContent = parent;
            yoga.SetValue(parent.Handle, NativeYogaValue.Width, NativeYogaUnit.Percent, 100);
            yoga.SetFloat(parent.Handle, NativeYogaFloat.Shrink, 0);
            yoga.SetValue(parent.Handle, NativeYogaValue.Gap, NativeYogaUnit.Point, node.Gap, 2);
            entry.Children.Add(parent);
            yoga.Insert(entry.Handle, parent.Handle, 0);
        }
        foreach (var child in node.LayoutChildren.Where(child => child.TagName != "modal"))
        {
            var next = BuildYoga(yoga, child);
            var owner = child.Position == TuiPosition.Absolute ? entry : parent;
            yoga.Insert(owner.Handle, next.Handle, checked((uint)owner.Children.Count));
            owner.Children.Add(next);
        }
        return entry;
    }

    private static void SetLength(NativeYogaTree yoga, nint node, NativeYogaValue kind, TuiLength? value)
    {
        if (value is { } size) yoga.SetValue(node, kind, size.Unit, size.Value);
    }
    private static void SetEdge(NativeYogaTree yoga, nint node, NativeYogaValue kind, uint edge, int? value)
    {
        if (value is { } size) yoga.SetValue(node, kind, NativeYogaUnit.Point, size, edge);
    }

    private void ApplyYoga(NativeYogaTree yoga, YogaEntry entry, int x, int y)
    {
        var layout = yoga.Layout(entry.Handle);
        x = Coordinate((long)x + LayoutCoordinate(layout.Left));
        y = Coordinate((long)y + LayoutCoordinate(layout.Top));
        if (entry.Node is { } node)
        {
            node.X = x; node.Y = y;
            node.LayoutWidth = LayoutSize(layout.Width); node.LayoutHeight = LayoutSize(layout.Height);
            if (node.ScrollState is { } scroll && entry.ScrollContent is { } content)
            {
                var rows = content.Children.Where(child => child.Node is not null).Select(child =>
                {
                    var row = yoga.Layout(child.Handle);
                    return new TerminalScrollState.Row(child.Node!, LayoutSize(row.Top), LayoutSize(row.Height));
                }).ToArray();
                scroll.Update(rows, LayoutSize(yoga.Layout(content.Handle).Height),
                    Math.Max(0, node.LayoutHeight - node.PaddingTop - node.PaddingBottom - node.BorderTop - node.BorderBottom));
            }
        }
        foreach (var child in entry.Children)
            ApplyYoga(yoga, child, x, ReferenceEquals(child, entry.ScrollContent) ? Coordinate((long)y - entry.Node!.ScrollState!.Offset) : y);
    }

    private static int LayoutSize(float value) => float.IsFinite(value) ? Cells((long)Math.Round(value, MidpointRounding.AwayFromZero)) : 0;
    private static int LayoutCoordinate(float value) => float.IsFinite(value) ? Coordinate((long)Math.Round(value, MidpointRounding.AwayFromZero)) : 0;

    private void FitTables(NativeYogaTree yoga, YogaEntry root, int width, int height)
    {
        foreach (var table in Walk(root).Where(entry => entry.Node?.SharedColumns == true))
        {
            var rows = table.Children.Where(row => row.Node?.Position != TuiPosition.Absolute).ToArray();
            var columns = rows.Select(row => row.Children.Count).DefaultIfEmpty(0).Max();
            if (columns == 0) continue;
            var intrinsic = new int[columns];
            var minimum = 1;
            foreach (var row in rows)
                for (var index = 0; index < row.Children.Count; index++)
                {
                    var cell = row.Children[index].Node!;
                    using var measure = new NativeYogaTree();
                    var probe = BuildYoga(measure, cell);
                    measure.SetEnum(probe.Handle, NativeYogaEnum.PositionType, 1);
                    measure.Calculate(probe.Handle);
                    intrinsic[index] = Math.Max(intrinsic[index], LayoutSize(measure.Layout(probe.Handle).Width));
                    minimum = Math.Max(minimum, 1 + cell.PaddingLeft + cell.PaddingRight + cell.BorderLeft + cell.BorderRight);
                }
            var available = rows.Select(row => LayoutSize(yoga.Layout(row.Handle).Width) -
                row.Node!.PaddingLeft - row.Node.PaddingRight - row.Node.BorderLeft - row.Node.BorderRight -
                Math.Max(0, columns - 1) * row.Node.Gap).DefaultIfEmpty(0).Min();
            var tracks = TableTrackWidths.Fit(intrinsic, Math.Max(0, available), minimum);
            foreach (var row in rows)
                for (var index = 0; index < row.Children.Count; index++)
                {
                    var cell = row.Children[index].Handle;
                    yoga.SetValue(cell, NativeYogaValue.Width, NativeYogaUnit.Point, tracks[index]);
                    yoga.SetFloat(cell, NativeYogaFloat.Grow, 0);
                    yoga.SetFloat(cell, NativeYogaFloat.Shrink, 0);
                }
            // The track fitter determines shared widths only. Yoga still computes
            // every cell/row/container height and position, including nested tables.
            yoga.Calculate(root.Handle, width, height);
        }
    }

    private static IEnumerable<YogaEntry> Walk(YogaEntry entry)
    {
        yield return entry;
        foreach (var child in entry.Children)
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}
