namespace OpenCode.Cli.Tui.Tabs;

/// <summary>session-tabs membership seeding and ui/animation's critically damped spring.</summary>
internal sealed class SessionTabMotion(TimeProvider clock)
{
    private Guid[] _ids = [];
    private double[][] _values = [[], [], []];
    private double[][] _targets = [[], [], []];
    private double[][] _velocity = [[], [], []];
    private int _total;
    private long _previous;
    public bool Live { get; private set; }

    public void Update(AdaptiveSessionTabLayout layout, Guid selected, Func<SessionTab, bool> complete,
        bool enabled, bool held, bool releasing)
    {
        var ids = layout.Tabs.Select(tab => tab.Key).ToArray();
        double[][] next = [layout.Widths.Select(width => (double)width).ToArray(),
            layout.Tabs.Select(tab => tab.Key == selected ? 1d : 0).ToArray(),
            layout.Tabs.Select(tab => complete(tab) ? 1d : 0).ToArray()];
        var changed = _ids.Length > 0 && !_ids.SequenceEqual(ids);
        var resized = _total > 0 && _total != layout.Total;
        if (enabled && !releasing && _ids.SequenceEqual(ids) && _total == layout.Total &&
            _targets.SelectMany(values => values).SequenceEqual(next.SelectMany(values => values))) return;
        var positions = ids.Select(id => Array.IndexOf(_ids, id)).ToArray();
        if (!enabled || _ids.Length == 0 || changed && positions.All(index => index < 0) || !changed && resized && !held && !releasing)
            Jump(next);
        else if (changed || held)
        {
            var seed = changed ? next.Select((values, channel) => positions.Select((position, index) =>
                position < 0 ? channel == 0 ? 0 : values[index] : _values[channel][position]).ToArray()).ToArray()
                : _values.Select(values => values.ToArray()).ToArray();
            if (held) seed[0] = next[0].ToArray();
            Jump(seed);
        }
        _ids = ids;
        _total = layout.Total;
        if (_targets.SelectMany(values => values).SequenceEqual(next.SelectMany(values => values))) return;
        _targets = next;
        _previous = clock.GetTimestampMilliseconds();
        Live = true;
    }

    private void Jump(double[][] values)
    {
        _values = values.Select(value => value.ToArray()).ToArray();
        _targets = values.Select(value => value.ToArray()).ToArray();
        _velocity = values.Select(value => new double[value.Length]).ToArray();
        _previous = clock.GetTimestampMilliseconds();
        Live = false;
    }

    public void Advance(long now)
    {
        if (!Live) return;
        var delta = Math.Min(.05, Math.Max(0, now - _previous) / 1000d);
        _previous = now;
        var frequency = 2 * Math.PI / (.1 * 1.2);
        var decay = Math.Exp(-frequency * delta);
        Live = false;
        for (var channel = 0; channel < _values.Length; channel++)
            for (var index = 0; index < _values[channel].Length; index++)
            {
                var offset = _values[channel][index] - _targets[channel][index];
                var velocity = _velocity[channel][index];
                var next = velocity + frequency * offset;
                _values[channel][index] = _targets[channel][index] + (offset + next * delta) * decay;
                _velocity[channel][index] = (velocity - frequency * next * delta) * decay;
                Live |= Math.Abs(_values[channel][index] - _targets[channel][index]) > .002 || Math.Abs(_velocity[channel][index]) > .002;
            }
        if (!Live) Jump(_targets);
    }

    public int[] Widths(Guid selected)
    {
        var widths = _values[0].Select(value => Math.Max(1, (int)Math.Floor(value + .5))).ToArray();
        var active = Array.IndexOf(_ids, selected);
        var remainder = _total - widths.Sum();
        // Only rounding slack is absorbed. New members leave a real gap while growing.
        if (active >= 0 && Math.Abs(remainder) <= widths.Length) widths[active] = Math.Max(1, widths[active] + remainder);
        return widths;
    }

    public double Selection(Guid id) => Array.IndexOf(_ids, id) is var index && index >= 0 ? _values[1][index] : 0;
    public double Activity(Guid id) => Array.IndexOf(_ids, id) is var index && index >= 0 ? _values[2][index] : 0;
}
