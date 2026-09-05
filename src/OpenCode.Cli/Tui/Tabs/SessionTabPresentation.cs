namespace OpenCode.Cli.Tui.Tabs;

using System.Collections.Immutable;
using System.Globalization;
using OpenTui.Native;

/// <summary>Resolved semantic roles; the host supplies its current light/dark/elevated theme.</summary>
public sealed record SessionTabsTheme(string Text, string Subdued, string Background, string Selected, string Hovered,
    string FormField, string Running, string Unread, string Permission, string Question, string Error,
    string DestructiveBackground, string DestructiveText)
{
    public static SessionTabsTheme Default { get; } = new("#EEEEEE", "#888888", "#0A0A0A", "#1E1E1E", "#202020",
        "#AAAAAA", "#5EC4FF", "#A3E08A", "#E9B86E", "#8CCBEA", "#F08080", "#642A2A", "#FFFFFF");
}

public sealed record SessionTabContextRequest(SessionTab? Tab, int X, int Y);
public sealed record SessionTabMenuAction(string Title, Func<Task> Run);

public static class SessionTabMenu
{
    public static IReadOnlyList<SessionTabMenuAction> Actions(SessionTabContextRequest request, Func<Guid, Task> close,
        Func<Task>? add = null, Func<SessionTab, Task>? rename = null, Func<Guid, Task>? promote = null)
    {
        var result = new List<SessionTabMenuAction>();
        if (add is not null) result.Add(new("New tab", add));
        if (request.Tab is not { } tab) return result;
        if (tab.Preview && promote is not null) result.Add(new("Keep open", () => promote(tab.Key)));
        if (rename is not null) result.Add(new("Rename", () => rename(tab)));
        result.Add(new("Close", () => close(tab.Key)));
        return result;
    }
}

/// <summary>Display-cell measurement comes from OpenTUI, never UTF-16 length or an ASCII width guess.</summary>
internal sealed class SessionTabText : IDisposable
{
    private NativeTextView? _measure;
    private readonly Dictionary<string, int> _widths = new(StringComparer.Ordinal);
    public int Width(string value)
    {
        if (_widths.TryGetValue(value, out var width)) return width;
        _measure ??= new NativeTextView();
        _measure.SetWrapMode(NativeTextWrapMode.None);
        _measure.SetText(value);
        width = (int)_measure.Measure(0).WidthColumns;
        if (_widths.Count > 4096) _widths.Clear();
        _widths[value] = width;
        return width;
    }

    public ImmutableArray<NativeTextRun> Runs(string title, int width, int offset, string foreground, string background,
        bool bold, bool italic, bool dim, bool fade = true,
        Func<int, NativeRgba>? cellBackground = null, int column = 0, Func<int, double>? shade = null)
    {
        if (width <= 0) return [];
        var overflows = Width(title) > width;
        var parts = Graphemes(overflows && offset > 0 ? title + " · " + title + " · " : title).ToArray();
        var cursor = overflows && offset > 0 ? offset % Math.Max(1, Width(title + " · ")) : 0;
        var index = 0;
        var skipped = 0;
        while (index < parts.Length && skipped < cursor) skipped += Width(parts[index++]);
        var output = ImmutableArray.CreateBuilder<NativeTextRun>();
        var cells = 0;
        while (index < parts.Length)
        {
            var text = parts[index++];
            var length = Width(text);
            if (cells + length > width) break;
            var intensity = fade && overflows && width > 4
                ? Math.Max(offset > 0 && cells < 4 ? 0.2 + 0.72 * (3 - cells) / 3 : 0,
                    cells >= width - 4 ? 0.2 + 0.72 * (cells - (width - 4)) / 3 : 0) : 0;
            var color = Blend(Parse(foreground), Parse(background), intensity);
            output.Add(new NativeTextRun(text, shade is null ? color : Blend(color, Parse(background), shade(cells)),
                Background: cellBackground?.Invoke(column + cells),
                Attributes: (bold ? 1u : 0) | (dim ? 2u : 0) | (italic ? 4u : 0)));
            cells += length;
        }
        if (cellBackground is not null)
            while (cells < width) output.Add(new NativeTextRun(" ", Background: cellBackground(column + cells++)));
        return output.ToImmutable();
    }

    private static IEnumerable<string> Graphemes(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) yield return enumerator.GetTextElement();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Keep the resolved-tab-color error text unchanged in the UI.")]
    internal static NativeRgba Parse(string value)
    {
        var hex = value.AsSpan().TrimStart('#');
        if (hex.Length is not (6 or 8)) throw new ArgumentException("Tab colors must be resolved hexadecimal RGBA colors.");
        return new(byte.Parse(hex[..2], NumberStyles.HexNumber), byte.Parse(hex.Slice(2, 2), NumberStyles.HexNumber),
            byte.Parse(hex.Slice(4, 2), NumberStyles.HexNumber), hex.Length == 8 ? byte.Parse(hex.Slice(6, 2), NumberStyles.HexNumber) : (byte)255);
    }

    internal static NativeRgba Blend(NativeRgba from, NativeRgba to, double amount) => new(
        (byte)Math.Clamp(Math.Round(from.R + (to.R - from.R) * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(from.G + (to.G - from.G) * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(from.B + (to.B - from.B) * amount), 0, 255));

    internal static string Hex(NativeRgba color) => $"#{(byte)color.R:X2}{(byte)color.G:X2}{(byte)color.B:X2}";

    public void Dispose() => _measure?.Dispose();
}
