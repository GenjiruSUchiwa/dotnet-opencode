namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.Keymap;
using System.Text;

public partial class OpenCodeApp
{
    [Parameter] public Func<string, bool>? FocusComponent { get; set; }
    private readonly Dictionary<SessionId, (SessionHttpClient Client, PersistentPtyInfo Terminal)> _terminalSelections = [];
    private IReadOnlyList<PersistentPtyInfo> _terminalEntries = [];
    private SessionHttpClient? _terminalClient;
    private SessionId? _terminalListSession;
    private PersistentPtyDeploymentInfo? _terminalCapability;
    private bool _terminalListOpen;
    private bool _terminalLoading;
    private bool _terminalFocused;
    private bool _terminalAutoFocus;
    private bool _terminalResizing;
    private string? _terminalError;
    private Action? _focusTerminal;

    private PersistentPtyInfo? SelectedTerminal => _sessionId is { } id && _terminalSelections.TryGetValue(id, out var selected)
        && selected.Client == ReadSessionClient?.Invoke() ? selected.Terminal : null;
    private bool TerminalVisible => SelectedTerminal is not null && _frame.TerminalVisible;

    private void ReadTerminalSelection()
    {
        if (_terminalListOpen && _terminalListSession != _sessionId)
        {
            _terminalListOpen = false;
            _terminalListSession = null;
            _dirty = true;
        }
        var visible = SelectedTerminal is not null;
        if (_frame.TerminalVisible == visible) return;
        _frame.ShowTerminal(visible);
        _terminalFocused = false;
        _dirty = true;
    }

    private async Task OpenTerminalList(bool create = false, bool toggle = false)
    {
        if (_sessionId is not { } session || RequireSessionClient is null || _terminalLoading) return;
        if (toggle && TerminalVisible) { HideTerminal(); return; }
        CloseDialog();
        FocusSessionPane();
        _terminalListOpen = true;
        _terminalListSession = session;
        _terminalLoading = true;
        _terminalError = null;
        _terminalEntries = [];
        _terminalCapability = null;
        _dirty = true;
        try
        {
            var client = await RequireSessionClient(_configurationLifetime.Token);
            _terminalClient = client;
            var capability = (await client.PersistentPtyCapabilitiesAsync(_configurationLifetime.Token)).Data;
            if (_terminalListSession != session) return;
            _terminalCapability = capability;
            if (!capability.CanAttempt)
            {
                _terminalError = capability.Reason ?? "Persistent terminals are unavailable on the selected server.";
                return;
            }
            var entries = (await client.ListPersistentPtysAsync(session, _configurationLifetime.Token)).Data;
            if (entries.Any(terminal => terminal.SessionId != session)) throw new InvalidOperationException("Terminal list returned a different Session.");
            if (_terminalListSession != session) return;
            _terminalEntries = entries;
            if (_terminalSelections.TryGetValue(session, out var selected) && !entries.Any(terminal => terminal.Id == selected.Terminal.Id))
            {
                _terminalSelections.Remove(session);
                if (_sessionId == session) _frame.ShowTerminal(false);
            }
            if (!create && !toggle) return;
            var existing = toggle ? entries.LastOrDefault() : null;
            var terminal = existing ?? (await client.CreatePersistentPtyAsync(session,
                new PersistentPtyCreateInput([], "Terminal", new Dictionary<string, string>(),
                    Cwd: ReadSessionObservation?.Invoke(session)?.Session?.Location.Directory), _configurationLifetime.Token)).Data;
            if (terminal.SessionId != session) throw new InvalidOperationException("Terminal creation returned a different Session.");
            if (_sessionId == session && _terminalListOpen) SelectTerminal(client, terminal);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _terminalError = SessionClientAdapter.Describe(exception); }
        finally { _terminalLoading = false; _dirty = true; }
    }

