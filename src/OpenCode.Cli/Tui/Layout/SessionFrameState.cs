namespace OpenCode.Cli.Tui.Layout;

/// <summary>Source session-frame geometry. Terminal panes are not advertised until their transport is implemented.</summary>
public sealed class SessionFrameState
{
    public const int SidebarWidth = 42;
    public bool SidebarHidden { get; private set; }
    public bool SidebarOpen { get; private set; }
    public bool TerminalVisible { get; private set; }
    private int? _terminalWidth;
    public int TerminalWidth(int availableWidth)
    {
        var half = Math.Max(1, availableWidth / 2);
        return Math.Max(Math.Min(24, half), Math.Min(_terminalWidth ?? half, Math.Max(half, availableWidth - 44)));
    }
    public void ShowTerminal(bool visible) { TerminalVisible = visible; if (visible) SidebarOpen = false; }
    public void ResizeTerminal(int width, int availableWidth) { _terminalWidth = width; _terminalWidth = TerminalWidth(availableWidth); }
    public bool Wide(int availableWidth) => availableWidth > 120;
    public bool SidebarVisible(int availableWidth, bool child) => !TerminalVisible && !child && (SidebarOpen || !SidebarHidden && Wide(availableWidth));
    public bool Overlay(int availableWidth, bool child) => SidebarVisible(availableWidth, child) && !Wide(availableWidth);
    public int ContentWidth(int availableWidth, bool child) => Math.Max(1,
        availableWidth - (TerminalVisible ? TerminalWidth(availableWidth) : SidebarVisible(availableWidth, child) && Wide(availableWidth) ? SidebarWidth : 0));

    public void ToggleSidebar(int availableWidth, bool child)
    {
        if (child) return;
        var visible = SidebarVisible(availableWidth, child);
        SidebarHidden = visible;
        SidebarOpen = !visible;
        if (SidebarOpen) TerminalVisible = false;
    }

    public void CloseSidebar() { SidebarOpen = false; SidebarHidden = true; }
    public void OpenSidebar() { SidebarOpen = true; SidebarHidden = false; TerminalVisible = false; }
    public void SetSidebarPreference(bool hidden) { SidebarHidden = hidden; SidebarOpen = false; }
}
