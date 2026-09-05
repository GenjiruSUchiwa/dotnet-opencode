namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTui.Native;

/// <summary>Native-owned editor. Set/insert through State, not a competing controlled Value/cursor pair.</summary>
public sealed class Textarea : LayoutComponentBase, IDisposable
{
    [Parameter, EditorRequired] public TextareaState State { get; set; } = null!;
    [Parameter] public string? FocusKey { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public string Fg { get; set; } = "#ffffff";
    [Parameter] public string Bg { get; set; } = "transparent";
    [Parameter] public string? FocusedFg { get; set; }
    [Parameter] public string? FocusedBg { get; set; }
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public string PlaceholderFg { get; set; } = "#666666";
    [Parameter] public NativeRgba CursorColor { get; set; } = NativeRgba.White;
    [Parameter] public NativeRgba? SelectionForeground { get; set; }
    [Parameter] public NativeRgba? SelectionBackground { get; set; }
    [Parameter] public NativeCursorAppearance CursorStyle { get; set; } = new(0, true);
    [Parameter] public NativeTextWrapMode WrapMode { get; set; } = NativeTextWrapMode.Word;
    [Parameter] public IReadOnlyList<NativeSyntaxRule> SyntaxRules { get; set; } = [];
    [Parameter] public EventCallback<TextareaChangeEventArgs> OnContentChange { get; set; }
    [Parameter] public EventCallback<TextareaChangeEventArgs> OnCursorChange { get; set; }
    [Parameter] public EventCallback<TextareaSubmitEventArgs> OnSubmit { get; set; }
    [Parameter] public EventCallback<TerminalFocusEventArgs> OnFocusChange { get; set; }
    [Parameter] public EventCallback OnReady { get; set; }
    private TextareaState? _subscribed;
    internal IReadOnlyList<NativeSyntaxRule> Rules { get; private set; } = [];

    protected override void OnParametersSet()
    {
        if (State is null) throw new InvalidOperationException("Textarea requires an owned State.");
        if (!ReferenceEquals(_subscribed, State))
        {
            if (_subscribed is not null) { _subscribed.Changed -= Refresh; _subscribed.Dispose(); }
            _subscribed = State; State.Changed += Refresh;
        }
        Rules = Array.AsReadOnly(SyntaxRules.ToArray());
    }
    private void Refresh() => StateHasChanged();
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "textarea");
        builder.AddAttribute(1, "focus-key", FocusKey);
        builder.AddAttribute(2, "fg", Fg);
        builder.AddAttribute(3, "bg", Bg);
        builder.AddAttribute(4, "focused-fg", FocusedFg);
        builder.AddAttribute(5, "focused-bg", FocusedBg);
        builder.AddAttribute(6, "placeholder", Placeholder);
        builder.AddAttribute(7, "placeholder-fg", PlaceholderFg);
        builder.AddAttribute(8, "editor-content", OnContentChange);
        builder.AddAttribute(9, "editor-cursor", OnCursorChange);
        builder.AddAttribute(10, "editor-submit", OnSubmit);
        builder.AddAttribute(11, "editor-ready", OnReady);
        builder.AddAttribute(12, "editor-focus", OnFocusChange);
        builder.AddAttribute(13, "wrap-mode", WrapMode.ToString());
        AddLayoutAttributes(builder);
        AddPointerAttributes(builder);
        builder.CloseElement();
    }
    public void Dispose()
    {
        if (_subscribed is null) return;
        _subscribed.Changed -= Refresh; _subscribed.Dispose(); _subscribed = null;
    }
}
