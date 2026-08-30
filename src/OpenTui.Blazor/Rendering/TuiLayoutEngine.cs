namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;
using OpenTui.Native;

public static class TuiLayoutEngine
{
    public static void LayoutAndRender(TuiNode root, int terminalWidth, int terminalHeight, Action<int, int, string, NativeRgba?, NativeRgba?, bool, bool> drawText, Action<int, int, int, int, NativeRgba?, NativeRgba?> fillRect)
    {
        // 1. Layout pass: compute positions and dimensions
        ComputeLayout(root, 0, 0, terminalWidth, terminalHeight);

        // 2. Render pass: draw nodes to terminal grid
        RenderNode(root, drawText, fillRect);
    }

    private static void ComputeLayout(TuiNode node, int x, int y, int availableWidth, int availableHeight)
    {
        int width = node.Width ?? availableWidth;
        int height = node.Height ?? availableHeight;

        // Content area inside padding
        int contentX = x + node.PaddingLeft;
        int contentY = y + node.PaddingTop;
        int contentWidth = Math.Max(0, width - node.PaddingLeft - node.PaddingRight);
        int contentHeight = Math.Max(0, height - node.PaddingTop - node.PaddingBottom);

        if (node.Direction == TuiFlexDirection.Column)
        {
            int currentY = contentY;
            foreach (var child in node.Children)
            {
                int childHeight = child.Height ?? Math.Max(1, (contentHeight - (node.Children.Count - 1) * node.Gap) / Math.Max(1, node.Children.Count));
                ComputeLayout(child, contentX, currentY, contentWidth, childHeight);
                currentY += childHeight + node.Gap;
            }
        }
        else // Row
        {
            int currentX = contentX;
            foreach (var child in node.Children)
            {
                int childWidth = child.Width ?? Math.Max(1, (contentWidth - (node.Children.Count - 1) * node.Gap) / Math.Max(1, node.Children.Count));
                ComputeLayout(child, currentX, contentY, childWidth, contentHeight);
                currentX += childWidth + node.Gap;
            }
        }
    }

    private static void RenderNode(TuiNode node, Action<int, int, string, NativeRgba?, NativeRgba?, bool, bool> drawText, Action<int, int, int, int, NativeRgba?, NativeRgba?> fillRect)
    {
        // Draw background if set
        if (node.Bg.HasValue && node.Width.HasValue && node.Height.HasValue)
        {
            fillRect(0, 0, node.Width.Value, node.Height.Value, node.Fg, node.Bg);
        }

        // Draw text content if present
        if (!string.IsNullOrEmpty(node.TextContent))
        {
            drawText(0, 0, node.TextContent, node.Fg, node.Bg, node.Bold, node.Dim);
        }

        // Render children
        foreach (var child in node.Children)
        {
            RenderNode(child, drawText, fillRect);
        }
    }
}
