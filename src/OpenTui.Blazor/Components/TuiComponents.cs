namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTui.Native;

public class Box : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string Direction { get; set; } = "column";
    [Parameter] public string? Border { get; set; }
    [Parameter] public int? Width { get; set; }
    [Parameter] public int? Height { get; set; }
    [Parameter] public int Gap { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "box");
        builder.AddAttribute(1, "direction", Direction);
        if (Border is not null) builder.AddAttribute(2, "border", Border);
        if (Width.HasValue) builder.AddAttribute(3, "width", Width.Value);
        if (Height.HasValue) builder.AddAttribute(4, "height", Height.Value);
        if (Gap > 0) builder.AddAttribute(5, "gap", Gap);
        if (ChildContent is not null) builder.AddContent(6, ChildContent);
        builder.CloseElement();
    }
}

public class Text : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? Value { get; set; }
    [Parameter] public string? Fg { get; set; }
    [Parameter] public bool Bold { get; set; }
    [Parameter] public bool Dim { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        if (Fg is not null) builder.AddAttribute(1, "fg", Fg);
        if (Bold) builder.AddAttribute(2, "bold", true);
        if (Dim) builder.AddAttribute(3, "dim", true);

        if (!string.IsNullOrEmpty(Value))
        {
            builder.AddContent(4, Value);
        }
        else if (ChildContent is not null)
        {
            builder.AddContent(5, ChildContent);
        }

        builder.CloseElement();
    }
}

public class Wordmark : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // 1. Subtle "dotnet" in .NET blurple character coloring directly ABOVE the opencode wordmark
        builder.OpenComponent<Box>(0);
        builder.AddAttribute(1, "Direction", "column");
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(b =>
        {
            // The subtle dotnet header in .NET brand blurple (#7B61FF / RGB 123,97,255)
            b.OpenComponent<Text>(10);
            b.AddAttribute(11, "Value", "      d  o  t  n  e  t");
            b.AddAttribute(12, "Fg", "#7B61FF");
            b.AddAttribute(13, "Bold", true);
            b.CloseComponent();

            // The OpenCode Wordmark lines
            b.OpenComponent<Text>(20);
            b.AddAttribute(21, "Value", "█▀▀█ █▀▀█ █▀▀█ █▀▀▄ █▀▀▀ █▀▀█ █▀▀█ █▀▀█");
            b.AddAttribute(22, "Bold", true);
            b.CloseComponent();

            b.OpenComponent<Text>(30);
            b.AddAttribute(31, "Value", "█  █ █  █ █▀▀▀ █  █ █    █  █ █  █ █▀▀▀");
            b.AddAttribute(32, "Bold", true);
            b.CloseComponent();

            b.OpenComponent<Text>(40);
            b.AddAttribute(41, "Value", "▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀  ▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀");
            b.AddAttribute(42, "Bold", true);
            b.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
