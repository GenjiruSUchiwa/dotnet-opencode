namespace OpenCode.Cli.Tui.Tabs;

using System.Collections.Immutable;
using OpenTui.Native;

/// <summary>Source tab-pulse.tsx envelopes and sweep math, rendered as native styled cells.</summary>
internal sealed class SessionTabPulse
{
    private bool _enabled;
    private bool _active;
    private bool _glow;
    private bool _complete;
    private long _prompt;
    private bool _completionPending;
    private double _sweepClock;
    private readonly Gate _run = new(450, Smooth, 500, value => 1 - Smooth(value));
    private readonly Gate _light = new(600, value => Attack(value, .3, 1.5, 1), 900, value => 1 - Smooth(Math.Clamp(value / (200d / 900), 0, 1)));
    private readonly Envelope _completion = new(1200, value => Attack(value, .12, 1, 0));
    private readonly Envelope _flash = new(800, value => Attack(value, .1, 1, 0));

    public SessionTabPulse(SessionTabActivity status, bool selected, bool enabled)
    {
        _enabled = enabled; _active = status.Running; _prompt = status.PromptPulse;
        _complete = status.Complete && status.Attention is null;
        _glow = status.Attention is not null || !selected && status.Busy != true && status.Unread is not null;
        _light.Settle(_glow);
        if (enabled && _active) _run.Trigger();
    }

    public void Update(SessionTabActivity status, bool selected, bool enabled)
    {
        if (_enabled != enabled)
        {
            _enabled = enabled;
            if (!enabled) { _run.Settle(false); _light.Settle(_glow); _completion.Stop(); _flash.Stop(); _completionPending = false; }
            else if (_active) _run.Trigger();
        }
        if (_active != status.Running)
        {
            _active = status.Running;
            if (enabled)
            {
                if (_active) { _sweepClock = 0; _run.Trigger(); _completion.Stop(); _completionPending = false; }
                else { _run.Release(); _completionPending = true; }
                _flash.Start();
            }
        }
        if (_prompt != status.PromptPulse) { _prompt = status.PromptPulse; if (enabled) _flash.Restart(2); }
        var complete = status.Complete && status.Attention is null;
        if (_complete != complete)
        {
            _complete = complete;
            if (!complete) { _completion.Stop(); _completionPending = false; }
            if (complete && _completionPending) { _completionPending = false; if (enabled) _completion.Start(); }
        }
        var glow = status.Attention is not null || !selected && status.Busy != true && status.Unread is not null;
        if (_glow == glow) return;
        _glow = glow;
        if (!enabled) _light.Settle(glow);
        else if (glow) _light.Trigger();
        else _light.Release();
    }

    public bool Live => _enabled && (_active || _run.Animating || _light.Animating || _completion.Active || _flash.Active);
    public void Advance(double delta)
    {
        if (!Live) return;
        if (!_run.Idle) _sweepClock += delta;
        _run.Advance(delta); _light.Advance(delta); _completion.Advance(delta); _flash.Advance(delta);
        if (!_completionPending) return;
        if (_complete) { _completionPending = false; _completion.Start(); }
        else if (_run.ReleaseProgress is null) _completionPending = false;
    }

    public NativeRgba Background(int index, int width, NativeRgba background, NativeRgba foreground, NativeRgba glowColor,
        bool vertical, bool detail = false) => Paint(index, width, background, glowColor,
            SessionTabText.Blend(background, foreground, detail ? .13 : vertical ? .25 : .45),
            SessionTabText.Blend(background, foreground, detail ? .42 : vertical ? .7 : .65),
            glowColor, detail ? 10 : 12, vertical ? 8 : null);

    public NativeRgba Edge(int index, int width, NativeRgba background, NativeRgba foreground, NativeRgba hue,
        double dim, bool upper, int tail) => Paint(index, width, background,
            SessionTabText.Blend(background, hue, (upper ? .1 : .12) * dim),
            SessionTabText.Blend(background, foreground, upper ? .04 : .05),
            SessionTabText.Blend(background, foreground, upper ? .18 : .22),
            SessionTabText.Blend(background, hue, (upper ? .1 : .12) * dim), tail, 8);

    private NativeRgba Paint(int index, int width, NativeRgba background, NativeRgba glowColor,
        NativeRgba runningColor, NativeRgba flashColor, NativeRgba completionColor, int maximumTail, int? flashTail)
    {
        var tail = Math.Min(maximumTail, Math.Max(1, width - 2));
        var glow = Glow(index, tail) * .16 * _light.Level;
        if (_light.ReleaseProgress is { } progress)
            glow = Math.Max(glow, Glow(index, tail + Smooth(progress) * width) * .16 * Attack(progress, .12, 1.25, 0) * Math.Max(1, _light.NoteOffLevel));
        var cycles = _sweepClock / 2800;
        var front = -4 + Coast(cycles % 1) * (width - 1 + 18 + 4);
        var second = -4 + Coast(cycles < .5 ? 0 : (cycles + .5) % 1) * (width - 1 + 18 + 4);
        var sweep = Math.Max(Intensity(index, front), Intensity(index, second)) * .14 * _run.Level;
        var output = SessionTabText.Blend(background, glowColor, glow);
        output = SessionTabText.Blend(output, runningColor, sweep);
        output = SessionTabText.Blend(output, flashColor,
            _flash.Level * .1 * (flashTail is { } length ? Glow(index, Math.Min(length, Math.Max(1, width - 2))) : 1));
        return SessionTabText.Blend(output, completionColor, _completion.Level * .18);
    }

