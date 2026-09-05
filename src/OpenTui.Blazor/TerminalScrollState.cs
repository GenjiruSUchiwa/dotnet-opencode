namespace OpenTui.Blazor;

using OpenTui.Blazor.Nodes;

/// <summary>Dispatcher-owned scroll state with bottom stickiness and a retained-row anchor.</summary>
public sealed class TerminalScrollState
{
    internal readonly record struct Row(TuiNode Node, int Start, int Height);
    private IReadOnlyList<Row> _rows = [];
    private TuiNode? _anchor;
    private object? _anchorKey;
    private int _anchorOffset;
    private (object Key, bool Center)? _reveal;
    public int Offset { get; private set; }
    public int ViewportHeight { get; private set; }
    public int ContentHeight { get; private set; }
    public bool AtBottom { get; private set; } = true;
    public bool AutoFollow { get; set; } = true;
    public event Action? Changed;

    public void ScrollBy(int rows)
    {
        _reveal = null;
        Offset = (int)Math.Clamp((long)Offset + rows, 0, Math.Max(0, ContentHeight - ViewportHeight));
        AtBottom = Offset == Math.Max(0, ContentHeight - ViewportHeight);
        CaptureAnchor();
        Changed?.Invoke();
    }

    public void ScrollToEnd()
    {
        _reveal = null;
        AtBottom = true;
        Offset = Math.Max(0, ContentHeight - ViewportHeight);
        CaptureAnchor();
        Changed?.Invoke();
    }

    public void Reset()
    {
        _reveal = null;
        _rows = [];
        _anchor = null;
        _anchorKey = null;
        Offset = ContentHeight = ViewportHeight = _anchorOffset = 0;
        AtBottom = true;
        Changed?.Invoke();
    }

    internal void Update(IReadOnlyList<Row> rows, int contentHeight, int viewportHeight)
    {
        ContentHeight = contentHeight;
        ViewportHeight = viewportHeight;
        var maximum = Math.Max(0, contentHeight - viewportHeight);
        if (AtBottom && AutoFollow) Offset = maximum;
        else if ((_anchor is not null || _anchorKey is not null) && rows.FirstOrDefault(row => ReferenceEquals(row.Node, _anchor)
            || _anchorKey is not null && Equals(row.Node.AnchorKey, _anchorKey)) is { Node: not null } anchor)
            Offset = (int)Math.Clamp((long)anchor.Start + Math.Min(_anchorOffset, Math.Max(0, anchor.Height - 1)), 0, maximum);
        else Offset = Math.Min(Offset, maximum);
        _rows = rows;
        if (_reveal is { } reveal && rows.FirstOrDefault(row => Equals(row.Node.AnchorKey, reveal.Key)) is { Node: not null } target)
        {
            Offset = Math.Clamp(reveal.Center ? target.Start - viewportHeight / 2
                : target.Start < Offset ? target.Start
                : target.Start + target.Height > Offset + viewportHeight ? target.Start + target.Height - viewportHeight : Offset, 0, maximum);
            AtBottom = Offset == maximum;
            _reveal = null;
        }
        CaptureAnchor();
    }

    public void ScrollToStart()
    {
        Offset = 0;
        AtBottom = false;
        _anchor = null;
        _anchorKey = null;
        _reveal = null;
        Changed?.Invoke();
    }

    /// <summary>Reveal a keyed row after the next layout, when its wrapped height is known.</summary>
    public void Reveal(object key, bool center = false)
    {
        _reveal = (key, center);
        Changed?.Invoke();
    }

    private void CaptureAnchor()
    {
        var row = _rows.FirstOrDefault(row => (long)row.Start + row.Height > Offset);
        _anchor = row.Node;
        _anchorKey = row.Node?.AnchorKey;
        _anchorOffset = Math.Max(0, Offset - row.Start);
    }

    /// <summary>Release a detached render tree while retaining its stable row key and offset.</summary>
    public void Detach()
    {
        if (_rows.Count > 0) CaptureAnchor();
        _rows = [];
        _anchor = null;
    }
}
