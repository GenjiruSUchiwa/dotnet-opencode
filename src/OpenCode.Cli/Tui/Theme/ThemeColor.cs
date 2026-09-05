namespace OpenCode.Cli.Tui.Theme;

using OpenTui.Native;

/// <summary>Immutable RGB-intent color. Reference identity deliberately matches OpenTUI RGBA:
/// an equal-valued literal is not a hue token; hue aliases own cloned colors.</summary>
public sealed class ThemeColor(byte red, byte green, byte blue, byte alpha = 255)
{
    public byte Red { get; } = red;
    public byte Green { get; } = green;
    public byte Blue { get; } = blue;
    public byte Alpha { get; } = alpha;
    public double R => Red / 255.0;
    public double G => Green / 255.0;
    public double B => Blue / 255.0;
    public double A => Alpha / 255.0;
    public double Luminance => .299 * R + .587 * G + .114 * B;
    // Four uint16 lanes, RGBA8 in their LOW bytes. RGB intent leaves all metadata bits zero.
    public NativeRgba Native => new(Red, Green, Blue, Alpha);
    public string Hex => $"#{Red:x2}{Green:x2}{Blue:x2}" + (Alpha == 255 ? "" : $"{Alpha:x2}");
    public ThemeColor Clone() => new(Red, Green, Blue, Alpha);
    public bool SameValue(ThemeColor other) => Red == other.Red && Green == other.Green && Blue == other.Blue && Alpha == other.Alpha;

    public static ThemeColor Parse(string value)
    {
        if (value is "transparent" or "none") return new(0, 0, 0, 0);
        if (!IsHex(value)) throw new FormatException($"Invalid theme color: {value}");
        var text = value[1..];
        if (text.Length is 3 or 4) text = string.Concat(text.Select(character => new string(character, 2)));
        return new(Convert.ToByte(text[..2], 16), Convert.ToByte(text[2..4], 16), Convert.ToByte(text[4..6], 16),
            text.Length == 8 ? Convert.ToByte(text[6..8], 16) : (byte)255);
    }

    internal static bool IsHex(string value) => value.Length is 4 or 5 or 7 or 9 && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    public static ThemeColor FromValues(double r, double g, double b, double a = 1) => FromInts(r * 255, g * 255, b * 255, a * 255);
    public static ThemeColor FromInts(double r, double g, double b, double a = 255) => new(Byte(r), Byte(g), Byte(b), Byte(a));
    internal static byte Byte(double value) => (byte)Math.Floor(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 255) + .5);
    public static ThemeColor Tint(ThemeColor basis, ThemeColor overlay, double alpha) => FromInts(
        basis.Red + (overlay.Red - basis.Red) * alpha, basis.Green + (overlay.Green - basis.Green) * alpha, basis.Blue + (overlay.Blue - basis.Blue) * alpha);

    public static ThemeColor Ansi(int code)
    {
        string[] colors = ["#000000", "#800000", "#008000", "#808000", "#000080", "#800080", "#008080", "#c0c0c0",
            "#808080", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff"];
        if (code < 16) return Parse(code >= 0 ? colors[code] : colors[0]);
        if (code < 232)
        {
            var index = code - 16;
            return FromInts(Level(index / 36), Level(index / 6 % 6), Level(index % 6));
        }
        if (code < 256) return FromInts((code - 232) * 10 + 8, (code - 232) * 10 + 8, (code - 232) * 10 + 8);
        return Parse(colors[0]);
        static int Level(int part) => part == 0 ? 0 : part * 40 + 55;
    }
}

public readonly record struct ThemeOklch(double L, double C, double H)
{
    public static ThemeOklch FromColor(ThemeColor color)
    {
        var r = Linear(color.R); var g = Linear(color.G); var b = Linear(color.B);
        var l = Math.Cbrt(.4122214708 * r + .5363325363 * g + .0514459929 * b);
        var m = Math.Cbrt(.2119034982 * r + .6806995451 * g + .1073969566 * b);
        var s = Math.Cbrt(.0883024619 * r + .2817188376 * g + .6299787005 * b);
        var a = 1.9779984951 * l - 2.428592205 * m + .4505937099 * s;
        var axis = .0259040371 * l + .7827717662 * m - .808675766 * s;
        var angle = Math.Atan2(axis, a) * 180 / Math.PI;
        return new(.2104542553 * l + .793617785 * m - .0040720468 * s, Math.Sqrt(a * a + axis * axis), angle < 0 ? angle + 360 : angle);
    }

    public ThemeColor ToColor(byte alpha = 255)
    {
        var basis = new ThemeOklch(Math.Clamp(L, 0, 1), Math.Max(0, C), ((H % 360) + 360) % 360);
        var rgb = basis.Rgb();
        if (!Fits(rgb))
        {
            var found = false;
            for (var index = 0; index < 24; index++)
            {
                rgb = (basis with { C = basis.C * Math.Pow(.9, index + 1) }).Rgb();
                if (!Fits(rgb)) continue;
                found = true;
                break;
            }
            if (!found) rgb = (basis with { C = 0 }).Rgb();
        }
        return ThemeColor.FromInts(rgb.R * 255, rgb.G * 255, rgb.B * 255, alpha);
    }

    private (double R, double G, double B) Rgb()
    {
        var a = C * Math.Cos(H * Math.PI / 180); var b = C * Math.Sin(H * Math.PI / 180);
        var lr = L + .3963377774 * a + .2158037573 * b;
        var mr = L - .1055613458 * a - .0638541728 * b;
        var sr = L - .0894841775 * a - 1.291485548 * b;
        var l = lr * lr * lr; var m = mr * mr * mr; var s = sr * sr * sr;
        return (Srgb(4.0767416621 * l - 3.3077115913 * m + .2309699292 * s),
            Srgb(-1.2684380046 * l + 2.6097574011 * m - .3413193965 * s), Srgb(-.0041960863 * l - .7034186147 * m + 1.707614701 * s));
    }
    private static bool Fits((double R, double G, double B) value) => value.R is >= 0 and <= 1 && value.G is >= 0 and <= 1 && value.B is >= 0 and <= 1;
    private static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static double Srgb(double value) => value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
}
