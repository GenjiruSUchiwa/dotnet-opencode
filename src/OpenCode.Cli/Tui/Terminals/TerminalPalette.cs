namespace OpenCode.Cli.Tui.Terminals;

using System.Text;
using OpenCode.Cli.Tui.Theme;

public static class TerminalPalette
{
    /// <summary>Source terminal-pane.tsx ANSI palette. These bytes go into the embedded emulator only.</summary>
    public static byte[] Encode(ThemeTokens theme)
    {
        var basis = theme.Mode == ThemeMode.Dark ? 200 : 800;
        var bright = theme.Mode == ThemeMode.Dark ? 100 : 900;
        var colors = new[]
        {
            theme.Background, theme.Color("text.feedback.error.default"), theme.Color("text.feedback.success.default"), theme.Color("text.feedback.warning.default"),
            theme.Hue["blue"][basis], theme.Hue["purple"][basis], theme.Color("text.feedback.info.default"), theme.Text,
            theme.Subdued, theme.Color("text.feedback.error.subdued"), theme.Color("text.feedback.success.subdued"), theme.Color("text.feedback.warning.subdued"),
            theme.Hue["blue"][bright], theme.Hue["purple"][bright], theme.Hue["cyan"][bright], theme.Hue["neutral"][theme.Mode == ThemeMode.Dark ? 100 : 900]
        };
        return Encoding.UTF8.GetBytes(string.Concat(colors.Select((color, index) => $"\x1b]4;{index};{Rgb(color)}\x1b\\")) +
            $"\x1b]10;{Rgb(theme.Text)}\x1b\\\x1b]11;{Rgb(theme.Background)}\x1b\\");
    }
    private static string Rgb(ThemeColor color) => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";
}
