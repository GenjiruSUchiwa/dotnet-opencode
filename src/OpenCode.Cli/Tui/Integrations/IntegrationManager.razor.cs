namespace OpenCode.Cli.Tui.Integrations;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Cli.Tui.Forms;
using OpenTui.Blazor;
using OpenTui.Blazor.Forms;

/// <summary>Client-backed integration/account dialogs. Mount with a key containing Client and Location.</summary>
public partial class IntegrationManager : ComponentBase, IAsyncDisposable
{
    [Inject] public TimeProvider Clock { get; set; } = TimeProvider.System;
    [Parameter, EditorRequired] public SessionHttpClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public LocationRef Location { get; set; } = null!;
    [Parameter, EditorRequired] public DialogTheme Theme { get; set; } = null!;
    [Parameter, EditorRequired] public TerminalFormTheme FormTheme { get; set; } = null!;
    [Parameter] public int TerminalWidth { get; set; } = 80;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public IntegrationId? InitialIntegration { get; set; }
    [Parameter] public bool AutoConnect { get; set; }
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public string RenameShortcut { get; set; } = "f2";
    [Parameter] public string DeleteShortcut { get; set; } = "del";
    [Parameter] public Func<string, CancellationToken, Task>? OpenExternal { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? CopyExternal { get; set; }
    [Parameter] public Func<CancellationToken, Task<string?>>? ReadClipboard { get; set; }
    [Parameter] public EventCallback<IntegrationId> OnConnected { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private enum View { List, Accounts, Methods, Key, Rename, Starting, OAuth, Code }
    private View _phase;
    private SessionHttpClient _client = null!;
    private LocationRef _location = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reload = new(1);
    private IReadOnlyList<IntegrationInfo> _integrations = [];
    private string? _integration;
    private string? _focused;
    private string? _deleting;
    private CredentialId? _rename;
    private IntegrationMethod? _method;
    private FormAnswer? _answer;
    private PendingForm? _form;
    private IntegrationAttempt? _attempt;
    private Task? _poll;
    private bool _loading;
    private bool _loaded;
    private bool _busy;
    private bool _closing;
    private bool _disposed;
    private string? _error;
    private string? _notice;
    private IntegrationInfo? Current => _integrations.FirstOrDefault(item => item.Id.Value == _integration);
    private string? Workspace => _location.WorkspaceId?.Value;

    protected override async Task OnInitializedAsync()
    {
        _client = Client;
        _location = Location;
        await LoadAsync();
        if (!_disposed && AutoConnect && InitialIntegration is { } id && _integrations.Any(item => item.Id == id))
            await IntegrationSelected(id.Value);
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_client, Client) || _location != Location)
            throw new InvalidOperationException("Remount IntegrationManager when its Client or Location changes.");
    }

    /// <summary>Root's existing event feed can call this for credential/integration changes. No second SSE connection is opened.</summary>
    public Task RefreshAsync() => InvokeAsync(LoadAsync);

    private async Task LoadAsync()
    {
        if (_disposed || _closing) return;
        var ct = _lifetime.Token;
        await _reload.WaitAsync(ct);
        _loading = true;
        try
        {
            var response = await _client.ListIntegrationsAsync(_location.Directory, Workspace, ct);
            ct.ThrowIfCancellationRequested();
            _integrations = response.Data;
            _loaded = true;
            _error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _error = error.Message; }
        finally
        {
            _loading = false;
            _reload.Release();
            if (!_disposed) StateHasChanged();
        }
    }

    private IReadOnlyList<DialogSelectOption<string>> IntegrationOptions => IntegrationPresentation.Ordered(_integrations)
        .Where(item => InitialIntegration is null || item.Id == InitialIntegration).Select(item =>
            new DialogSelectOption<string>(item.Id.Value, item.Name,
                IntegrationPresentation.Methods(item).Length == 0 ? "No supported connection method" : null,
                IntegrationPresentation.IsMcp(item) ? "MCP" : IntegrationPresentation.Priority(item.Id.Value) < 99 ? "Popular" : "Services",
                Footer: IntegrationPresentation.Summary(item),
                Disabled: IntegrationPresentation.Methods(item).Length == 0 && IntegrationPresentation.Credentials(item).Length == 0)).ToArray();

