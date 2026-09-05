namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTui.Blazor.Code;
using OpenTui.Native;

/// <summary>Native code view. Syntax is supplied by a real ICodeHighlighter, never inferred by the renderer.</summary>
public sealed class TuiCode : PointerComponentBase, IAsyncDisposable
{
    [Parameter] public string Content { get; set; } = "";
    [Parameter] public string? Filetype { get; set; }
    [Parameter] public ICodeHighlighter? Highlighter { get; set; }
    [Parameter] public IReadOnlyList<NativeSyntaxRule> SyntaxRules { get; set; } = [];
    [Parameter] public bool Conceal { get; set; } = true;
    [Parameter] public bool DrawUnstyledText { get; set; } = true;
    [Parameter] public bool Streaming { get; set; }
    [Parameter] public string? BaseHighlight { get; set; }
    [Parameter] public NativeTextWrapMode WrapMode { get; set; } = NativeTextWrapMode.Character;
    [Parameter] public string? Fg { get; set; }
    [Parameter] public string? Bg { get; set; }
    [Parameter] public bool Selectable { get; set; } = true;
    private readonly CodeHighlightState _state = new();
    private SharedTreeSitter.Lease? _defaultHighlighter;
    internal CodeDocument Document => _state.Visible ? _state.Document : CodeDocument.Plain("");
    public bool HasParser => _state.HasParser;
    public string? Diagnostic => _state.Diagnostic;
    public Task HighlightingDone => _state.HighlightingDone;

    protected override void OnInitialized() => _state.Changed += Refresh;
    protected override void OnParametersSet() => _state.Update(new(Content, Filetype,
        Highlighter ?? (string.IsNullOrEmpty(Filetype) ? null : _defaultHighlighter ??= SharedTreeSitter.Retain()), SyntaxRules,
        Conceal, DrawUnstyledText, Streaming, BaseHighlight));
    private void Refresh() => StateHasChanged();
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        builder.AddAttribute(1, "fg", Fg);
        builder.AddAttribute(2, "bg", Bg);
        builder.AddAttribute(3, "wrap-mode", WrapMode.ToString());
        builder.AddAttribute(4, "selectable", Selectable);
        AddPointerAttributes(builder);
        builder.AddContent(108, Document.Text);
        builder.CloseElement();
    }
    public async ValueTask DisposeAsync()
    {
        _state.Changed -= Refresh;
        await _state.DisposeAsync();
        if (_defaultHighlighter is not null) await _defaultHighlighter.DisposeAsync();
    }
}
