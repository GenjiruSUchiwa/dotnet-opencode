namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

public class OpenCodeApp : ComponentBase
{
    [Parameter] public string ActiveModel { get; set; } = "Gemini 3.7 Flash";
    [Parameter] public string ActiveAgent { get; set; } = "Build";
    [Parameter] public int ServerPort { get; set; } = 5055;
    [Parameter] public string CurrentDirectory { get; set; } = "";
    [Parameter] public string? CurrentPrompt { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Box>(0);
        builder.AddAttribute(1, "Direction", "column");
        builder.AddAttribute(2, "ChildContent", (RenderFragment)(b =>
        {
            // 1. The Wordmark with subtle "dotnet" in blurple directly ABOVE "opencode"
            b.OpenComponent<Wordmark>(10);
            b.CloseComponent();

            // 2. Center Prompt Box
            b.OpenComponent<Box>(20);
            b.AddAttribute(21, "Direction", "column");
            b.AddAttribute(22, "Border", "double");
            b.AddAttribute(23, "ChildContent", (RenderFragment)(card =>
            {
                card.OpenComponent<Text>(24);
                card.AddAttribute(25, "Value", string.IsNullOrEmpty(CurrentPrompt) ? "Ask anything… \"Fix a bug in SessionStore\"" : CurrentPrompt);
                card.CloseComponent();

                card.OpenComponent<Text>(26);
                card.AddAttribute(27, "Value", $"{ActiveAgent} · {ActiveModel}");
                card.AddAttribute(28, "Fg", "#569CF5");
                card.CloseComponent();
            }));
            b.CloseComponent();

            // 3. Subtitle / paths
            b.OpenComponent<Text>(30);
            b.AddAttribute(31, "Value", $"{CurrentDirectory}   shift+tab agents   ctrl+p commands");
            b.AddAttribute(32, "Dim", true);
            b.CloseComponent();

            // 4. Status bar footer
            b.OpenComponent<Text>(40);
            b.AddAttribute(41, "Value", $"✓ Server (port {ServerPort})   ○ UI   Theme   Tools   Experiments            10.0.0-opencode-dotnet");
            b.AddAttribute(42, "Dim", true);
            b.CloseComponent();
        }));
        builder.CloseComponent();
    }
}