    private Task IntegrationSelected(string id)
    {
        _integration = id;
        _error = null;
        _deleting = null;
        if (Current is not { } current) return Task.CompletedTask;
        var saved = IntegrationPresentation.Credentials(current);
        if (saved.Length > 0) { _phase = View.Accounts; _focused = saved[0].Id.Value; return Task.CompletedTask; }
        return SelectMethodAsync(current);
    }

    private Task SelectMethodAsync(IntegrationInfo integration)
    {
        var methods = IntegrationPresentation.Methods(integration);
        _phase = View.Methods;
        if (methods.Length != 1) return Task.CompletedTask;
        return BeginMethodAsync(methods[0]);
    }

    private IReadOnlyList<DialogSelectOption<string>> MethodOptions(IntegrationInfo integration) => IntegrationPresentation.Methods(integration)
        .Select(method => method switch
        {
            IntegrationKeyMethod key => new DialogSelectOption<string>("key", key.Label ?? "API key"),
            IntegrationOAuthMethod oauth => new DialogSelectOption<string>(oauth.Id, oauth.Label),
            _ => throw new InvalidOperationException("Unsupported authentication method.")
        }).ToArray();

    private Task MethodSelected(string id)
    {
        var method = Current?.Methods.FirstOrDefault(method => id == "key" && method is IntegrationKeyMethod || method is IntegrationOAuthMethod oauth && oauth.Id == id)
            ?? throw new InvalidOperationException("The selected method is no longer available.");
        return BeginMethodAsync(method);
    }

    private Task BeginMethodAsync(IntegrationMethod method)
    {
        _method = method;
        _answer = null;
        var fields = method switch { IntegrationKeyMethod key => key.Form, IntegrationOAuthMethod oauth => oauth.Form, _ => null };
        if (fields is { Count: > 0 })
        {
            // Local method form, not a fabricated server pending form: only the final auth request
            // is sent over HTTP. Reuse the real FormComposer and its field/default validation.
            _form = new PendingForm(new FormInfo(FormId.Create(), "global", $"Connect {Current!.Name}", fields), _location);
            return Task.CompletedTask;
        }
        return OpenMethodAsync();
    }

    private async Task MethodFormReply(FormReplyRequest reply, CancellationToken ct)
    {
        _answer = reply.Reply.Answer;
        _form = null;
        await OpenMethodAsync();
        await InvokeAsync(StateHasChanged);
    }
    private Task MethodFormCancel(FormCancelRequest request, CancellationToken ct) { _form = null; return CloseAsync(); }

