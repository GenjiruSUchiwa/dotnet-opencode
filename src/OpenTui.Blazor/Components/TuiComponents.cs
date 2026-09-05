namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using OpenTui.Blazor.Nodes;
using System.Collections.Immutable;
using OpenTui.Native;
using OpenTui.Blazor.TextMarks;
using OpenTui.Blazor.Rendering;

public class Box : PointerComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public TuiFlexDirection Direction { get; set; } = TuiFlexDirection.Column;
    [Parameter] public string? Border { get; set; }
    [Parameter] public string? BorderFg { get; set; }
    /// <summary>When supplied, overrides the sides implied by the legacy Border style.</summary>
    [Parameter] public TuiBorderSides? BorderSides { get; set; }
    [Parameter] public TuiBorderCharacters? CustomBorderChars { get; set; }
    [Parameter] public bool ShouldFill { get; set; } = true;
    [Parameter] public TuiOverflow Overflow { get; set; } = TuiOverflow.Visible;
    internal uint[]? BorderCodepoints { get; private set; }
    private TuiBorderCharacters? _previousBorderChars;
    [Parameter] public int? Width { get; set; }
    [Parameter] public int? Height { get; set; }
    [Parameter] public int Gap { get; set; }
    [Parameter] public int Grow { get; set; }
    [Parameter] public int Shrink { get; set; }
    [Parameter] public bool Center { get; set; }
    [Parameter] public bool SharedColumns { get; set; }
    [Parameter] public TuiCrossAlignment CrossAlignment { get; set; }
    [Parameter] public TuiPosition Position { get; set; }
    [Parameter] public int? Left { get; set; }
    [Parameter] public int? Right { get; set; }
    [Parameter] public int? Top { get; set; }
    [Parameter] public int? Bottom { get; set; }
    [Parameter] public int ZIndex { get; set; }
    [Parameter] public string? FocusKey { get; set; }
    [Parameter] public EventCallback<TerminalKeyEventArgs> OnKeyDown { get; set; }
    [Parameter] public EventCallback<TerminalSizeEventArgs> OnSizeChanged { get; set; }
    [Parameter] public string? Bg { get; set; }
    [Parameter] public int PaddingX { get; set; }
    [Parameter] public int PaddingTop { get; set; }
    [Parameter] public int PaddingBottom { get; set; }
    [Parameter] public int? PaddingLeft { get; set; }
    [Parameter] public int? PaddingRight { get; set; }

    protected override void OnParametersSet()
    {
        if (_previousBorderChars == CustomBorderChars) return;
        BorderCodepoints = CustomBorderChars?.ToCodepoints();
        _previousBorderChars = CustomBorderChars;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "box");
        builder.AddAttribute(1, "direction", Direction.ToString());
        builder.AddAttribute(2, "border", Border);
        builder.AddAttribute(3, "width", Width);
        builder.AddAttribute(4, "height", Height);
        builder.AddAttribute(5, "gap", Gap);
        builder.AddAttribute(6, "grow", Grow);
        builder.AddAttribute(7, "center", Center);
        builder.AddAttribute(8, "bg", Bg);
        builder.AddAttribute(9, "padding-x", PaddingX);
        builder.AddAttribute(10, "padding-top", PaddingTop);
        builder.AddAttribute(11, "border-fg", BorderFg);
        builder.AddAttribute(12, "shrink", Shrink);
        builder.AddAttribute(13, "padding-bottom", PaddingBottom);
        builder.AddAttribute(14, "padding-left", PaddingLeft);
        builder.AddAttribute(15, "padding-right", PaddingRight);
        builder.AddAttribute(17, "shared-columns", SharedColumns);
        builder.AddAttribute(18, "cross-alignment", CrossAlignment.ToString());
        builder.AddAttribute(19, "position", Position.ToString());
        builder.AddAttribute(20, "left", Left);
        builder.AddAttribute(21, "right", Right);
        builder.AddAttribute(22, "top", Top);
        builder.AddAttribute(23, "bottom", Bottom);
        builder.AddAttribute(24, "z-index", ZIndex);
        builder.AddAttribute(25, "focus-key", FocusKey);
        builder.AddAttribute(26, "onkeydown", OnKeyDown);
        builder.AddAttribute(27, "onsizechanged", OnSizeChanged);
        builder.AddAttribute(28, "border-sides", BorderSides is { } sides ? (uint?)sides : null);
        // False boolean element attributes are omitted by RenderTreeBuilder.
        // Keep false explicit because the source/default fill policy is true.
        builder.AddAttribute(29, "should-fill", ShouldFill.ToString());
        builder.AddAttribute(30, "overflow", Overflow.ToString());
        AddPointerAttributes(builder);
        builder.AddContent(108, ChildContent);
        builder.CloseElement();
    }
}

