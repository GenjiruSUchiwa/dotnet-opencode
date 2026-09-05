namespace OpenCode.Cli.Tui.Theme;

using System.Text.Json.Nodes;

/// <summary>Caller-supplied terminal palette snapshot. This module never queries the terminal.</summary>
public sealed record TerminalThemePalette(IReadOnlyList<string?> Palette, string? DefaultBackground = null, string? DefaultForeground = null);

public static class SystemTheme
{
    public static ThemeMode? DetectMode(TerminalThemePalette palette) => palette.DefaultBackground is null ? null
        : ThemeColor.Parse(palette.DefaultBackground).Luminance > .5 ? ThemeMode.Light : ThemeMode.Dark;

    public static JsonObject Generate(TerminalThemePalette colors, ThemeMode mode)
    {
        var background = ThemeColor.Parse(colors.DefaultBackground ?? Palette(0) ?? throw new NotSupportedException("Terminal background palette is unavailable."));
        var foreground = ThemeColor.Parse(colors.DefaultForeground ?? Palette(7) ?? throw new NotSupportedException("Terminal foreground palette is unavailable."));
        var dark = mode == ThemeMode.Dark;
        var grays = new Dictionary<int, ThemeColor>();
        var luminance = background.Luminance * 255;
        for (var index = 1; index <= 12; index++)
        {
            var factor = index / 12.0;
            if (dark && luminance < 10)
            {
                var gray = Math.Floor(factor * .4 * 255); grays[index] = ThemeColor.FromInts(gray, gray, gray); continue;
            }
            if (!dark && luminance > 245)
            {
                var gray = Math.Floor(255 - factor * .4 * 255); grays[index] = ThemeColor.FromInts(gray, gray, gray); continue;
            }
            var next = dark ? luminance + (255 - luminance) * factor * .4 : luminance * (1 - factor * .4);
            var ratio = next / luminance;
            grays[index] = ThemeColor.FromInts(Math.Floor(Math.Clamp(background.Red * ratio, 0, 255)),
                Math.Floor(Math.Clamp(background.Green * ratio, 0, 255)), Math.Floor(Math.Clamp(background.Blue * ratio, 0, 255)));
        }
        var mutedGray = dark ? luminance < 10 ? 180 : Math.Min(Math.Floor(160 + luminance * .3), 200)
            : luminance > 245 ? 75 : Math.Max(Math.Floor(100 - (255 - luminance) * .2), 60);
        var muted = ThemeColor.FromInts(mutedGray, mutedGray, mutedGray);
        var red = Color(1); var green = Color(2); var yellow = Color(3); var blue = Color(4); var magenta = Color(5); var cyan = Color(6);
        var alpha = dark ? .22 : .14;
        var theme = new JsonObject();
        foreach (var (key, color) in new (string, ThemeColor)[] {
            ("primary",cyan),("secondary",magenta),("accent",cyan),("error",red),("warning",yellow),("success",green),("info",cyan),
            ("text",foreground),("textMuted",muted),("selectedListItemText",background),
            ("background",new ThemeColor(background.Red,background.Green,background.Blue,0)),
            ("backgroundPanel",grays[2]),("backgroundElement",grays[3]),("backgroundMenu",grays[3]),
            ("borderSubtle",grays[6]),("border",grays[7]),("borderActive",grays[8]),
            ("diffAdded",green),("diffRemoved",red),("diffContext",grays[7]),("diffHunkHeader",grays[7]),
            ("diffHighlightAdded",Color(10)),("diffHighlightRemoved",Color(9)),
            ("diffAddedBg",ThemeColor.Tint(background,green,alpha)),("diffRemovedBg",ThemeColor.Tint(background,red,alpha)),
            ("diffContextBg",grays[2]),("diffLineNumber",muted),
            ("diffAddedLineNumberBg",ThemeColor.Tint(grays[2],green,alpha)),("diffRemovedLineNumberBg",ThemeColor.Tint(grays[2],red,alpha)),
            ("markdownText",foreground),("markdownHeading",foreground),("markdownLink",blue),("markdownLinkText",cyan),
            ("markdownCode",green),("markdownBlockQuote",yellow),("markdownEmph",yellow),("markdownStrong",foreground),
            ("markdownHorizontalRule",grays[7]),("markdownListItem",blue),("markdownListEnumeration",cyan),
            ("markdownImage",blue),("markdownImageText",cyan),("markdownCodeBlock",foreground),
            ("syntaxComment",muted),("syntaxKeyword",magenta),("syntaxFunction",blue),("syntaxVariable",foreground),
            ("syntaxString",green),("syntaxNumber",yellow),("syntaxType",cyan),("syntaxOperator",cyan),("syntaxPunctuation",foreground) }) theme[key] = color.Hex;
        return new JsonObject { ["theme"] = theme };
        string? Palette(int index) => index < colors.Palette.Count ? colors.Palette[index] : null;
        ThemeColor Color(int index) => Palette(index) is { } value ? ThemeColor.Parse(value) : ThemeColor.Ansi(index);
    }
}
