namespace OpenCode.Cli.Tui.Tabs;

using System.Collections.Immutable;
using System.Text;
using OpenTui.Native;

/// <summary>title-shimmer.tsx state and cell masks; snapshots are native-measured, styled whole glyphs.</summary>
internal sealed class SessionTabTitleMotion(TimeProvider clock)
{
    private string? _title;
    private string? _pendingTitle;
    private bool _pending;
    private bool _enabled;
    private double _elapsed;
    private double _blend;
    private double? _arrival;
    private long? _previousTime;
    private Glyph[] _previous = [];
    private int _previousWidth;
    private sealed record Glyph(NativeTextRun Run, int Column, int Width);
    private bool Shimmering => _pending && _title == _pendingTitle;
    public bool Live => _enabled && (Shimmering || _arrival is not null || _blend > 0);

    public void Update(string title, bool pending, bool enabled)
    {
        var live = Live;
        if (pending && !_pending)
        {
            if (_pendingTitle != title) _blend = 0;
            _pendingTitle = title;
            if (_blend == 0) _elapsed = 0;
            _arrival = null;
            _previous = []; _previousWidth = 0;
        }
        if (title != _title)
        {
            _arrival = _pending && enabled && _previousWidth > 0 ? 0 : null;
            if (_arrival is null) _blend = 0;
        }
        _title = title; _pending = pending; _enabled = enabled;
        if (!enabled) { _arrival = null; _blend = 0; }
        if (!live || !Live) _previousTime = null;
        if (!Live) { _previous = []; _previousWidth = 0; }
    }

    public void Advance(long now)
    {
        if (!Live) return;
        var delta = _previousTime is { } previous ? Math.Max(0, now - previous) : 0;
        _previousTime = now;
        _elapsed = (_elapsed + delta) % 1200;
        if (_arrival is { } arrival)
        {
            _arrival = arrival + delta;
            if (_arrival >= 450) { _arrival = null; _blend = 0; }
        }
        _blend = Math.Clamp(_blend + (Shimmering || _arrival is not null ? delta : -delta) / 240d, 0, 1);
        if (!Live) { _previous = []; _previousWidth = 0; _previousTime = null; }
    }

    public ImmutableArray<NativeTextRun> Paint(ImmutableArray<NativeTextRun> runs, int width,
        SessionTabText text, NativeRgba background, Func<int, NativeRgba> cellBackground)
    {
        if (!Live || width <= 0) return runs;
        _previousTime ??= clock.GetTimestampMilliseconds();
        var column = 0;
        var glyphs = runs.Select(run =>
        {
            var length = text.Width(Encoding.UTF8.GetString(run.Text.Span));
            var glyph = new Glyph(run with { Background = null }, column, length);
            column += length;
            return glyph;
        }).ToArray();
        var end = glyphs.LastOrDefault(glyph => Encoding.UTF8.GetString(glyph.Run.Text.Span) is not (" " or "")) is { } last
            ? last.Column + last.Width : 0;
        var wipe = _arrival is { } arrival && _previousWidth > 0
            ? -4 + SessionTabPulse.Coast(arrival / 450) * (Math.Max(end, _previousWidth) + 8) : (double?)null;
        var cut = Math.Clamp((int)Math.Floor((wipe ?? 0) + .5), 0, width);
        if (wipe is null)
        {
            _previousWidth = Math.Max(1, end);
            _previous = glyphs.Where(glyph => glyph.Column + glyph.Width <= _previousWidth).ToArray();
        }
        var front = -4 + SessionTabPulse.Coast(_elapsed / 1200) * (_previousWidth + 22);
        var level = SessionTabPulse.Smooth(_blend);
        var visible = wipe is null ? glyphs : glyphs.Where(glyph => glyph.Column + glyph.Width <= cut)
            .Concat(_previous.Where(glyph => glyph.Column >= cut && glyph.Column + glyph.Width <= width)).ToArray();
        var output = ImmutableArray.CreateBuilder<NativeTextRun>();
        column = 0;
        foreach (var glyph in visible)
        {
            if (glyph.Column < column || glyph.Column + glyph.Width > width) continue;
            // A wipe/scissor may cross a wide glyph: leave the whole glyph blank, never a continuation half.
            while (column < glyph.Column) output.Add(new NativeTextRun(" ", Background: cellBackground(column++)));
            var old = wipe is null || glyph.Column >= cut;
            var visibility = old ? 1 - .6 * level * (1 - SessionTabPulse.Intensity(glyph.Column, front)) : 1;
            if (wipe is { } edge)
            {
                var distance = old ? glyph.Column - edge : edge - (glyph.Column + glyph.Width);
                visibility *= SessionTabPulse.Smooth(Math.Clamp(distance / 4, 0, 1));
            }
            output.Add(glyph.Run with
            {
                Foreground = glyph.Run.Foreground is { } color ? SessionTabText.Blend(color, background, 1 - visibility) : null,
                Background = cellBackground(glyph.Column)
            });
            column += glyph.Width;
        }
        while (column < width) output.Add(new NativeTextRun(" ", Background: cellBackground(column++)));
        return output.ToImmutable();
    }
}
