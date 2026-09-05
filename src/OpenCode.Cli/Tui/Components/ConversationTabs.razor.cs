namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Native;

public partial class ConversationTabs : IDisposable
{
    [Inject] public TimeProvider Clock { get; set; } = TimeProvider.System;
    [Parameter, EditorRequired] public IReadOnlyList<SessionTab> Tabs { get; set; } = [];
    [Parameter] public Guid Selected { get; set; }
    [Parameter] public Guid? Busy { get; set; }
    [Parameter] public IReadOnlyDictionary<SessionId, SessionTabActivity> Activity { get; set; } = new Dictionary<SessionId, SessionTabActivity>();
    [Parameter] public int TerminalWidth { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public SessionTabOrientation Orientation { get; set; }
    [Parameter] public int VerticalWidth { get; set; } = AdaptiveSessionTabs.SidebarWidth;
    [Parameter] public bool Numbers { get; set; }
    [Parameter] public bool Animations { get; set; } = true;
    [Parameter] public SessionTabSpinner Spinner { get; set; }
    [Parameter] public SessionTabUnreadMarker UnreadMarker { get; set; }
    [Parameter] public SessionTabsTheme Theme { get; set; } = SessionTabsTheme.Default;
    [Parameter] public EventCallback<Guid> OnSelect { get; set; }
    [Parameter] public EventCallback<Guid> OnClose { get; set; }
    [Parameter] public EventCallback OnAdd { get; set; }
    [Parameter] public EventCallback<SessionTabMove> OnMove { get; set; }
    [Parameter] public EventCallback<Guid> OnPromote { get; set; }
    [Parameter] public EventCallback<SessionTabContextRequest> OnContextMenu { get; set; }
    private AdaptiveSessionTabLayout _layout = new([], [], 0, 0, 0, 0);
    private readonly SessionTabText _text = new();
    private readonly Dictionary<Guid, SessionTabPulse> _pulses = [];
    private readonly Dictionary<Guid, SessionTabTitleMotion> _titles = [];
    private readonly Dictionary<Guid, SessionTabIndicatorMotion> _indicators = [];
    private readonly Dictionary<Guid, SessionTabSeparator> _separators = [];
    private SessionTabMotion _motion = null!;
    private SessionTabSeparator? _bottomSeparator;
    private Guid? _bottomKey;
    private bool _previousVertical;
    private bool _releasingHold;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _animation;
    private Guid? _hovered;
    private long _hoverTime;
    private Guid? _dragging;
    private SessionTabMove? _preview;
    private int _pressX;
    private int _pressY;
    private bool _didDrag;
    private int _verticalStart;
    private Guid _previousSelected;
    private int _previousWidth;
    private Guid? _lastClick;
    private long _lastClickTime;
    private long _tick;
    private bool _disposed;
    private CloseHold? _hold;
    private sealed record CloseHold(Guid[] Items, Guid[] Visible, int[] Widths, Guid Closed, Guid Target, int X, long Until);
    private bool Vertical => Orientation == SessionTabOrientation.Vertical;
    private int RailWidth => Math.Min(Math.Max(1, TerminalWidth), AdaptiveSessionTabs.ClampVerticalWidth(VerticalWidth, TerminalWidth));
    private int NumberWidth => Math.Max(2, (Vertical ? Items.Count(tab => tab.SessionId is not null) : Items.Count).ToString(System.Globalization.CultureInfo.CurrentCulture).Length);
    private bool ShowPlus => OnAdd.HasDelegate && !Tabs.Any(tab => tab.Key == Selected && tab.SessionId is null);
    private IReadOnlyList<SessionTab> Items
    {
        get
        {
            var items = Tabs.Where(tab => tab.SessionId is not null).ToList();
            if (Tabs.FirstOrDefault(tab => tab.Key == Selected && tab.SessionId is null) is { } home) items.Add(home);
            if (_preview is not { } move) return items;
            var tab = items.FirstOrDefault(tab => tab.Key == move.Key);
            if (tab is null) return items;
            items.Remove(tab);
            items.Insert(Math.Clamp(move.Index, 0, items.Count), tab);
            return items;
        }
    }

    protected override void OnInitialized()
    {
        _motion = new(Clock);
        _tick = Clock.GetTimestampMilliseconds();
    }

    protected override void OnParametersSet()
    {
        if (_previousVertical != Vertical)
        {
            _pulses.Clear(); _titles.Clear(); _indicators.Clear(); _separators.Clear();
            _bottomSeparator = null; _bottomKey = null; _motion = new(Clock);
            _previousVertical = Vertical;
        }
        if (_previousWidth != TerminalWidth) { _hold = null; _previousWidth = TerminalWidth; }
        if (_preview is { } move && _dragging is null && Tabs.ToList().FindIndex(tab => tab.Key == move.Key) == move.Index) _preview = null;
        Reflow();
        if (_previousSelected != Selected)
        {
            var index = Items.ToList().FindIndex(tab => tab.Key == Selected);
            var count = Math.Max(1, (TerminalHeight - 2) / 3);
            if (index >= 0) _verticalStart = Math.Clamp(_verticalStart, Math.Max(0, index - count + 1), index);
            _previousSelected = Selected;
        }
        _animation ??= Animate();
    }

    private void Reflow()
    {
        var held = HeldLayout();
        _layout = held ?? AdaptiveSessionTabs.Layout(Items, Selected, TerminalWidth - (ShowPlus ? 3 : 0), _layout.Start);
        _motion.Update(_layout, Selected, tab => Status(tab).Complete, Animations && !Vertical, held is not null, _releasingHold);
        _releasingHold = false;
        UpdateVisuals();
        if (_hovered is { } hovered && !Items.Any(tab => tab.Key == hovered)) _hovered = null;
    }

    private void UpdateVisuals()
    {
        var mounted = Vertical ? Items.Where(tab => tab.SessionId is not null).ToArray() : _layout.Tabs.ToArray();
        for (var index = 0; index < mounted.Length; index++)
        {
            var tab = mounted[index];
            var status = Status(tab);
            if (!_pulses.TryGetValue(tab.Key, out var pulse)) _pulses[tab.Key] = new(status, tab.Key == Selected, Animations);
            else pulse.Update(status, tab.Key == Selected, Animations);
            if (!_titles.TryGetValue(tab.Key, out var title)) _titles[tab.Key] = title = new(Clock);
            title.Update(Title(tab), status.Renaming, Animations);
            if (!_indicators.TryGetValue(tab.Key, out var indicator)) _indicators[tab.Key] = indicator = new(Clock);
            indicator.Update(status, SessionTabText.Parse(status.Unread == SessionTabUnread.Error ? Theme.Error : Theme.Unread), Animations);
            if (!Vertical) continue;
            var previous = index > 0 ? mounted[index - 1] : null;
            var previousStatus = previous is null ? new SessionTabActivity() : Status(previous);
            if (!_separators.TryGetValue(tab.Key, out var separator))
                _separators[tab.Key] = new(status, tab.Key == Selected, previousStatus, previous?.Key == Selected, Theme, Animations);
            else separator.Update(status, tab.Key == Selected, previousStatus, previous?.Key == Selected, Theme, Animations);
        }
        var keys = mounted.Select(tab => tab.Key).ToHashSet();
        foreach (var key in _pulses.Keys.Where(key => !keys.Contains(key)).ToArray()) _pulses.Remove(key);
        foreach (var key in _titles.Keys.Where(key => !keys.Contains(key)).ToArray()) _titles.Remove(key);
        foreach (var key in _indicators.Keys.Where(key => !keys.Contains(key)).ToArray()) _indicators.Remove(key);
        foreach (var key in _separators.Keys.Where(key => !keys.Contains(key)).ToArray()) _separators.Remove(key);
        var last = Vertical ? mounted.LastOrDefault() : null;
        if (_bottomKey != last?.Key) { _bottomKey = last?.Key; _bottomSeparator = null; }
        if (last is null) return;
        _bottomSeparator ??= new(Status(last), last.Key == Selected, new(), false, Theme, Animations);
        _bottomSeparator.Update(Status(last), last.Key == Selected, new(), false, Theme, Animations);
    }

    private AdaptiveSessionTabLayout? HeldLayout()
    {
        if (_hold is not { } hold) return null;
        if (Clock.GetTimestampMilliseconds() > hold.Until) { _hold = null; _releasingHold = true; return null; }
        var ids = Items.Select(tab => tab.Key).ToArray();
        if (!ids.SequenceEqual(hold.Items) && !ids.SequenceEqual(hold.Items.Where(id => id != hold.Closed))) { _hold = null; return null; }
        var visible = hold.Visible.Where(ids.Contains).ToList();
        if (!visible.Contains(hold.Target)) { visible.Add(hold.Target); visible = visible.OrderBy(id => Array.IndexOf(ids, id)).ToList(); }
        var start = visible.Count == 0 ? -1 : Array.IndexOf(ids, visible[0]);
        if (start < 0 || visible.Where((id, index) => Array.IndexOf(ids, id) != start + index).Any()) return null;
        var widths = visible.Select(id => Array.IndexOf(hold.Visible, id) is var index && index >= 0 ? hold.Widths[index] : 1).ToArray();
        var target = visible.IndexOf(hold.Target);
        if (!ids.Contains(hold.Closed)) widths[target] = hold.X - (start > 0 ? AdaptiveSessionTabs.OverflowWidth(start) : 0) - widths.Take(target).Sum() + 2;
        if (widths.Any(width => width < 1) || widths.Sum() > TerminalWidth) return null;
        return new(visible.Select(id => Items.First(tab => tab.Key == id)).ToArray(), widths, start, ids.Length - start - visible.Count, start, widths.Sum());
    }

    private IEnumerable<(SessionTab Tab, int Width)> Visible()
    {
        if (Vertical) return Items.Where(tab => tab.SessionId is not null).Skip(_verticalStart)
            .Take(Math.Max(1, (TerminalHeight - 2) / 3)).Select(tab => (tab, RailWidth));
        var widths = _motion.Widths(Selected);
        return _layout.Tabs.Select((tab, index) => (tab, widths[index]));
    }
    private SessionTabActivity Status(SessionTab tab) => tab.SessionId is { } id && Activity.TryGetValue(id, out var status)
        ? status with { Busy = status.Busy == true || Busy == tab.Key } : new(Busy: Busy == tab.Key);
    private NativeRgba PulseCell(SessionTab tab, int width, int column, bool detail = false) => Vertical && column >= Math.Min(10, width)
        ? SessionTabText.Parse(Background(tab)) : _pulses[tab.Key].Background(column, Vertical ? Math.Min(10, width) : width,
            SessionTabText.Parse(Background(tab)), SessionTabText.Parse(Theme.Text), SessionTabText.Parse(IndicatorColor(tab)), Vertical, detail);
    private string Title(SessionTab tab) => Status(tab).Title ?? tab.Title;
    private string Background(SessionTab tab) => Vertical
        ? tab.Key == Selected ? Theme.Selected : _hovered == tab.Key || _dragging == tab.Key ? Theme.Hovered : Theme.Background
        : SessionTabText.Hex(SessionTabText.Blend(SessionTabText.Parse(_hovered == tab.Key && tab.Key != Selected || _dragging == tab.Key ? Theme.Hovered : Theme.Background),
            SessionTabText.Parse(Theme.Selected), _dragging == tab.Key ? 1 : _motion.Selection(tab.Key)));
    private int TitleWidth(SessionTab tab, int width) => Math.Max(0, width - NumberWidth - (Vertical ? 2 : 1) - (_hovered == tab.Key ? Vertical ? 1 : 2 : 0));
    private string Foreground(SessionTab tab) => _hovered == tab.Key ? Theme.Text : Vertical ? tab.Key == Selected ? Theme.Text : Theme.Subdued
        : SessionTabText.Hex(SessionTabText.Blend(SessionTabText.Parse(Theme.Subdued), SessionTabText.Parse(Theme.Text), _motion.Selection(tab.Key)));
    private ImmutableArray<NativeTextRun> TitleRuns(SessionTab tab, int width, int titleWidth)
    {
        var runs = _text.Runs(Title(tab), titleWidth, _hovered == tab.Key ? MarqueeOffset(tab, titleWidth) : 0,
            Foreground(tab), Background(tab), tab.Key == Selected || _dragging == tab.Key, tab.Preview, Status(tab).Renaming && !Animations,
            true, column => PulseCell(tab, width, column), NumberWidth + 1);
        return _titles[tab.Key].Paint(runs, titleWidth, _text, SessionTabText.Parse(Background(tab)),
            column => PulseCell(tab, width, NumberWidth + 1 + column));
    }
    private string MarkerColor(SessionTab tab)
    {
        var background = SessionTabText.Parse(Background(tab));
        var normal = SessionTabText.Parse(IndicatorColor(tab));
        var status = Status(tab);
        if (!Vertical && Numbers && !status.Running && status.Attention is null && status.Unread != SessionTabUnread.Error)
        {
            var idle = SessionTabText.Blend(SessionTabText.Parse(Theme.FormField), SessionTabText.Parse(Theme.Background), .55);
            var active = SessionTabText.Blend(SessionTabText.Parse(Theme.Text), background, .25);
            var basis = _hovered == tab.Key && tab.Key != Selected ? SessionTabText.Parse(Foreground(tab))
                : SessionTabText.Blend(idle, active, _motion.Selection(tab.Key));
            normal = SessionTabText.Blend(basis, SessionTabText.Parse(Theme.Unread), _motion.Activity(tab.Key));
        }
        return SessionTabText.Hex(_indicators[tab.Key].Color(normal, background, SessionTabText.Parse(Theme.Text), Numbers));
    }
    private string IndicatorColor(SessionTab tab) => Status(tab) switch
    {
        { Attention: SessionTabAttention.Permission } => Theme.Permission,
        { Attention: SessionTabAttention.Question } => Theme.Question,
        { Running: true } => Theme.Running,
        { Unread: SessionTabUnread.Error } => Theme.Error,
        { Unread: not null } => Theme.Unread,
        _ => tab.Key == Selected ? Theme.Text : Theme.FormField
    };
    private string Indicator(SessionTab tab)
    {
        if (tab.SessionId is null) return "+";
        if (Numbers) return (Items.ToList().FindIndex(item => item.Key == tab.Key) + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        var status = Status(tab);
        if (status.Attention == SessionTabAttention.Permission) return "!";
        if (status.Attention == SessionTabAttention.Question) return "?";
        if (status.Running)
        {
            var frames = Spinner switch
            {
                SessionTabSpinner.Arcs => new[] { "◜", "◝", "◞", "◟" },
                SessionTabSpinner.Quadrants => ["◴", "◷", "◶", "◵"],
                SessionTabSpinner.Line => ["|", "/", "-", "\\"],
                _ => ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"]
            };
            return frames[Animations ? (int)((_tick / (Spinner == SessionTabSpinner.Dots ? 80 : 120)) % frames.Length) : 0];
        }
        return status.Unread is null && !_indicators[tab.Key].Fading ? "" : UnreadMarker switch
        { SessionTabUnreadMarker.Dot => "●", SessionTabUnreadMarker.Square => "▪", SessionTabUnreadMarker.LargeSquare => "■", _ => "•" };
    }

    private int MarqueeOffset(SessionTab tab, int width)
    {
        if (!Animations || _text.Width(Title(tab)) <= width) return 0;
        var elapsed = Clock.GetTimestampMilliseconds() - _hoverTime - 600;
        var offset = elapsed < 0 ? 0 : 1 + (int)(elapsed / 80);
        return offset >= _text.Width(Title(tab) + " · ") ? 0 : offset;
    }
    private void Enter(SessionTab tab, int width) { if (_hovered != tab.Key) _hoverTime = Clock.GetTimestampMilliseconds(); _hovered = tab.Key; }
    private void Leave(SessionTab tab) { if (_hovered == tab.Key) _hovered = null; }
    private void LeaveStrip(TerminalPointerEventArgs args) { _hovered = null; ReleaseHold(); }
    private void ReleaseHold() { if (_hold is null) return; _hold = null; _releasingHold = true; Reflow(); }
    private Task CloseDown(SessionTab tab, TerminalPointerEventArgs args)
    {
        if (args.Button == TerminalPointerButton.Right) return Task.CompletedTask;
        args.Handled = true; _didDrag = false; _dragging = null;
        return args.Button == TerminalPointerButton.Middle ? OnClose.InvokeAsync(tab.Key) : Task.CompletedTask;
    }

    private async Task Down(SessionTab tab, TerminalPointerEventArgs args)
    {
        args.Handled = true;
        ReleaseHold();
        if (args.Button == TerminalPointerButton.Middle) { await OnClose.InvokeAsync(tab.Key); return; }
        if (args.Button == TerminalPointerButton.Right) { await OnContextMenu.InvokeAsync(new(tab.SessionId is null ? null : tab, args.ScreenX, args.ScreenY)); return; }
        if (args.Button != TerminalPointerButton.Left) return;
        var now = Clock.GetTimestampMilliseconds();
        var promote = tab.Preview && _lastClick == tab.Key && now - _lastClickTime < 300;
        _lastClick = tab.Key; _lastClickTime = now;
        _pressX = args.ScreenX; _pressY = args.ScreenY; _dragging = tab.Key; _didDrag = false;
        if (promote) await OnPromote.InvokeAsync(tab.Key);
    }

    private void Drag(TerminalPointerEventArgs args)
    {
        if (_dragging is not { } key || args.ScreenX == _pressX && args.ScreenY == _pressY) return;
        args.Handled = true;
        _didDrag = true;
        if (!OnMove.HasDelegate || Items.FirstOrDefault(tab => tab.Key == key)?.SessionId is null) return;
        var slot = Vertical ? Math.Max(0, (args.Y - 1) / 3 + _verticalStart) : _layout.Start;
        if (!Vertical)
        {
            var edge = _layout.Before > 0 ? AdaptiveSessionTabs.OverflowWidth(_layout.Before) : 0;
            foreach (var width in _layout.Widths) { edge += width; if (args.X < edge) break; slot++; }
        }
        _preview = new(key, Math.Clamp(slot, 0, Math.Max(0, Items.Count(tab => tab.SessionId is not null) - 1)));
        Reflow();
    }

    private async Task Release(TerminalPointerEventArgs args)
    {
        if (_dragging is not { } key || args.Button == TerminalPointerButton.Right) return;
        args.Handled = true;
        _dragging = null;
        if (_didDrag && _preview is { } move) await OnMove.InvokeAsync(move);
        await OnSelect.InvokeAsync(key);
    }

    private async Task Close(SessionTab tab, TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (_didDrag || _hovered != tab.Key) return;
        if (!Vertical)
        {
            var items = Items.ToArray();
            var index = Array.FindIndex(items, item => item.Key == tab.Key);
            var target = items.ElementAtOrDefault(index + 1) ?? items.ElementAtOrDefault(index - 1);
            var visible = _layout.Tabs.ToList().FindIndex(item => item.Key == tab.Key);
            if (target is not null && visible >= 0)
                _hold = new(items.Select(item => item.Key).ToArray(), _layout.Tabs.Select(item => item.Key).ToArray(), _motion.Widths(Selected),
                    tab.Key, target.Key, (_layout.Before > 0 ? AdaptiveSessionTabs.OverflowWidth(_layout.Before) : 0) + _motion.Widths(Selected).Take(visible + 1).Sum() - 2,
                    Clock.GetTimestampMilliseconds() + 5000);
        }
        await OnClose.InvokeAsync(tab.Key);
    }
    private Task Before(TerminalPointerEventArgs args) { args.Handled = true; return _layout.Start > 0 ? OnSelect.InvokeAsync(Items[_layout.Start - 1].Key) : Task.CompletedTask; }
    private Task After(TerminalPointerEventArgs args) { args.Handled = true; return _layout.After > 0 ? OnSelect.InvokeAsync(Items[_layout.Start + _layout.Tabs.Count].Key) : Task.CompletedTask; }
    private Task Add(TerminalPointerEventArgs args) { args.Handled = true; return OnAdd.InvokeAsync(); }
    private Task AddDown(TerminalPointerEventArgs args)
    { if (args.Button != TerminalPointerButton.Right) return Task.CompletedTask; args.Handled = true; return OnContextMenu.InvokeAsync(new(null, args.ScreenX, args.ScreenY)); }
    private void Wheel(TerminalPointerEventArgs args)
    {
        if (!Vertical) return;
        args.Handled = true;
        _verticalStart = Math.Clamp(_verticalStart + Math.Sign(args.DeltaY), 0, Math.Max(0, Items.Count - Math.Max(1, (TerminalHeight - 2) / 3)));
    }

    private async Task Animate()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16), Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    var now = Clock.GetTimestampMilliseconds();
                    var delta = now - _tick;
                    _tick = now;
                    _motion.Advance(now);
                    foreach (var pulse in _pulses.Values) pulse.Advance(delta);
                    foreach (var title in _titles.Values) title.Advance(now);
                    foreach (var indicator in _indicators.Values) indicator.Advance(now);
                    foreach (var separator in _separators.Values) separator.Advance(delta);
                    _bottomSeparator?.Advance(delta);
                    var held = _hold is not null;
                    if (held) Reflow();
                    if (Animations || held) StateHasChanged();
                });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _pulses.Clear(); _titles.Clear(); _indicators.Clear(); _separators.Clear();
        _bottomSeparator = null;
        _text.Dispose(); _lifetime.Dispose();
    }
}
