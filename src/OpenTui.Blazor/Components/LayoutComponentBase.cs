namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTui.Blazor.Nodes;

/// <summary>Typed Yoga constraints shared by box/text/input primitives without changing integer callers.</summary>
public abstract class LayoutComponentBase : PointerComponentBase
{
    [Parameter] public TuiLength? WidthValue { get; set; }
    [Parameter] public TuiLength? HeightValue { get; set; }
    [Parameter] public TuiLength? MinWidth { get; set; }
    [Parameter] public TuiLength? MinHeight { get; set; }
    [Parameter] public TuiLength? MaxWidth { get; set; }
    [Parameter] public TuiLength? MaxHeightValue { get; set; }
    [Parameter] public TuiLength? FlexBasis { get; set; }
    [Parameter] public float? FlexGrow { get; set; }
    [Parameter] public float? FlexShrink { get; set; }
    [Parameter] public TuiAlign AlignSelf { get; set; } = TuiAlign.Auto;

    protected void AddLayoutAttributes(RenderTreeBuilder builder)
    {
        builder.AddAttribute(80, "width-value", WidthValue?.ToString());
        builder.AddAttribute(81, "height-value", HeightValue?.ToString());
        builder.AddAttribute(82, "min-width", MinWidth?.ToString());
        builder.AddAttribute(83, "min-height", MinHeight?.ToString());
        builder.AddAttribute(84, "max-width", MaxWidth?.ToString());
        builder.AddAttribute(85, "max-height-value", MaxHeightValue?.ToString());
        builder.AddAttribute(86, "flex-basis", FlexBasis?.ToString());
        builder.AddAttribute(87, "flex-grow", FlexGrow?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AddAttribute(88, "flex-shrink", FlexShrink?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.AddAttribute(89, "align-self", AlignSelf.ToString());
    }
}
