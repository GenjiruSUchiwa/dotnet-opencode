namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Layout;
using OpenTui.Blazor;
using OpenTui.Blazor.TextMarks;

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionPresentation?>? ReadPresentation { get; set; }
    private readonly SessionFrameState _frame = new();
    private SessionFrame? _sessionFrame;
    private SessionPresentation? _presentation;
    private SessionSummary Summary => SessionSummary.Create(_transcriptMessages, _presentation?.Models ?? _catalog?.Models ?? [],
        _sessionId is { } id && ReadSessionObservation?.Invoke(id)?.Session is { } observed ? observed
            : _presentation?.Session is { } session && session.Id == _sessionId ? session : null, _projectedHistory);
    private int RunningChildren => _formScope?.Descendants.Count(id => _tabActivity.TryGetValue(id, out var activity) && activity.Busy == true) ?? 0;
    private void ToggleSidebar()
    {
        if (TerminalVisible) { HideTerminal(); _frame.OpenSidebar(); }
        else _frame.ToggleSidebar(_width, _childSession);
        _dirty = true;
        StateHasChanged();
    }
    private void CloseSidebar() { _frame.CloseSidebar(); _dirty = true; }
    private void JumpToLatest() { TranscriptScroll.ScrollToEnd(); _dirty = true; }

    private void ReadSessionPresentation()
    {
        var next = ReadPresentation?.Invoke();
        if (next is not null && next.Session?.Id != _sessionId) return;
        if (ReferenceEquals(next, _presentation)) return;
        _presentation = next;
        _dirty = true;
    }

    private bool HandleSidebarKey(ConsoleKeyInfo key)
    {
        if (!_hasConversation || !_frame.Overlay(_width, _childSession) || PromptBlocked) return false;
        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)) CloseSidebar();
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.PageUp) _sessionFrame?.ScrollSidebar(key.Key == ConsoleKey.PageUp ? -Math.Max(1, _height / 2) : -1);
        if (key.Key is ConsoleKey.DownArrow or ConsoleKey.PageDown) _sessionFrame?.ScrollSidebar(key.Key == ConsoleKey.PageDown ? Math.Max(1, _height / 2) : 1);
        return true;
    }

}