    private async Task ChooseTerminal(PtyId? id)
    {
        if (_terminalLoading || _terminalClient is not { } client || _terminalListSession is not { } session || _terminalCapability?.CanAttempt != true) return;
        if (id is { } existing)
        {
            var terminal = _terminalEntries.FirstOrDefault(terminal => terminal.Id == existing);
            if (terminal is not null && _sessionId == session) SelectTerminal(client, terminal);
            return;
        }
        _terminalLoading = true;
        try
        {
            var terminal = (await client.CreatePersistentPtyAsync(session, new PersistentPtyCreateInput([], "Terminal", new Dictionary<string, string>(),
                Cwd: ReadSessionObservation?.Invoke(session)?.Session?.Location.Directory), _configurationLifetime.Token)).Data;
            if (terminal.SessionId != session) throw new InvalidOperationException("Terminal creation returned a different Session.");
            if (_sessionId == session && _terminalListOpen) SelectTerminal(client, terminal);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _terminalError = SessionClientAdapter.Describe(exception); }
        finally { _terminalLoading = false; _dirty = true; }
    }

    private void SelectTerminal(SessionHttpClient client, PersistentPtyInfo terminal)
    {
        _terminalSelections[terminal.SessionId] = (client, terminal);
        _frame.ShowTerminal(true);
        _terminalListOpen = false;
        _terminalAutoFocus = true;
        _dirty = true;
    }

    private async Task RemoveTerminal(PtyId id)
    {
        if (_terminalLoading || _terminalClient is not { } client || _terminalListSession is not { } session) return;
        _terminalLoading = true;
        try
        {
            await client.RemovePersistentPtyAsync(id, _configurationLifetime.Token);
            _terminalEntries = _terminalEntries.Where(terminal => terminal.Id != id).ToArray();
            if (_terminalSelections.TryGetValue(session, out var selected) && selected.Terminal.Id == id)
            {
                _terminalSelections.Remove(session);
                if (_sessionId == session) _frame.ShowTerminal(false);
            }
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _terminalError = SessionClientAdapter.Describe(exception); }
        finally { _terminalLoading = false; _dirty = true; }
    }

    private void HideTerminal()
    {
        if (_sessionId is { } id) _terminalSelections.Remove(id);
        _frame.ShowTerminal(false);
        FocusSessionPane();
        _dirty = true;
    }
    private void CloseTerminalList() { _terminalListOpen = false; _dirty = true; FocusSessionPane(); }
    private void FocusSessionPane()
    {
        _terminalFocused = false;
        FocusComponent?.Invoke(ActivePermission is not null ? "permission" : ActiveForm is not null ? "form" : "prompt");
        _dirty = true;
    }
    private void TerminalFocusChanged(bool focused) { _terminalFocused = focused; _dirty = true; }
    private void RememberTerminalFocus(Action? action) => _focusTerminal = action;
    private void ConsumeTerminalFocus() => _terminalAutoFocus = false;
    private bool DeferTerminalKey(TerminalKeyInput key)
    {
        if (_keyDispatcher?.Pending.Count > 0) return true;
        if (!key.TryGetKeymapEvent(out var input) || _resolvedBindings?.Leader is not { } leader) return false;
        if (input.Stroke == leader) return true;
        return input.BaseCode is >= 32 and not 127 && Rune.IsValid(input.BaseCode.Value)
            && new KeyStroke(new Rune(input.BaseCode.Value).ToString(), input.Stroke.Ctrl, input.Stroke.Shift,
                input.Stroke.Meta, input.Stroke.Super, input.Stroke.Hyper) == leader;
    }

    private void BeginTerminalResize(TerminalPointerEventArgs args)
    {
        if (args.Button != TerminalPointerButton.Left) return;
        args.Handled = true;
        _terminalResizing = true;
        _dirty = true;
    }
    private void ResizeTerminal(TerminalPointerEventArgs args)
    {
        if (!_terminalResizing) return;
        args.Handled = true;
        _frame.ResizeTerminal(_width - args.ScreenX - 1, _width);
        _dirty = true;
    }
    private void EndTerminalResize(TerminalPointerEventArgs args) { args.Handled = true; _terminalResizing = false; _dirty = true; }
}
