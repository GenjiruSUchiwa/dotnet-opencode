namespace OpenTui.Blazor.TextMarks;

using OpenTui.Blazor.Nodes;
using OpenTui.Native;

/// <summary>The width method from an input that has actually been laid out. This
/// bridge uses the existing cell-width API, not a new native extmark function.</summary>
public sealed record TerminalTextMarkMetrics(byte WidthMethod)
{
    public int Measure(string element) => OpenTuiNative.MeasureCellWidth(element, WidthMethod);

    public static TerminalTextMarkMetrics? FromInput(TuiNode root, string focusKey)
    {
        if (root.TagName == "input" && root.FocusKey == focusKey && root.InputLayout is not null)
            return new(root.InputWidthMethod);
        foreach (var child in root.LayoutChildren)
            if (FromInput(child, focusKey) is { } metrics) return metrics;
        return null;
    }
}
