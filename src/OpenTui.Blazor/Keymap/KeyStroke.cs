namespace OpenTui.Blazor.Keymap;

/// <summary>OpenTUI key identity. Meta is Alt/Option, not Super. Shift is never inferred from casing.</summary>
public sealed record KeyStroke
{
    public string Name { get; }
    public bool Ctrl { get; }
    public bool Shift { get; }
    public bool Meta { get; }
    public bool Super { get; }
    public bool Hyper { get; }

    public KeyStroke(string name, bool ctrl = false, bool shift = false, bool meta = false, bool super = false, bool hyper = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim().ToLowerInvariant();
        Ctrl = ctrl;
        Shift = shift;
        Meta = meta;
        Super = super;
        Hyper = hyper;
    }

    public override string ToString() => string.Concat(Ctrl ? "ctrl+" : "", Shift ? "shift+" : "",
        Meta ? "meta+" : "", Super ? "super+" : "", Hyper ? "hyper+" : "", Name == "return" ? "enter" : Name);
}

public enum KeyEventType { Press, Release }

/// <summary>The host supplies its normalized native event, without reducing it to ConsoleKeyInfo.</summary>
public sealed record KeymapEvent(KeyStroke Stroke, KeyEventType Type = KeyEventType.Press,
    int? BaseCode = null, bool Repeat = false, bool PropagationStopped = false);

public sealed record KeySequencePart(KeyStroke Stroke, string? TokenName = null)
{
    public string Display => TokenName is null ? Stroke.ToString() : $"<{TokenName}>";
}

public abstract record BindingKey
{
    private BindingKey() { }
    public sealed record Text(string Value) : BindingKey;
    public sealed record Stroke(KeyStroke Value) : BindingKey;
}
