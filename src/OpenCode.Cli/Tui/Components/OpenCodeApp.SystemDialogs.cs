namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Cli.Tui.SystemDialogs;
using OpenCode.Cli.Tui.Theme;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    [Parameter] public ThemeCatalog? ApplicationThemeCatalog { get; set; }
    [Parameter] public Func<LocationRef, CancellationToken, Task<LocationResponse<IReadOnlyList<McpServer>>>>? LoadStatusMcp { get; set; }
    private bool _statusDialog;
    private bool _themesDialog;
    private bool _statusLoading;
    private IReadOnlyList<McpServer>? _statusMcp;
    private string? _statusMcpError;
    private DialogThemeList? _themeListDialog;
    private LocationRef? _statusLocation;
    private (SessionHttpClient? Client, LocationRef Location, ManagementRevision Revision)? _statusCatalogContext;
    private (SessionHttpClient? Client, LocationRef Location, ManagementRevision Revision)? _modelCatalogContext;
    private bool _modelCatalogRefreshing;

    private Task OpenStatus()
    {
        CloseDialog();
        _statusLocation = SelectionLocation;
        var presentation = _presentation is { } current && current.Location == _statusLocation ? current : null;
        _statusMcp = presentation?.Mcp;
        _statusMcpError = presentation?.McpError;
        _statusCatalogContext = null;
        _statusDialog = true;
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }

    private Task OpenThemes()
    {
        if (ApplicationThemeCatalog is null || Themes is null) return Task.CompletedTask;
        CloseDialog();
        _themesDialog = true;
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }

    private async Task FlushThemeWrites()
    {
        if (_themePersistence is { } persistence) await persistence.FlushAsync();
        await _themeUpdate;
    }

    private void ReadSystemCatalogs()
    {
        var location = SelectionLocation;
        var context = (ReadSessionClient?.Invoke(), location, ReadManagementRevision?.Invoke(location) ?? default);
        if (_statusDialog && _statusLocation != location) { CloseDialog(); return; }
        if (_statusDialog && !_statusLoading && _statusCatalogContext != context)
        {
            _statusCatalogContext = context;
            _keyTasks.Add(RefreshStatusMcp(location));
        }
        if (!_models) { _modelCatalogContext = null; return; }
        if (_catalogLoading || _configurationBusy || _modelCatalogRefreshing || _modelCatalogContext == context) return;
        _modelCatalogContext = context;
        _keyTasks.Add(RefreshModelDialogCatalog(location));
    }

    private async Task RefreshStatusMcp(LocationRef location)
    {
        _statusLoading = true;
        _dirty = true;
        try
        {
            if (LoadStatusMcp is null) throw new InvalidOperationException("MCP status loading is not connected.");
            var result = await LoadStatusMcp(location, _configurationLifetime.Token);
            if (!_statusDialog || _statusLocation != location) return;
            if (new LocationRef(result.Location.Directory, result.Location.WorkspaceId) != location)
                throw new InvalidOperationException("MCP status resolved a different Location.");
            _statusMcp = result.Data;
            _statusMcpError = null;
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (_statusLocation == location) _statusMcpError = SessionClientAdapter.Describe(exception); }
        finally { _statusLoading = false; _dirty = true; }
    }

    private async Task RefreshModelDialogCatalog(LocationRef location)
    {
        if (LoadCatalog is null) return;
        _modelCatalogRefreshing = true;
        try
        {
            var catalog = await LoadCatalog(_configurationLifetime.Token);
            if (!_models || location != SelectionLocation) return;
            _catalog = catalog;
            _catalogError = null;
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _catalogError = SessionClientAdapter.Describe(exception); }
        finally { _modelCatalogRefreshing = false; _dirty = true; }
    }
}
