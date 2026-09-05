namespace OpenTui.Blazor.Nodes;

using OpenTui.Native;
using System.Collections.Immutable;
using System.Text;
using OpenTui.Blazor.TextMarks;
using OpenTui.Blazor.Code;
using Microsoft.AspNetCore.Components;

public enum TuiFlexDirection
{
    Row,
    Column
}

public enum TuiCrossAlignment { Stretch, Start, Center, End }
public enum TuiPosition { Flow, Absolute }

public sealed class TuiNode
{
    public string TagName { get; internal init; } = "box";
    internal bool IsMarkup { get; init; }
    private bool IsFormattingWhitespace => TagName == "#text" && IsMarkup && string.IsNullOrWhiteSpace(TextContent);
    public TuiNode? Parent { get; private set; }
    internal object? Key { get; init; }
    internal object? AnchorKey => Key ?? Parent?.AnchorKey;
    private readonly List<TuiNode> _children = [];
    internal IComponent? Component;
    internal TerminalScrollState? ScrollState;
    internal EmbeddedTerminalState? EmbeddedTerminal;
    internal ImageState? Image;
    internal ImageFit ImageFit;
    internal NativeImageProtocol ImageProtocol;
    internal ulong SizeHandlerId;
    internal int ReportedWidth = -1;
    internal int ReportedHeight = -1;
    internal ImmutableArray<NativeTextRun> TextRuns;
    internal ImmutableArray<NativeTextRun> NativeRuns;
    public IReadOnlyList<TuiNode> Children => _children;
    private List<TuiNode>? _layoutChildren;
    private List<TuiNode>? _flowChildren;
    public IReadOnlyList<TuiNode> FlowChildren => _flowChildren ??= LayoutChildren.Where(child => child.TagName != "modal" && child.Position != TuiPosition.Absolute).ToList();
    internal IEnumerable<TuiNode> PaintChildren => LayoutChildren.Where(child => child.TagName != "modal").OrderBy(child => child.ZIndex);

    public IReadOnlyList<TuiNode> LayoutChildren
    {
        get
        {
            if (_layoutChildren is not null) return _layoutChildren;
            _layoutChildren = [];
            if (TagName is "text" or "input") return _layoutChildren;
            foreach (var child in Children)
            {
                // Razor emits newline/indent markup around components and
                // fragments. Keep its frame identity for render diffs, but do
                // not turn formatting into terminal rows/columns or painted cells.
                // Text/input Content still reads the original children verbatim.
                if (child.IsFormattingWhitespace) continue;
                if (child.TagName == "#component") _layoutChildren.AddRange(child.LayoutChildren);
                else _layoutChildren.Add(child);
            }
            return _layoutChildren;
        }
    }

    private string? _content;
    public string Content => _content ??= Children.Count > 0 && (TagName is "text" or "input" or "#component")
        ? Children.Count == 1 ? Children[0].Content : string.Concat(Children.Select(child => child.Content))
        : TextContent;

    internal void InvalidateLayoutChildren()
    {
        _layoutChildren = null;
        _flowChildren = null;
        _content = null;
        Parent?.InvalidateLayoutChildren();
    }

