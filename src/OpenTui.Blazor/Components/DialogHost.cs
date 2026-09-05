namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>Renders the top stack entry. Escape pops; a backdrop click clears the stack.</summary>
public sealed class DialogHost : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public DialogStack Stack { get; set; } = null!;
    [Parameter] public string Background { get; set; } = "#202020";
    [Parameter] public string Backdrop { get; set; } = "#00000096";
    private DialogStack? _subscribed;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(Stack, _subscribed)) return;
        if (_subscribed is not null) _subscribed.Changed -= Refresh;
        _subscribed = Stack;
        Stack.Changed += Refresh;
    }
    private void Refresh() => StateHasChanged();
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (Stack.Current is not { } entry) return;
        builder.OpenComponent<Modal>(0);
        builder.SetKey(entry.Key ?? entry);
        builder.AddAttribute(1, "Size", entry.Size);
        builder.AddAttribute(2, "Centered", entry.Centered);
        builder.AddAttribute(3, "Background", Background);
        builder.AddAttribute(4, "Backdrop", Backdrop);
        builder.AddAttribute(5, "OnClose", EventCallback.Factory.Create(this, Stack.Pop));
        builder.AddAttribute(6, "OnDismiss", EventCallback.Factory.Create(this, Stack.Clear));
        builder.AddAttribute(7, "ChildContent", entry.Content);
        builder.CloseComponent();
    }
    public void Dispose()
    {
        if (_subscribed is not null) _subscribed.Changed -= Refresh;
    }
}