public class Input : PointerComponentBase
{
    [Parameter] public string Value { get; set; } = "";
    [Parameter] public int Cursor { get; set; }
    [Parameter] public int? SelectionAnchor { get; set; }
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public string? Fg { get; set; }
    [Parameter] public string? Bg { get; set; }
    [Parameter] public string? PlaceholderFg { get; set; }
    [Parameter] public NativeRgba? CursorColor { get; set; }
    [Parameter] public NativeRgba? SelectionForeground { get; set; }
    [Parameter] public NativeRgba? SelectionBackground { get; set; }
    [Parameter] public int MaxHeight { get; set; } = 1;
    [Parameter] public string? FocusKey { get; set; }
    [Parameter] public EventCallback<TerminalKeyEventArgs> OnKeyDown { get; set; }
    [Parameter] public EventCallback<TerminalPasteEventArgs> OnPaste { get; set; }
    [Parameter] public EventCallback<TerminalTextInputEventArgs> OnTextInput { get; set; }
    [Parameter] public IReadOnlyList<TerminalTextMark> TextMarks { get; set; } = [];
    internal ImmutableArray<NativeTextRun> MarkRuns { get; private set; }
    private string? _paintText;
    private ImmutableArray<TerminalTextMark> _paintMarks = [];

    protected override void OnParametersSet()
    {
        if (_paintText == Value && _paintMarks.SequenceEqual(TextMarks)) return;
        _paintText = Value;
        _paintMarks = TextMarks.ToImmutableArray();
        MarkRuns = InputTextRuns.Create(Value, _paintMarks);
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "max-height", MaxHeight);
        builder.AddAttribute(2, "cursor", Cursor);
        builder.AddAttribute(3, "placeholder", Placeholder);
        builder.AddAttribute(4, "focus-key", FocusKey);
        builder.AddAttribute(5, "onkeydown", OnKeyDown);
        builder.AddAttribute(6, "onpaste", OnPaste);
        builder.AddAttribute(7, "selection-anchor", SelectionAnchor);
        builder.AddAttribute(8, "fg", Fg);
        builder.AddAttribute(9, "bg", Bg);
        builder.AddAttribute(10, "placeholder-fg", PlaceholderFg);
        builder.AddAttribute(11, "cursor-color", Color(CursorColor));
        builder.AddAttribute(12, "selection-fg", Color(SelectionForeground));
        builder.AddAttribute(13, "selection-bg", Color(SelectionBackground));
        builder.AddAttribute(14, "ontextinput", OnTextInput);
        AddPointerAttributes(builder);
        builder.AddContent(108, Value);
        builder.CloseElement();
    }

    private static string? Color(NativeRgba? value) => value is { } color ? $"#{(byte)color.R:X2}{(byte)color.G:X2}{(byte)color.B:X2}{(byte)color.A:X2}" : null;
}

public class TuiText : PointerComponentBase
{
    [Parameter] public string? Value { get; set; }
    /// <summary>Immutable styled UTF-8 runs. Replace the array to update content; do not mutate its backing memory.</summary>
    [Parameter] public ImmutableArray<NativeTextRun> Runs { get; set; }
    [Parameter] public string? Fg { get; set; }
    [Parameter] public string? Bg { get; set; }
    [Parameter] public bool Bold { get; set; }
    [Parameter] public bool Dim { get; set; }
    [Parameter] public int? Height { get; set; }
    /// <summary>Optional cap on the natural wrapped row count; explicit Height still controls fixed-row content.</summary>
    [Parameter] public int? MaxHeight { get; set; }
    [Parameter] public int? Width { get; set; }
    [Parameter] public int Grow { get; set; }
    [Parameter] public bool Tail { get; set; }
    [Parameter] public int Scroll { get; set; }
    [Parameter] public NativeTextWrapMode WrapMode { get; set; } = NativeTextWrapMode.Character;
    [Parameter] public bool Selectable { get; set; } = true;

    protected override void OnParametersSet()
    {
        if (!Runs.IsDefault && Value is not null)
#pragma warning disable MA0015 // This describes mutually exclusive component parameters, not a C# method argument.
            throw new ArgumentException("TuiText accepts either Value or Runs, not both.");
#pragma warning restore MA0015
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        builder.AddAttribute(1, "fg", Fg);
        builder.AddAttribute(2, "bg", Bg);
        builder.AddAttribute(3, "bold", Bold);
        builder.AddAttribute(4, "dim", Dim);
        builder.AddAttribute(5, "height", Height);
        builder.AddAttribute(6, "grow", Grow);
        builder.AddAttribute(7, "tail", Tail);
        builder.AddAttribute(8, "scroll", Scroll);
        builder.AddAttribute(9, "width", Width);
        builder.AddAttribute(11, "wrap-mode", WrapMode.ToString());
        builder.AddAttribute(12, "selectable", Selectable);
        builder.AddAttribute(13, "text-max-height", MaxHeight);
        // Runs remain typed component state. Element attributes would stringify them.
        AddPointerAttributes(builder);
        if (Runs.IsDefault) builder.AddContent(108, Value);
        builder.CloseElement();
    }
}
