namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using OpenTui.Native;
using OpenCode.Cli.Tui.Theme;

public partial class Wordmark : ComponentBase
{
    [Parameter] public int TerminalWidth { get; set; } = 80;
    [Parameter] public WordmarkTheme? Theme { get; set; }
    // The agreed dotnet badge is the sole brand-color deviation; the logo uses theme roles.
    private static readonly ThemeColor DotnetLabelColor = ThemeColor.Parse("#7B61FF");
    private WordmarkTheme? _colors;
    private int _mode = -1;
    private List<LogoLine> _rows = [];
    private int LogoWidth => TerminalWidth < 22 ? 4 : TerminalWidth < 44 ? 19 : 39;
    private sealed record LogoSpan(string Text, NativeRgba Foreground, NativeRgba? Background, bool Bold);
    private sealed record LogoLine(int Width, ImmutableArray<NativeTextRun> Runs);

    protected override void OnParametersSet()
    {
        var mode = TerminalWidth < 22 ? 0 : TerminalWidth < 44 ? 1 : 2;
        var colors = Theme ?? WordmarkTheme.Default;
        if (_mode == mode && Equals(_colors, colors)) return;
        _mode = mode;
        _colors = colors;
        // Upstream logo.ts geometry, including the raised d and quarter-tint shadows.
        string[] left = ["                   ", "█▀▀█ █▀▀█ █▀▀█ █▀▀▄", "█__█ █__█ █^^^ █__█", "▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀~~▀"];
        string[] right = ["             ▄     ", "█▀▀▀ █▀▀█ █▀▀█ █▀▀█", "█___ █__█ █__█ █^^^", "▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀"];
        var rows = mode == 0 ? ["█▀▀█", "█__█", "▀▀▀▀"]
            : mode == 1 ? left.Skip(1).Concat(right).ToArray()
            : left.Select((line, index) => line + " " + right[index]).ToArray();
        _rows = rows.Select((line, index) => Segments(line, index, mode, colors)).ToList();
    }

    private static LogoLine Segments(string line, int row, int mode, WordmarkTheme colors)
    {
        var spans = new List<LogoSpan>();
        for (var column = 0; column < line.Length; column++)
        {
            var c = line[column];
            var muted = mode == 2 ? column < 20 : mode == 1 && row < 3;
            var textColor = muted ? colors.Subdued : colors.Text;
            var shadow = ThemeColor.Tint(colors.Background, textColor, .25).Native;
            var foreground = c is '~' or ',' ? shadow : textColor.Native;
            NativeRgba? background = c is '_' or '^' ? shadow : null;
            var text = c switch { '_' => " ", '^' or '~' => "▀", ',' => "▄", _ => c.ToString() };
#pragma warning disable MA0065 // NativeRgba keeps CLR field equality (including floating-point NaN equality), not a new native ABI/equality contract.
            if (spans.Count > 0 && spans[^1].Foreground.Equals(foreground) && Nullable.Equals(spans[^1].Background, background) && spans[^1].Bold == !muted)
#pragma warning restore MA0065
                spans[^1] = spans[^1] with { Text = spans[^1].Text + text };
            else spans.Add(new(text, foreground, background, !muted));
        }
        return new(line.Length, spans.Select(span => new NativeTextRun(span.Text, span.Foreground, span.Background, span.Bold ? 1u : 0u)).ToImmutableArray());
    }
}