    public TuiFlexDirection Direction { get; internal set; } = TuiFlexDirection.Column;
    public int? Width { get; internal set; }
    public int? Height { get; internal set; }
    public int PaddingX { get; internal set; }
    public int? PaddingLeftOverride { get; internal set; }
    public int? PaddingRightOverride { get; internal set; }
    public int PaddingLeft => PaddingLeftOverride ?? PaddingX;
    public int PaddingRight => PaddingRightOverride ?? PaddingX;
    public int PaddingTop { get; internal set; }
    public int PaddingBottom { get; internal set; }
    public int Gap { get; internal set; }
    public int Grow { get; internal set; }
    public int Shrink { get; internal set; }
    public bool Center { get; internal set; }
    public TuiPosition Position { get; internal set; }
    public int? Left { get; internal set; }
    public int? Right { get; internal set; }
    public int? Top { get; internal set; }
    public int? Bottom { get; internal set; }
    public int ZIndex { get; internal set; }
    public bool PointerEvents { get; internal set; } = true;
    internal ulong PointerDownHandlerId;
    internal ulong PointerUpHandlerId;
    internal ulong PointerMoveHandlerId;
    internal ulong PointerEnterHandlerId;
    internal ulong PointerLeaveHandlerId;
    internal ulong ClickHandlerId;
    internal ulong WheelHandlerId;
    public bool SharedColumns { get; internal set; }
    public TuiCrossAlignment CrossAlignment { get; internal set; }
    public NativeTextWrapMode WrapMode { get; internal set; } = NativeTextWrapMode.Character;
    public bool Selectable { get; internal set; }
    internal NativeTextSelectionRange? SelectionRange;
    internal NativeRgba? NativeSelectionForeground;
    internal NativeRgba? NativeSelectionBackground;
    internal bool NativeSelectionColorsSet;
    private ImmutableArray<NativeTextRun> _plainRuns;
    private string _plainRunsText = "";
    internal string PlainText
    {
        get
        {
            if (TextRuns.IsDefault) return Content;
            if (_plainRuns != TextRuns)
            {
                _plainRuns = TextRuns;
                _plainRunsText = string.Concat(TextRuns.Select(run => Encoding.UTF8.GetString(run.Text.Span)));
            }
            return _plainRunsText;
        }
    }
    internal NativeTextWrapMode? NativeWrapMode;
    public bool Tail { get; internal set; }
    public int Scroll { get; internal set; }
    public int? Cursor { get; internal set; }
    public int? SelectionAnchor { get; internal set; }
    internal string? IntrinsicText;
    internal ImmutableArray<NativeTextRun> IntrinsicRuns;
    internal byte IntrinsicWidthMethod;
    internal int? IntrinsicWidth;
    public int MaxHeight { get; internal set; } = 1;
    internal int? TextMaxHeight;
    public string? FocusKey { get; internal set; }
    public bool Focused { get; internal set; }
    public ulong KeyHandlerId { get; internal set; }
    public ulong PasteHandlerId { get; internal set; }
    public ulong TextInputHandlerId { get; internal set; }
    public ulong CloseHandlerId { get; internal set; }
    internal TerminalTextLayout? InputLayout;
    internal TerminalTextMap? InputTextMap;
    internal string? InputMapText;
    internal byte InputMapWidthMethod;
    internal int InputLayoutWidth;
    internal byte InputWidthMethod;
    internal int InputTop;
    internal NativeTextView? TextView;
    internal CodeDocument? CodeDocument;
    internal CodeDocument? NativeCodeDocument;
    internal NativeSyntaxStyle? CodeSyntaxStyle;
    internal string? NativeText;
    internal NativeRgba? NativeForeground;
    internal NativeRgba? NativeBackground;
    internal uint? NativeAttributes;
    internal int NativeViewportWidth;
    internal int NativeViewportHeight;
    internal int NativeViewportTop;

    internal void ReleaseNativeText()
    {
        foreach (var child in Children) child.ReleaseNativeText();
        TextView?.Dispose();
        TextView = null;
        CodeSyntaxStyle?.Dispose();
        CodeSyntaxStyle = null;
        NativeCodeDocument = null;
        NativeText = null;
        NativeWrapMode = null;
        NativeSelectionColorsSet = false;
        NativeRuns = default;
        NativeForeground = NativeBackground = null;
        NativeAttributes = null;
        NativeViewportWidth = NativeViewportHeight = NativeViewportTop = 0;
    }
    public string? Placeholder { get; internal set; }
    public NativeRgba? PlaceholderFg { get; internal set; }
    public NativeRgba? CursorColor { get; internal set; }
    public NativeRgba? SelectionForeground { get; internal set; }
    public NativeRgba? SelectionBackground { get; internal set; }
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int LayoutWidth { get; internal set; }
    public int LayoutHeight { get; internal set; }

    public NativeRgba? Fg { get; internal set; }
    public NativeRgba? Bg { get; internal set; }
    public bool Bold { get; internal set; }
    public bool Dim { get; internal set; }
    public string? BorderStyle { get; internal set; }
    public NativeRgba? BorderFg { get; internal set; }

    private string _textContent = "";
    public string TextContent
    {
        get => _textContent;
        internal set
        {
            if (_textContent == value) return;
            _textContent = value;
            // A markup update can switch between formatting and visible text.
            InvalidateLayoutChildren();
        }
    }

    internal void AddChild(TuiNode child)
    {
        InsertChild(Children.Count, child);
    }

    internal void InsertChild(int index, TuiNode child)
    {
        child.Parent?.RemoveChild(child);
        child.Parent = this;
        _children.Insert(index, child);
        InvalidateLayoutChildren();
    }

    internal void RemoveChild(TuiNode child)
    {
        _children.Remove(child);
        if (child.Parent == this) child.Parent = null;
        InvalidateLayoutChildren();
    }

    internal void ApplyPermutation(IReadOnlyList<(int From, int To)> permutation)
    {
        var previous = _children.ToArray();
        foreach (var move in permutation) _children[move.To] = previous[move.From];
        InvalidateLayoutChildren();
    }
}
