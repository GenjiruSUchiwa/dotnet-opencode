namespace OpenTui.Blazor.Nodes;

using OpenTui.Native;

public enum TuiFlexDirection
{
    Row,
    Column
}

public class TuiNode
{
    public string TagName { get; set; } = "box";
    public TuiNode? Parent { get; set; }
    public List<TuiNode> Children { get; } = [];

    public TuiFlexDirection Direction { get; set; } = TuiFlexDirection.Column;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int PaddingLeft { get; set; }
    public int PaddingRight { get; set; }
    public int PaddingTop { get; set; }
    public int PaddingBottom { get; set; }
    public int Gap { get; set; }

    public NativeRgba? Fg { get; set; }
    public NativeRgba? Bg { get; set; }
    public bool Bold { get; set; }
    public bool Dim { get; set; }
    public string? BorderStyle { get; set; }

    public string TextContent { get; set; } = "";

    public void AddChild(TuiNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void RemoveChild(TuiNode child)
    {
        Children.Remove(child);
        if (child.Parent == this) child.Parent = null;
    }
}
