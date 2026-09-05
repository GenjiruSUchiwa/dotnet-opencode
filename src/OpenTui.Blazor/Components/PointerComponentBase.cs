namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

public abstract class PointerComponentBase : ComponentBase
{
    [Parameter] public bool PointerEvents { get; set; } = true;
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnPointerDown { get; set; }
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnPointerUp { get; set; }
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnPointerMove { get; set; }
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnPointerEnter { get; set; }
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnPointerLeave { get; set; }
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnClick { get; set; }
    [Parameter] public EventCallback<TerminalPointerEventArgs> OnWheel { get; set; }
    [Parameter] public EventCallback<TerminalRoutedKeyEventArgs> OnKeyInput { get; set; }
    [Parameter] public EventCallback<TerminalRoutedPasteEventArgs> OnPasteInput { get; set; }

    // Call after component-specific attributes (<100), before content (>=108).
    protected void AddPointerAttributes(RenderTreeBuilder builder)
    {
        builder.AddAttribute(98, "onkeyinput", OnKeyInput);
        builder.AddAttribute(99, "onpasteinput", OnPasteInput);
        builder.AddAttribute(100, "pointer-events", PointerEvents.ToString());
        builder.AddAttribute(101, "onpointerdown", OnPointerDown);
        builder.AddAttribute(102, "onpointerup", OnPointerUp);
        builder.AddAttribute(103, "onpointermove", OnPointerMove);
        builder.AddAttribute(104, "onpointerenter", OnPointerEnter);
        builder.AddAttribute(105, "onpointerleave", OnPointerLeave);
        builder.AddAttribute(106, "onclick", OnClick);
        builder.AddAttribute(107, "onwheel", OnWheel);
    }
}
