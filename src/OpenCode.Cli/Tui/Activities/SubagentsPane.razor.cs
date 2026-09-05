namespace OpenCode.Cli.Tui.Activities;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Theme;
using OpenTui.Blazor;

public partial class SubagentsPane : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public SessionHttpClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public SessionId SessionId { get; set; }
    [Parameter] public SessionId? CurrentSession { get; set; }
    [Parameter, EditorRequired] public ThemeTokens Theme { get; set; } = null!;
    [Parameter] public IReadOnlyList<SessionInfo>? Sessions { get; set; }
    [Parameter] public IReadOnlyDictionary<string, SessionActive>? ActiveSessions { get; set; }
    [Parameter] public IReadOnlyDictionary<SessionId, IReadOnlyList<SessionMessage>>? Messages { get; set; }
    [Parameter] public IReadOnlyList<PermissionRequest>? Permissions { get; set; }
    [Parameter] public IReadOnlyList<FormInfo>? Forms { get; set; }
    [Parameter] public IReadOnlyList<ActivityJobProjection>? Jobs { get; set; }
    [Parameter] public bool FamilyComplete { get; set; }
    [Parameter] public bool Active { get; set; } = true;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public string? Error { get; set; }
    [Parameter] public string InterruptShortcut { get; set; } = "ctrl+d";
    [Parameter] public string ToggleShortcut { get; set; } = "ctrl+a";
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public Func<ActivityJobProjection, CancellationToken, Task>? CancelJob { get; set; }
    [Parameter] public EventCallback<string> OnFocusRequested { get; set; }
    [Parameter] public EventCallback<SessionId> OnOpenSession { get; set; }
    [Parameter] public EventCallback<SessionId> OnFocusSession { get; set; }
    [Parameter] public EventCallback<SessionId> OnRequestMessages { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    [Parameter] public EventCallback OnRefresh { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<int> OnSwitchTab { get; set; }
    private readonly TerminalScrollState _scroll = new() { AutoFollow = false };
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<FamilyEntry> _rows = [];
    private int _selected;
    private bool _activeOnly = true;
    private bool _wasActive;
    private bool _details;
    private bool _busy;
    private bool _disposed;
    private bool _focusPending;
    private SessionId? _route;
    private string? _error;
    private string? _familyError;
    private FamilyEntry? Selected => _rows.ElementAtOrDefault(_selected);
    private int ListHeight => Math.Max(1, Math.Min(Math.Min(5, TerminalHeight - 6), _rows.Count));
    private string? Unavailable => Error ?? _familyError ?? (Sessions is null ? "Session family has not been loaded." : ActiveSessions is null ? "Session activity has not been loaded." : null);

    protected override void OnParametersSet() => Project();

    private void Project()
    {
        var previous = Selected?.Session.Id;
        if (!Active && _wasActive) { _selected = 0; _activeOnly = true; _details = false; _route = null; }
        _familyError = null;
        try
        {
            var family = Sessions is null ? [] : ActivityProjection.Family(Sessions, SessionId);
            if (Sessions is not null && !Sessions.Any(session => session.Id == SessionId)) _familyError = "The current Session is not in the supplied family snapshot.";
            _rows = ActiveSessions is null ? [] : family.Where(row => ActivityProjection.Running(row.Session.Id, ActiveSessions) == _activeOnly).ToArray();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { _familyError = error.Message; _rows = []; }
        var route = CurrentSession ?? SessionId;
        if (Active && (!_wasActive || _route != route))
        { _selected = Math.Max(0, _rows.ToList().FindIndex(row => row.Session.Id == route)); _route = route; Reveal(true); _focusPending = true; }
        else if (previous is { } id && _rows.ToList().FindIndex(row => row.Session.Id == id) is >= 0 and var index) _selected = index;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        _wasActive = Active;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!Active || !_focusPending) return;
        _focusPending = false;
        await OnFocusRequested.InvokeAsync("activity-subagents");
    }

    private string RowBackground(bool focused, bool current) => Theme.ActionBackground(ThemeActionVariant.Primary,
        focused ? ThemeActionState.Focused : current ? ThemeActionState.Selected : ThemeActionState.Default).Hex;
    private string RowText(bool focused, bool current) => Theme.ActionText(ThemeActionVariant.Primary,
        focused ? ThemeActionState.Focused : current ? ThemeActionState.Selected : ThemeActionState.Default).Hex;
    private string Status(SessionInfo session) => string.Join(" · ", new[] { ActivityProjection.SessionState(session, ActiveSessions), ActivityProjection.Blocked(session.Id, Permissions, Forms) }.Where(value => value is not null));
    private string StatusColor(SessionInfo session) => ActivityProjection.Blocked(session.Id, Permissions, Forms) is not null ? Theme.Color("text.feedback.warning.default").Hex
        : !ActivityProjection.Running(session.Id, ActiveSessions) && session.Outcome == SessionOutcome.Failed ? Theme.Color("text.feedback.error.default").Hex : Theme.Subdued.Hex;
    private string Preview(SessionId session) => Messages?.TryGetValue(session, out var messages) == true
        ? ActivityProjection.MessagePreview(messages) : "Messages have not been loaded. Open the Session or request details from the host.";
    private void Reveal(bool center) { if (Selected is { } row) _scroll.Reveal(row.Session.Id, center); }

    private async Task Move(int index)
    {
        if (_rows.Count == 0) return;
        _selected = (index + _rows.Count) % _rows.Count;
        Reveal(true);
        if (Selected is { } row) await OnFocusSession.InvokeAsync(row.Session.Id);
    }
    private async Task Hover(int index, TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (_busy) return;
        _selected = index;
        if (Selected is { } row) await OnFocusSession.InvokeAsync(row.Session.Id);
    }
    private async Task OpenClick(int index, TerminalPointerEventArgs args) { args.Handled = true; await Move(index); await OpenAsync(); }
    private async Task OpenAsync()
    {
        if (Selected is not { } row) return;
        if (!OnOpenSession.HasDelegate) { _error = "Session navigation is not connected."; return; }
        await OnOpenSession.InvokeAsync(row.Session.Id);
    }
    private async Task Toggle()
    {
        _activeOnly = !_activeOnly; _selected = 0; _details = false;
        Project(); _scroll.ScrollToStart();
        if (Selected is { } row) await OnFocusSession.InvokeAsync(row.Session.Id);
    }
    private async Task InterruptAsync()
    {
        if (_busy || Selected is not { } row || !ActivityProjection.Running(row.Session.Id, ActiveSessions)) return;
        _busy = true; _error = null;
        try { await Client.InterruptAsync(row.Session.Id, ct: _lifetime.Token); await OnChanged.InvokeAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { _error = ActivityProjection.Error(error); }
        finally { _busy = false; }
    }
    private async Task DetailsAsync()
    {
        _details = !_details;
        if (_details && Selected is { } row && (Messages is null || !Messages.ContainsKey(row.Session.Id))) await OnRequestMessages.InvokeAsync(row.Session.Id);
    }
    private async Task CancelJobClick(TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (_busy || CancelJob is null || Selected is not { } row || ActivityProjection.SubagentJob(row.Session, Jobs) is not { State: ActivityJobState.Running } job) return;
        _busy = true;
        try { await CancelJob(job, _lifetime.Token); await OnChanged.InvokeAsync(); }
        catch (Exception error) { _error = ActivityProjection.Error(error); }
        finally { _busy = false; }
    }
    private async Task InterruptClick(TerminalPointerEventArgs args) { args.Handled = true; await InterruptAsync(); }
    private async Task ToggleClick(TerminalPointerEventArgs args) { args.Handled = true; await Toggle(); }

    private async Task Key(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        if (!Active) return;
        var control = args.Key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (args.Key.Key == ConsoleKey.Escape || control && args.Key.Key == ConsoleKey.C) { await OnClose.InvokeAsync(); return; }
        if (args.Key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow) { await OnSwitchTab.InvokeAsync(args.Key.Key == ConsoleKey.LeftArrow ? -1 : 1); return; }
        if (_busy) return;
        var command = ResolveCommand is null ? args.Key.Key switch
        {
            ConsoleKey.UpArrow => "composer.subagent.up", ConsoleKey.DownArrow => "composer.subagent.down", ConsoleKey.Enter => "composer.subagent.select",
            ConsoleKey.D when control => "composer.subagent.interrupt", ConsoleKey.A when control => "composer.subagent.toggle-activity", _ => null
        } : ResolveCommand(args.Key);
        switch (command)
        {
            case "composer.subagent.up": if (_selected == 0) await OnClose.InvokeAsync(); else await Move(_selected - 1); return;
            case "composer.subagent.down": await Move(_selected + 1); return;
            case "composer.subagent.select": await OpenAsync(); return;
            case "composer.subagent.interrupt": await InterruptAsync(); return;
            case "composer.subagent.toggle-activity": await Toggle(); return;
        }
        if (args.Key.Key == ConsoleKey.Spacebar) await DetailsAsync();
        if (args.Key.Key == ConsoleKey.R && control) await OnRefresh.InvokeAsync();
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _scroll.Detach(); }
}
