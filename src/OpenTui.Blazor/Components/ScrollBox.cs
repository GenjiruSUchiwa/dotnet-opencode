namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

public sealed class ScrollBox : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public TerminalScrollState State { get; set; } = default!;
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public int Grow { get; set; } = 1;
    private TerminalScrollState? _subscribed;

    protected override void OnParametersSet()
    {
#pragma warning disable MA0015 // State is the required Blazor component parameter.
        ArgumentNullException.ThrowIfNull(State);
#pragma warning restore MA0015
        if (ReferenceEquals(_subscribed, State)) return;
        if (_subscribed is not null) _subscribed.Changed -= Refresh;
        _subscribed = State;
        State.Changed += Refresh;
    }

    private void Refresh() => StateHasChanged();

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "scroll");
        builder.AddAttribute(1, "grow", Grow);
        builder.AddContent(2, ChildContent);
        builder.CloseElement();
    }

    public void Dispose()
    {
        if (_subscribed is not null) _subscribed.Changed -= Refresh;
    }
}
