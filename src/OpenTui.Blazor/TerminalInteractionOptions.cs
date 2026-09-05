namespace OpenTui.Blazor;

/// <summary>Live, dispatcher-owned terminal interaction preferences. No application config or persistence is owned here.</summary>
public sealed class TerminalInteractionOptions
{
    public bool MouseEnabled { get; set; } = true;
    private double _scrollSpeed = 3;
    public double ScrollSpeed
    {
        get => _scrollSpeed;
        set
        {
            if (!double.IsFinite(value) || value < .001) throw new ArgumentOutOfRangeException(nameof(value));
            _scrollSpeed = value;
        }
    }
}
