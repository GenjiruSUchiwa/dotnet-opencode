namespace OpenCode.Cli.Tui.Integrations;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Dialogs;
using OpenTui.Blazor;
using OpenTui.Blazor.Forms;

/// <summary>Real configured MCP status/toggle and integration sign-in UI. Mount per Client/Location identity.</summary>
public partial class McpManager : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public SessionHttpClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public LocationRef Location { get; set; } = null!;
    [Parameter, EditorRequired] public DialogTheme Theme { get; set; } = null!;
    [Parameter, EditorRequired] public TerminalFormTheme FormTheme { get; set; } = null!;
    [Parameter] public int TerminalWidth { get; set; } = 80;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public string? InitialServer { get; set; }
    [Parameter] public bool Details { get; set; }
    [Parameter] public string ToggleShortcut { get; set; } = "space";
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? OpenExternal { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? CopyExternal { get; set; }
    [Parameter] public Func<CancellationToken, Task<string?>>? ReadClipboard { get; set; }
    [Parameter] public EventCallback<IntegrationId> OnConnected { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private SessionHttpClient _client = null!;
    private LocationRef _location = null!;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reload = new(1);
    private IReadOnlyList<McpServer> _servers = [];
    private McpServer? _detail;
    private IntegrationId? _signIn;
    private string? _focused;
    private string? _busy;
    private string? _error;
    private bool _loading;
    private bool _loaded;
    private bool _disposed;
    private McpServer? Focused => _servers.FirstOrDefault(item => item.Name == _focused);
    private string? Workspace => _location.WorkspaceId?.Value;

    protected override async Task OnInitializedAsync()
    {
        _client = Client;
        _location = Location;
        _focused = InitialServer;
        await LoadAsync();
        if (Details && Focused?.Status is McpFailedStatus) _detail = Focused;
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_client, Client) || _location != Location)
            throw new InvalidOperationException("Remount McpManager when its Client or Location changes.");
    }

    /// <summary>Invoke from the root's shared mcp.status.changed handler; this component does not open another event stream.</summary>
    public Task RefreshAsync() => InvokeAsync(LoadAsync);

    private async Task LoadAsync()
    {
        if (_disposed) return;
        var ct = _lifetime.Token;
        await _reload.WaitAsync(ct);
        _loading = true;
        try
        {
            var result = await _client.ListMcpServersAsync(_location.Directory, Workspace, ct);
            ct.ThrowIfCancellationRequested();
            _servers = result.Data.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            _loaded = true;
            if (!_servers.Any(item => item.Name == _focused)) _focused = _servers.FirstOrDefault()?.Name;
            _error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _error = error.Message; }
        finally { _loading = false; _reload.Release(); if (!_disposed) StateHasChanged(); }
    }

    private IReadOnlyList<DialogSelectOption<string>> Options => _servers.Select(server => new DialogSelectOption<string>(server.Name, server.Name,
        Footer: _busy == server.Name ? "Connecting …" : server.Status switch
        {
            McpConnectedStatus => "Connected ✓", McpFailedStatus => "Failed !", McpNeedsAuthStatus => "Sign in required →",
            McpPendingStatus => "Connecting …", McpDisabledStatus => "Disabled ○", _ => "Unknown status"
        })).ToArray();

    private string ToggleTitle => Focused?.Status switch
    { McpConnectedStatus => "disconnect", McpFailedStatus => "retry", McpNeedsAuthStatus => "sign in", _ => "connect" };
    private IReadOnlyList<DialogSelectAction<string>> Actions =>
    [
        new("dialog.mcp.toggle", ToggleTitle, ToggleShortcut, option => ToggleAsync(option?.Value), Disabled: _busy is not null || Focused?.Status is McpPendingStatus),
        new("mcp.refresh", "refresh", "ctrl+r", _ => RefreshAsync(), RequiresSelection: false)
    ];
    private string? ResolveKey(ConsoleKeyInfo key) => ResolveCommand?.Invoke(key) ?? (key.Key switch
    {
        ConsoleKey.Spacebar => "dialog.mcp.toggle",
        ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Control) => "mcp.refresh",
        _ => null
    });
    private void Moved(string name) { _focused = name; StateHasChanged(); }
    private void Filtered(string query) => _focused = IntegrationPresentation.FirstFiltered(Options, query);

    private Task SelectAsync(string name)
    {
        var server = _servers.FirstOrDefault(item => item.Name == name);
        if (server?.Status is McpNeedsAuthStatus)
        {
            if (server.IntegrationId is { } id) _signIn = id;
            else _error = "Sign-in requires the server's MCP OAuth integration to be configured.";
        }
        if (server?.Status is McpFailedStatus) _detail = server;
        return Task.CompletedTask;
    }

    private async Task ToggleAsync(string? name)
    {
        if (_busy is not null || name is null || _disposed) return;
        var server = _servers.FirstOrDefault(item => item.Name == name);
        if (server is null || server.Status is McpPendingStatus) return;
        if (server.Status is McpNeedsAuthStatus) { await SelectAsync(name); StateHasChanged(); return; }
        _busy = name;
        _error = null;
        StateHasChanged();
        var ct = _lifetime.Token;
        try
        {
            if (server.Status is McpConnectedStatus) await _client.DisconnectMcpServerAsync(name, _location.Directory, Workspace, ct);
            else await _client.ConnectMcpServerAsync(name, _location.Directory, Workspace, ct);
            await LoadAsync();
            await OnChanged.InvokeAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _error = error.Message; }
        finally { _busy = null; if (!_disposed) StateHasChanged(); }
    }

    private Task BackAsync() { _detail = null; return Task.CompletedTask; }
    private async Task FinishSignIn() { _signIn = null; await LoadAsync(); }

    private async Task DetailKey(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        if (args.Key.Key != ConsoleKey.C || _detail?.Status is not McpFailedStatus failed) return;
        if (CopyExternal is null) { _error = "Clipboard access is not connected."; return; }
        try { await CopyExternal(failed.Error, _lifetime.Token); }
        catch (Exception error) { _error = error.Message; }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); }
}
