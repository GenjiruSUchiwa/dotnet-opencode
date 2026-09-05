namespace OpenCode.Cli.Tui.Tabs;

using OpenTui.Native;

/// <summary>TabIndicator's latched hue, 180ms smoothstep dissolve, and first-fifth flash.</summary>
internal sealed class SessionTabIndicatorMotion(TimeProvider clock)
{
    private double _opacity;
    private double _from;
    private long? _started;
    private bool _unread;
    private NativeRgba _hue;
    public bool Fading => !_unread && _opacity > 0;

    public void Update(SessionTabActivity status, NativeRgba hue, bool enabled)
    {
        var pending = status.Busy == true || status.Attention is not null;
        var unread = status.Unread is not null && !pending;
        if (unread) _hue = hue;
        if (unread || pending || !enabled)
        {
            _opacity = unread ? 1 : 0;
            _started = null;
        }
        else if (_unread)
        {
            _from = _opacity;
            _started = clock.GetTimestampMilliseconds();
        }
        _unread = unread;
    }

    public void Advance(long now)
    {
        if (_started is not { } started) return;
        var progress = Math.Clamp((now - started) / 180d, 0, 1);
        _opacity = _from * (1 - progress * progress * (3 - 2 * progress));
        if (progress >= 1) _started = null;
    }

    public NativeRgba Color(NativeRgba normal, NativeRgba background, NativeRgba flash, bool numbers)
    {
        if (numbers || !Fading) return normal;
        var strength = Math.Max(0, 1 - Math.Abs(_opacity - .8) / .2);
        return SessionTabText.Blend(background, SessionTabText.Blend(_hue, flash, strength * .3), Math.Min(1, _opacity / .8));
    }
}
