namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>View of a caller-owned embedded terminal state. The owner disposes State after input/output callbacks stop.</summary>
public sealed class EmbeddedTerminal : PointerComponentBase, IDisposable
{
    [Parameter, EditorRequired] public EmbeddedTerminalState State { get; set; } = null!;
    [Parameter] public string FocusKey { get; set; } = "embedded-terminal";
    private EmbeddedTerminalState? _subscribed;
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_subscribed, State))
        {
            if (_subscribed is not null) _subscribed.Changed -= Refresh;
            _subscribed = State;
            State.Changed += Refresh;
        }
        State.Initialize();
    }
    private void Refresh() => StateHasChanged();
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "embedded-terminal");
        builder.AddAttribute(1, "width", State.Columns);
        builder.AddAttribute(2, "height", State.Rows);
        builder.AddAttribute(3, "focus-key", FocusKey);
        AddPointerAttributes(builder);
        builder.CloseElement();
    }
    public void Dispose() { if (_subscribed is not null) _subscribed.Changed -= Refresh; }
}
