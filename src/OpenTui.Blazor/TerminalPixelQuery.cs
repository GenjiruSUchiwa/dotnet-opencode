namespace OpenTui.Blazor;

using System.Globalization;

internal readonly record struct TerminalPixelSize(int Width, int Height);

/// <summary>Source renderer query state: one outstanding query, resize coalescing, no polling timer.</summary>
internal sealed class TerminalPixelQuery(Action send)
{
    public bool Waiting { get; private set; }
    private bool _again;
    private bool _stopped;

    public void Request()
    {
        if (_stopped) return;
        _again = true;
        if (Waiting) return;
        _again = false;
        Waiting = true;
        send();
    }

    public bool Consume(string response, out TerminalPixelSize? size)
    {
        size = null;
        if (_stopped || !Waiting || !response.StartsWith("\x1b[4;", StringComparison.Ordinal) || !response.EndsWith('t')) return false;
        var values = response.AsSpan(4, response.Length - 5);
        var separator = values.IndexOf(';');
        if (separator < 1 || separator == values.Length - 1 ||
            values[..separator].IndexOfAnyExceptInRange('0', '9') >= 0 ||
            values[(separator + 1)..].IndexOfAnyExceptInRange('0', '9') >= 0) return false;
        // A syntactically valid reply completes the query even if its numeric
        // dimensions cannot be represented, matching parsePixelResolution.
        Waiting = false;
        if (_again) { Request(); return true; }
        if (int.TryParse(values[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var height) &&
            int.TryParse(values[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var width))
            size = new(width, height);
        return true;
    }

    public void Stop() { _stopped = true; Waiting = false; _again = false; }
}
