namespace OpenTui.Blazor.Rendering;

using OpenTui.Blazor.Nodes;

public sealed partial class TuiRenderer
{
    internal bool DispatchImageEvents()
    {
        var changed = false;
        foreach (var node in ImageNodes(RootNode).ToArray())
            if (Attached(node)) changed |= node.Image!.FlushRenderEvent();
        return changed;
    }
    private static IEnumerable<TuiNode> ImageNodes(TuiNode node)
    {
        if (node.Image is not null) yield return node;
        foreach (var child in node.LayoutChildren)
            foreach (var image in ImageNodes(child)) yield return image;
    }
}
