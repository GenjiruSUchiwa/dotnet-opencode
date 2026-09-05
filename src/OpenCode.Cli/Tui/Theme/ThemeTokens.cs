namespace OpenCode.Cli.Tui.Theme;

public enum ThemeMode { Light, Dark }
public enum ThemeModePreference { System, Light, Dark }
public enum ThemeContext { Base, Elevated, Overlay }
public enum ThemeActionState { Default, Disabled, Pressed, Focused, Selected, Hovered }
public enum ThemeActionVariant { Primary, Secondary, Destructive }
public sealed record ThemeHueSource(string Hue, int Step);

/// <summary>Semantic token view. Convert a selected ThemeColor to Native only at the render boundary.</summary>
public sealed class ThemeTokens
{
    private readonly IReadOnlyDictionary<string, ThemeColor> _colors;
    private readonly IReadOnlyDictionary<ThemeColor, ThemeHueSource> _sources;
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, ThemeColor>> Hue { get; }
    public IReadOnlyList<IReadOnlyDictionary<int, ThemeColor>> Categorical { get; }
    public ThemeMode Mode { get; }
    public IReadOnlyDictionary<string, ThemeColor> Colors => _colors;
    public ThemeColor Text => Color("text.default");
    public ThemeColor Subdued => Color("text.subdued");
    public ThemeColor Background => Color("background.default");
    public ThemeColor Border => Color("border.default");
    public ThemeColor Scrollbar => Color("scrollbar.default");

    internal ThemeTokens(ThemeMode mode, IReadOnlyDictionary<string, ThemeColor> colors,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, ThemeColor>> hue,
        IReadOnlyList<IReadOnlyDictionary<int, ThemeColor>> categorical, IReadOnlyDictionary<ThemeColor, ThemeHueSource> sources)
    { Mode = mode; _colors = colors; Hue = hue; Categorical = categorical; _sources = sources; }

    public ThemeColor Color(string path) => _colors.TryGetValue(path, out var value) ? value : throw new KeyNotFoundException($"Theme token not found: {path}");
    public ThemeColor ActionText(ThemeActionVariant variant, ThemeActionState state = ThemeActionState.Default) => Color($"text.action.{Key(variant)}.{Key(state)}");
    public ThemeColor ActionBackground(ThemeActionVariant variant, ThemeActionState state = ThemeActionState.Default) => Color($"background.action.{Key(variant)}.{Key(state)}");
    public ThemeColor FormfieldText(ThemeActionState state = ThemeActionState.Default) => Color($"text.formfield.{Key(state)}");
    public ThemeColor FormfieldBackground(ThemeActionState state = ThemeActionState.Default) => Color($"background.formfield.{Key(state)}");
    public ThemeHueSource? Source(ThemeColor color) => _sources.GetValueOrDefault(color);
    public ThemeColor Increase(ThemeColor color, double amount = 1) => Shift(color, amount);
    public ThemeColor Decrease(ThemeColor color, double amount = 1) => Shift(color, -amount);
    public ThemeColor Raise(ThemeColor color) => Mode == ThemeMode.Light ? Increase(color) : Decrease(color);
    private ThemeColor Shift(ThemeColor color, double amount)
    {
        if (Source(color) is not { } source) return color;
        var offset = double.IsFinite(amount) ? Math.Truncate(amount) : 0;
        return Hue[source.Hue][(int)Math.Clamp(source.Step / 100.0 + offset, 1, 9) * 100];
    }
    private static string Key<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();
}

public sealed record ResolvedTheme(ThemeMode Mode, IReadOnlyList<ThemeMode> Modes, ThemeTokens Base, ThemeTokens Elevated, ThemeTokens Overlay)
{
    public ThemeTokens ForContext(ThemeContext context = ThemeContext.Base) => context switch
    { ThemeContext.Base => Base, ThemeContext.Elevated => Elevated, ThemeContext.Overlay => Overlay, _ => throw new ArgumentOutOfRangeException(nameof(context)) };
}
