namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

public enum ModalSize { Medium, Large, ExtraLarge }

/// <summary>An absolute terminal overlay. The renderer scopes focus and Escape to the top modal.</summary>
public sealed class Modal : ComponentBase
{
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public bool Centered { get; set; }
    [Parameter] public string Background { get; set; } = "#202020";
    [Parameter] public string Backdrop { get; set; } = "#00000096";
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnDismiss { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "modal");
        builder.AddAttribute(1, "center", Centered);
        builder.AddAttribute(2, "bg", Backdrop);
        builder.AddAttribute(3, "onclose", OnClose);
        builder.AddAttribute(4, "z-index", 3000);
        builder.AddAttribute(5, "onclick", EventCallback.Factory.Create<TerminalPointerEventArgs>(this, async args =>
        {
            args.Handled = true;
            await (OnDismiss.HasDelegate ? OnDismiss : OnClose).InvokeAsync();
        }));
        builder.OpenComponent<Box>(6);
        builder.AddAttribute(7, "Width", Size switch { ModalSize.Large => 88, ModalSize.ExtraLarge => 116, _ => 60 });
        builder.AddAttribute(8, "Bg", Background);
        builder.AddAttribute(9, "PaddingTop", 1);
        builder.AddAttribute(10, "OnClick", EventCallback.Factory.Create<TerminalPointerEventArgs>(this, args => args.Handled = true));
        builder.AddAttribute(11, "ChildContent", ChildContent);
        builder.CloseComponent();
        builder.CloseElement();
    }
}
