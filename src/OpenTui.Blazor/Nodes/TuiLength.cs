namespace OpenTui.Blazor.Nodes;

using System.Globalization;
using OpenTui.Native;

/// <summary>A Yoga dimension. Existing integer Width/Height parameters remain available.</summary>
public readonly record struct TuiLength
{
    public NativeYogaUnit Unit { get; }
    public float Value { get; }
    private TuiLength(NativeYogaUnit unit, float value)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Unit = unit; Value = value;
    }
    public static TuiLength Auto => new(NativeYogaUnit.Auto, 0);
    public static TuiLength Cells(float value) => new(NativeYogaUnit.Point, value);
    public static TuiLength Percent(float value) => new(NativeYogaUnit.Percent, value);
    public static implicit operator TuiLength(int value) => Cells(value);
    public override string ToString() => Unit == NativeYogaUnit.Undefined ? "undefined" : Unit == NativeYogaUnit.Auto ? "auto" :
        Value.ToString(CultureInfo.InvariantCulture) + (Unit == NativeYogaUnit.Percent ? "%" : "");
    internal static TuiLength Parse(string text)
    {
        if (text == "auto") return Auto;
        if (text == "undefined") return default;
        var percent = text.EndsWith('%');
        var value = float.Parse(percent ? text[..^1] : text, CultureInfo.InvariantCulture);
        return percent ? Percent(value) : Cells(value);
    }
}

public enum TuiJustify { Start, Center, End, SpaceBetween, SpaceAround, SpaceEvenly }
public enum TuiAlign { Auto, Start, Center, End, Stretch, Baseline, SpaceBetween, SpaceAround, SpaceEvenly }
public enum TuiFlexWrap { None, Wrap, Reverse }
