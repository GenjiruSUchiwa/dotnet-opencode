namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTui.Native;

/// <summary>Draws caller-owned decoded image state through native bufferDrawImage.</summary>
public sealed class TuiImage : PointerComponentBase, IDisposable
{
    [Parameter, EditorRequired] public ImageState State { get; set; } = null!;
    [Parameter] public ImageFit Fit { get; set; } = ImageFit.Fit;
    [Parameter] public NativeImageProtocol Protocol { get; set; } = NativeImageProtocol.Auto;
    [Parameter] public int? Width { get; set; }
    [Parameter] public int? Height { get; set; }
    [Parameter] public int Grow { get; set; }
    private ImageState? _subscribed;
    protected override void OnParametersSet()
    {
#pragma warning disable MA0015 // Fit is a Blazor component parameter; retain its existing diagnostic identity.
        if (!Enum.IsDefined(Fit) || !Enum.IsDefined(Protocol)) throw new ArgumentOutOfRangeException(nameof(Fit));
#pragma warning restore MA0015
        if (ReferenceEquals(_subscribed, State)) return;
        if (_subscribed is not null) _subscribed.Changed -= Refresh;
        _subscribed = State;
        State.Changed += Refresh;
    }
    private void Refresh() => StateHasChanged();
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "image");
        builder.AddAttribute(1, "width", Width);
        builder.AddAttribute(2, "height", Height);
        builder.AddAttribute(3, "grow", Grow);
        AddPointerAttributes(builder);
        builder.CloseElement();
    }
    public void Dispose() { if (_subscribed is not null) _subscribed.Changed -= Refresh; }
}