    public static double Glow(int index, double tail) => Smooth(Math.Clamp(1 - Math.Max(0, index - 1) / tail, 0, 1));
    public static double Shimmer(long elapsed, int index, int width)
    {
        var front = -4 + Coast((elapsed % 1200) / 1200d) * (width + 4 + 18);
        return .6 * Smooth(Math.Clamp(elapsed / 240d, 0, 1)) * (1 - Intensity(index, front));
    }
    internal static double Smooth(double value) => value * value * value * (value * (value * 6 - 15) + 10);
    private static double Attack(double value, double attack, double peak, double rest) => value < attack
        ? peak * Smooth(Math.Clamp(value / attack, 0, 1)) : peak - (peak - rest) * Smooth(Math.Clamp((value - attack) / (1 - attack), 0, 1));
    internal static double Intensity(double index, double front) => front - index < 0
        ? Smooth(Math.Clamp(1 + (front - index) / 4, 0, 1)) : Smooth(Math.Clamp(1 - (front - index) / 18, 0, 1));
    internal static double Coast(double value) => value < .2 ? value * value / (.4 * .8)
        : value > .8 ? 1 - (1 - value) * (1 - value) / (.4 * .8) : (value - .1) / .8;

    private sealed class Envelope(double duration, Func<double, double> shape)
    {
        private double? _clock;
        private double _scale = 1;
        public bool Active => _clock is not null;
        public double Level => _clock is { } clock ? _scale * shape(clock / duration) : 0;
        public void Start() { if (_clock is null) Restart(1); }
        public void Restart(double scale) { _clock = 0; _scale = scale; }
        public void Stop() => _clock = null;
        public void Advance(double delta) { if (_clock is not { } clock) return; _clock = clock + delta >= duration ? null : clock + delta; }
    }

    private sealed class Gate(double attackDuration, Func<double, double> attack, double releaseDuration, Func<double, double> release)
    {
        private int _phase;
        private double _clock;
        public double NoteOffLevel { get; private set; } = 1;
        public bool Idle => _phase == 0;
        public bool Animating => _phase is 1 or 3;
        public double? ReleaseProgress => _phase == 3 ? _clock / releaseDuration : null;
        public double Level => _phase switch { 0 => 0, 1 => attack(_clock / attackDuration), 2 => 1, _ => NoteOffLevel * release(_clock / releaseDuration) };
        public void Trigger() { _phase = 1; _clock = 0; }
        public void Release() { if (Idle) return; NoteOffLevel = Level; _phase = 3; _clock = 0; }
        public void Settle(bool on) { _phase = on ? 2 : 0; _clock = 0; }
        public void Advance(double delta)
        {
            if (!Animating) return;
            _clock += delta;
            if (_clock < (_phase == 1 ? attackDuration : releaseDuration)) return;
            _phase = _phase == 1 ? 2 : 0; _clock = 0;
        }
    }
}

/// <summary>Each separator owns both pulse voices, so a reorder retargets rather than borrowing a neighbor's clock.</summary>
internal sealed class SessionTabSeparator(SessionTabActivity current, bool selected, SessionTabActivity previous, bool previousSelected,
    SessionTabsTheme theme, bool enabled)
{
    private readonly Side _inner = new(current, selected, theme, enabled);
    private readonly Side _outer = new(previous, previousSelected, theme, enabled);

    public void Update(SessionTabActivity current, bool selected, SessionTabActivity previous, bool previousSelected,
        SessionTabsTheme theme, bool enabled)
    {
        _inner.Update(current, selected, theme, enabled);
        _outer.Update(previous, previousSelected, theme, enabled);
    }

    public void Advance(double delta) { _inner.Advance(delta); _outer.Advance(delta); }

    public ImmutableArray<NativeTextRun> Runs(int width, SessionTabsTheme theme, bool below = false)
    {
        var background = SessionTabText.Parse(theme.Background);
        var foreground = SessionTabText.Parse(theme.Text);
        var count = Math.Min(10, Math.Max(0, width));
        return Enumerable.Range(0, count).Select(column => new NativeTextRun(below ? "▀" : "▄",
            _inner.Pulse.Edge(column, count, background, foreground, _inner.Hue, _inner.Dim, below, 8),
            below ? background : _outer.Pulse.Edge(column, count, background, foreground, _outer.Hue, _outer.Dim, true, 5))).ToImmutableArray();
    }

    private sealed class Side
    {
        public SessionTabPulse Pulse { get; }
        public NativeRgba Hue { get; private set; }
        public double Dim { get; private set; }
        private double _from;
        private double _target;
        private double _clock;

        public Side(SessionTabActivity status, bool selected, SessionTabsTheme theme, bool enabled)
        {
            Pulse = new(status, selected, enabled);
            Hue = SessionTabText.Parse(theme.Unread);
            Dim = _target = selected && status.Attention is not null ? .7 : 1;
            Update(status, selected, theme, enabled);
        }

        public void Update(SessionTabActivity status, bool selected, SessionTabsTheme theme, bool enabled)
        {
            Pulse.Update(status, selected, enabled);
            var hue = status.Attention switch
            {
                SessionTabAttention.Permission => theme.Permission,
                SessionTabAttention.Question => theme.Question,
                _ => status.Unread is SessionTabUnread.Error ? theme.Error : status.Unread is not null ? theme.Unread : null
            };
            if (hue is not null) Hue = SessionTabText.Parse(hue);
            var target = selected && status.Attention is not null ? .7 : 1;
            if (target != _target) { _from = Dim; _target = target; _clock = 0; }
            if (!enabled) Dim = _target;
        }

        public void Advance(double delta)
        {
            Pulse.Advance(delta);
            if (Dim == _target) return;
            _clock += delta;
            var progress = Math.Clamp(_clock / 200, 0, 1);
            Dim = _from + (_target - _from) * progress * progress * (3 - 2 * progress);
        }
    }
}
