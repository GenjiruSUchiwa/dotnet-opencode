namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Cli.Tui.Integrations;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    [Parameter] public Func<CancellationToken, Task<SessionHttpClient>>? RequireSessionClient { get; set; }
    [Parameter] public Func<SessionHttpClient?>? ReadSessionClient { get; set; }
    [Parameter] public Func<LocationRef, ManagementRevision>? ReadManagementRevision { get; set; }
    private bool _integrations;
    private bool _mcps;
    private SessionHttpClient? _managementClient;
    private LocationRef? _managementLocation;
    private IntegrationManager? _integrationManager;
    private McpManager? _mcpManager;
    private ProviderId? _modelProvider;
    private ManagementRevision? _managementRevision;
    private bool _managementRefreshing;
    private bool _managementRefreshAgain;

    private void ReadManagementContext()
    {
        if (!_integrations && !_mcps) return;
        if (ReadSessionClient?.Invoke() != _managementClient || (_presentation?.Location ?? new LocationRef(CurrentDirectory)) != _managementLocation)
        {
            CloseDialog();
            _inputError = "The server or location changed. Reopen management for the current connection.";
            _dirty = true;
            return;
        }
        if (_managementLocation is not { } location || ReadManagementRevision is null) return;
        var revision = ReadManagementRevision(location);
        if (_managementRevision == revision) return;
        _managementRevision = revision;
        if (_managementRefreshing) { _managementRefreshAgain = true; return; }
        _keyTasks.Add(RefreshManagementScreens());
    }

    private async Task RefreshManagementScreens()
    {
        var client = _managementClient;
        var location = _managementLocation;
        _managementRefreshing = true;
        try
        {
            do
            {
                _managementRefreshAgain = false;
                if (_integrations && _integrationManager is { } integration) await integration.RefreshAsync();
                if (_mcps && _mcpManager is { } mcp) await mcp.RefreshAsync();
                if (!ManagementMatches(client, location)) return;
                await RefreshManagementCatalogs();
            } while (_managementRefreshAgain && ManagementMatches(client, location));
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested || !ManagementMatches(client, location)) { }
        catch (Exception exception)
        {
            if (ManagementMatches(client, location)) { _inputError = SessionClientAdapter.Describe(exception); _dirty = true; }
        }
        finally { _managementRefreshing = false; }
    }

    private bool ManagementMatches(SessionHttpClient? client, LocationRef? location) => (_integrations || _mcps)
        && _managementClient == client && _managementLocation == location;

    private IReadOnlyList<DialogSelectAction<ModelRef>> ModelActions => RequireSessionClient is null ? [] :
        [new("model.dialog.provider", _catalog?.Integrations?.Any(integration => integration.Connections.Count > 0) == true ? "Connect an integration" : "View all integrations", Shortcut("model.dialog.provider"),
            _ => OpenManagement(false), RequiresSelection: false)];

    private async Task OpenManagement(bool mcp)
    {
        if (RequireSessionClient is null) return;
        var location = _presentation?.Location ?? new LocationRef(CurrentDirectory);
        CloseDialog();
        try
        {
            _managementClient = await RequireSessionClient(_configurationLifetime.Token);
            _managementLocation = location;
            _managementRevision = ReadManagementRevision?.Invoke(location);
            _mcps = mcp;
            _integrations = !mcp;
            _dirty = true;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _inputError = SessionClientAdapter.Describe(exception); _dirty = true; }
    }

    private Task RefreshManagementCatalogs() => RefreshManagementCatalogs(false);

    private async Task RefreshManagementCatalogs(bool throwErrors)
    {
        if (LoadCatalog is null) return;
        try
        {
            _catalog = await LoadCatalog(_configurationLifetime.Token);
            if (ReloadConfiguration is not null)
            {
                var configuration = await ReloadConfiguration(_configurationLifetime.Token);
                if (configuration.SessionId == _sessionId) ApplyConfiguration(configuration);
            }
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _inputError = SessionClientAdapter.Describe(exception);
            if (throwErrors) throw;
        }
        _dirty = true;
    }

    private async Task IntegrationConnected(IntegrationId integration)
    {
        await RefreshManagementCatalogs(true);
        if (_catalog is null) return;
        var provider = _catalog.Providers.FirstOrDefault(provider => provider.IntegrationId == integration);
        CloseDialog();
        _modelProvider = provider?.Id;
        _models = true;
        _dirty = true;
        await InvokeAsync(StateHasChanged);
    }
}