    private async Task OpenMethodAsync()
    {
        if (_method is IntegrationKeyMethod) { _phase = View.Key; return; }
        if (_method is not IntegrationOAuthMethod oauth || Current is not { } integration) return;
        _phase = View.Starting;
        _error = _notice = null;
        StateHasChanged();
        var ct = _lifetime.Token;
        try
        {
            var result = await _client.ConnectIntegrationOAuthAsync(integration.Id, new(IntegrationMethodId.FromExisting(oauth.Id), _answer), _location.Directory, Workspace, ct);
            if (_disposed || _closing || ct.IsCancellationRequested)
            {
                await CancelServerAttemptAsync(integration.Id, result.Data);
                return;
            }
            _attempt = result.Data;
            _phase = result.Data.Mode == "code" ? View.Code : View.OAuth;
            if (_phase == View.OAuth) StartPolling();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) { _error = error.Message; _phase = View.Methods; } }
        if (!_disposed) StateHasChanged();
    }

    private IReadOnlyList<DialogSelectOption<string>> AccountOptions(IntegrationInfo integration)
    {
        var saved = IntegrationPresentation.Credentials(integration).OrderBy(item => item.Label, StringComparer.CurrentCulture).ThenBy(item => item.Id.Value, StringComparer.Ordinal);
        return (IntegrationPresentation.Methods(integration).Length == 0 ? Enumerable.Empty<DialogSelectOption<string>>() : [new("add", "Add account")])
            .Concat(saved.Select(item => new DialogSelectOption<string>(item.Id.Value,
                _deleting == item.Id.Value ? $"Press {DeleteShortcut} again to confirm" : item.Label, Category: "Connected accounts"))).ToArray();
    }

    private async Task AccountSelected(string value)
    {
        if (Current is not { } current) return;
        if (value == "add") { await SelectMethodAsync(current); return; }
        var saved = IntegrationPresentation.Credentials(current);
        if (saved.FirstOrDefault()?.Id.Value == value) return;
        var credential = saved.FirstOrDefault(item => item.Id.Value == value) ?? throw new InvalidOperationException("The account is no longer available.");
        await MutateAsync(() => _client.ActivateCredentialAsync(credential.Id, _location.Directory, Workspace, _lifetime.Token));
    }

    private IReadOnlyList<DialogSelectAction<string>> RefreshActions => [new("integration.refresh", "refresh", "ctrl+r", _ => RefreshAsync(), RequiresSelection: false)];
    private IReadOnlyList<DialogSelectAction<string>> AccountActions =>
    [
        new("dialog.integration.rename", "rename", RenameShortcut, option => RenameAsync(option?.Value), Disabled: _focused is null or "add"),
        new("dialog.integration.delete", "delete", DeleteShortcut, option => DeleteAsync(option?.Value), Disabled: _focused is null or "add"),
        .. RefreshActions
    ];

    private void Moved(string value) { _focused = value; _deleting = null; StateHasChanged(); }
    private void AccountFiltered(string query)
    {
        _focused = Current is { } current ? IntegrationPresentation.FirstFiltered(AccountOptions(current), query) : null;
        _deleting = null;
    }
    private string? ResolveKey(ConsoleKeyInfo key) => ResolveCommand?.Invoke(key) ?? (key.Key switch
    {
        ConsoleKey.F2 => "dialog.integration.rename",
        ConsoleKey.Delete => "dialog.integration.delete",
        ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Control) => "integration.refresh",
        _ => null
    });

    private Task RenameAsync(string? id)
    {
        if (id is null or "add" || Current is not { } current || !IntegrationPresentation.Credentials(current).Any(item => item.Id.Value == id)) return Task.CompletedTask;
        _rename = CredentialId.FromExisting(id);
        _phase = View.Rename;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task DeleteAsync(string? id)
    {
        if (id is null or "add" || Current is not { } current || !IntegrationPresentation.Credentials(current).Any(item => item.Id.Value == id)) return;
        if (_deleting != id) { _deleting = id; StateHasChanged(); return; }
        await MutateAsync(() => _client.RemoveCredentialAsync(CredentialId.FromExisting(id), _location.Directory, Workspace, _lifetime.Token));
        _deleting = null;
        if (Current is { } refreshed && IntegrationPresentation.Credentials(refreshed).Length == 0) _phase = View.Methods;
        StateHasChanged();
    }

    private string PromptTitle => _phase == View.Rename ? "Rename account" : _phase == View.Code ? "Authorization code" : Current?.Name ?? "API key";
    private string PromptPlaceholder => _phase == View.Rename ? "Account name" : _phase == View.Code ? "Authorization code" : "API key";
    private string PromptInitial => _phase == View.Rename ? Current?.Connections.OfType<ConnectionCredentialInfo>().FirstOrDefault(item => item.Id == _rename)?.Label ?? "" : "";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "These validation messages are displayed directly as prompts; preserve their source-compatible wording.")]
    private async Task SubmitTextAsync(string text, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        if (_phase == View.Rename)
        {
            var label = text.Trim();
            if (label.Length == 0) throw new ArgumentException("Enter an account name.");
            await _client.UpdateCredentialAsync(_rename!.Value, label, _location.Directory, Workspace, linked.Token);
            _phase = View.Accounts;
            await LoadAsync();
            await OnChanged.InvokeAsync();
            return;
        }
        if (text.Length == 0) throw new ArgumentException(_phase == View.Key ? "Enter an API key." : "Enter the authorization code.");
        var integration = Current ?? throw new InvalidOperationException("The integration is no longer available.");
        if (_phase == View.Key)
            await _client.ConnectIntegrationKeyAsync(integration.Id, new(text, _answer), _location.Directory, Workspace, linked.Token);
        else if (_attempt is { } attempt)
            await _client.CompleteIntegrationOAuthAsync(integration.Id, attempt.AttemptId, text, _location.Directory, Workspace, linked.Token);
        else throw new InvalidOperationException("No authorization attempt is pending.");
        await ConnectedAsync(integration.Id);
    }

    private async Task MutateAsync(Func<Task> mutate)
    {
        if (_busy || _closing) return;
        _busy = true;
        _error = null;
        try { await mutate(); await LoadAsync(); await OnChanged.InvokeAsync(); }
        catch (Exception error) { if (!_disposed) _error = error.Message; }
        finally { _busy = false; if (!_disposed) StateHasChanged(); }
    }

    private void StartPolling() { if (_poll is null || _poll.IsCompleted) _poll = PollAsync(); }
    private async Task PollAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            while (!_disposed && !_closing && _attempt is { } attempt && _integration is { } id)
            {
                var result = await _client.IntegrationOAuthStatusAsync(IntegrationId.FromExisting(id), attempt.AttemptId, _location.Directory, Workspace, ct);
                ct.ThrowIfCancellationRequested();
                if (result.Data is IntegrationPendingAttemptStatus) { await Task.Delay(TimeSpan.FromMilliseconds(500), Clock, ct); continue; }
                if (result.Data is IntegrationCompleteAttemptStatus)
                { await InvokeAsync(() => ConnectedAsync(IntegrationId.FromExisting(id))); return; }
                _attempt = null;
                _error = result.Data is IntegrationFailedAttemptStatus failed ? failed.Message : "Authorization expired. Start a new connection.";
                _phase = View.Methods;
                await InvokeAsync(StateHasChanged);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_disposed && !_closing) await InvokeAsync(() => { _error = error.Message; StateHasChanged(); }); }
    }

    private async Task ConnectedAsync(IntegrationId id)
    {
        _attempt = null;
        await LoadAsync();
        await OnChanged.InvokeAsync();
        await OnConnected.InvokeAsync(id);
        await OnClose.InvokeAsync();
    }

    private async Task OAuthKey(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        if (args.Key.Key == ConsoleKey.O) await ExternalAsync(false);
        if (args.Key.Key == ConsoleKey.C) await ExternalAsync(true);
        if (args.Key.Key == ConsoleKey.R) { _error = null; StartPolling(); }
    }
    private Task OpenClick(TerminalPointerEventArgs args) { args.Handled = true; return ExternalAsync(false); }
    private Task CloseClick(TerminalPointerEventArgs args) { args.Handled = true; return CloseAsync(); }

    private async Task ExternalAsync(bool copy)
    {
        if (_attempt is not { } attempt) return;
        var callback = copy ? CopyExternal : OpenExternal;
        if (callback is null) { _error = copy ? "Clipboard access is not connected." : "Browser opening is not connected. Copy the URL manually."; return; }
        try
        {
            var code = Regex.Match(attempt.Instructions, "[A-Z0-9]{4}-[A-Z0-9]{4,5}", RegexOptions.NonBacktracking);
            await callback(copy && code.Success ? code.Value : attempt.Url, _lifetime.Token);
            _notice = copy ? "Copied to clipboard" : "Continue authorization in your browser.";
        }
        catch (Exception error) { _error = error.Message; }
    }

    public async Task CloseAsync()
    {
        if (_closing) return;
        _closing = true;
        await _lifetime.CancelAsync();
        try { if (_attempt is { } attempt && _integration is { } id) await CancelServerAttemptAsync(IntegrationId.FromExisting(id), attempt); }
        finally { _attempt = null; await OnClose.InvokeAsync(); }
    }

    private async Task CancelServerAttemptAsync(IntegrationId id, IntegrationAttempt attempt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), Clock);
        await _client.CancelIntegrationOAuthAsync(id, attempt.AttemptId, _location.Directory, Workspace, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync();
        try { if (_attempt is { } attempt && _integration is { } id) await CancelServerAttemptAsync(IntegrationId.FromExisting(id), attempt); }
        catch (Exception) { /* The server's owned attempt deadline remains the backstop if disconnection prevents cancellation. */ }
        _attempt = null;
        _answer = null;
        _form = null;
        // The poll observes cancellation itself. Do not await it here: it can be invoking a root
        // OnConnected callback that unmounts this component on the renderer's dispatcher.
    }
}
