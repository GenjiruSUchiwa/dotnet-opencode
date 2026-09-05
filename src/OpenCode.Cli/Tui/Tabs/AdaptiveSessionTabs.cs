namespace OpenCode.Cli.Tui.Tabs;

public sealed record AdaptiveSessionTabLayout(IReadOnlyList<SessionTab> Tabs, IReadOnlyList<int> Widths,
    int Before, int After, int Start, int Total);
public sealed record SessionTabMove(Guid Key, int Index);
public enum SessionTabOrientation { Horizontal, Vertical }
public enum SessionTabScope { Cwd, Global }
public enum SessionTabUnread { Activity, Error }
public enum SessionTabAttention { Permission, Question }
public enum SessionTabSpinner { Dots, Arcs, Quadrants, Line }
public enum SessionTabUnreadMarker { SmallDot, Dot, Square, LargeSquare }

/// <summary>Direct integer-layout port of context/session-tabs-model.ts; widths are terminal cells.</summary>
public static class AdaptiveSessionTabs
{
    public const int Width = 22;
    public const int MaximumWidth = 32;
    public const int MinimumWidth = 8;
    public const int SidebarWidth = 42;
    public static int OverflowWidth(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 2;
    public static bool FitsVertically(int total, int width = SidebarWidth) => total >= width + 44;
    public static int ClampVerticalWidth(int width, int total) => Math.Max(24, Math.Min(width, Math.Min(72, total - 44)));
    public static string Detail(string project, string? branch, string? defaultBranch, bool worktree) =>
        worktree && branch is not null && branch != defaultBranch ? project.Length > 0 ? $"{project} ⎇ {branch}" : branch : project;

    public static AdaptiveSessionTabLayout Layout(IReadOnlyList<SessionTab> tabs, Guid selected, int available, int previousStart = 0)
    {
        if (tabs.Count == 0) return new([], [], 0, 0, 0, 0);
        var active = tabs.ToList().FindIndex(tab => tab.Key == selected);
        int Fit(int width) => Math.Min(tabs.Count, Math.Max(1, active == -1
            ? (int)Math.Floor(Math.Max(0, width) / (double)MinimumWidth)
            : 1 + (int)Math.Floor((Math.Max(0, width) - Width) / (double)MinimumWidth)));
        var count = Fit(available);
        var start = previousStart;
        for (var attempt = 3; ; attempt--)
        {
            var bounded = Math.Clamp(start, 0, tabs.Count - count);
            start = Math.Clamp(active == -1 ? bounded : active < bounded ? active : active >= bounded + count ? active - count + 1 : bounded,
                0, tabs.Count - count);
            var markers = (start > 0 ? OverflowWidth(start) : 0) + (start + count < tabs.Count ? OverflowWidth(tabs.Count - start - count) : 0);
            var next = Fit(available - markers);
            if (next == count || attempt == 0) break;
            count = next;
        }
        var visible = tabs.Skip(start).Take(count).ToArray();
        var after = tabs.Count - start - count;
        var contentWidth = Math.Max(1, available - (start > 0 ? OverflowWidth(start) : 0) - (after > 0 ? OverflowWidth(after) : 0));
        var roomy = contentWidth >= Width * visible.Length;
        var total = roomy ? Math.Min(contentWidth, MaximumWidth * visible.Length) : contentWidth;
        if (roomy || active == -1)
        {
            var width = total / visible.Length;
            var remainder = total - width * visible.Length;
            return new(visible, visible.Select((_, index) => width + (index < remainder ? 1 : 0)).ToArray(), start, after, start, total);
        }
        var inactiveWidth = visible.Length == 1 ? 0 : Math.Min(Width,
            Math.Max(MinimumWidth, (int)Math.Floor((total - Math.Min(Width, total)) / (double)(visible.Length - 1))));
        var activeWidth = visible.Length == 1 ? total : total - inactiveWidth * (visible.Length - 1);
        return new(visible, visible.Select(tab => tab.Key == selected ? activeWidth : inactiveWidth).ToArray(), start, after, start, total);
    }
}
