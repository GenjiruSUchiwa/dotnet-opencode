namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Models;
using OpenCode.Cli.Tui.Keymap;
using OpenCode.Schema;
using OpenTui.Blazor.Keymap;

public partial class OpenCodeApp
{
    [Parameter] public ModelPreferenceService? ModelPreferenceState { get; set; }
    [Parameter] public string? ModelPreferenceLoadError { get; set; }
    private ModelSelectionController? _modelController;
    private string? _modelPreferenceInfo;
    private string? _modelPreferenceError;
    private string? ModelDialogError => _catalogError ?? _catalog?.IntegrationError ?? _modelPreferenceError
        ?? (ModelPreferenceState?.Loaded == true ? null : ModelPreferenceLoadError);

    private void ConnectModelPreferences()
    {
        if (ModelPreferenceState is { } preferences) _modelController = new(preferences, ApplyModelSelection, ModelPreferenceCatalog.Unavailable);
    }

    private IDisposable RegisterModelActionDispatcher(Func<string, Task<bool>> dispatch)
    {
        var config = _resolvedBindings ?? throw new InvalidOperationException("The model keymap is not initialized.");
        var commands = new[] { ModelPreferenceCommands.Provider, ModelPreferenceCommands.Favorite }
            .Select(id => new TuiKeymapCommand(new KeymapCommand(id, _ =>
            {
                _keyTasks.Add(dispatch(id));
                return true;
            }))).ToArray();
        var layer = TuiKeymapLayer.Create(config, commands, mode: TuiKeymapLayer.ModalMode,
            condition: new() { When = _ => _models });
        // The selector owns the returned lease, including disposal on variant-stage entry.
        // Single strokes remain with ResolveDialogCommand; completed sequences are consumed here.
        return _keyLayers.Register(layer with { Bindings = layer.Bindings.Where(binding => binding.Sequence.Count > 1).ToArray() });
    }

    // The picker alone records acceptance. This callback neither closes it nor writes preferences.
    private Task AcceptExactModel(ModelRef model, CancellationToken token) => ApplyModelSelection(model, token);

    private async Task ChooseVariant(ModelRef model)
    {
        await RunConfigurationAction(async token =>
        {
            if (_modelController is null || _catalog is null) throw new InvalidOperationException("Model preferences are not connected.");
            await _modelController.SelectAsync(new(model with { Variant = ModelPreferences.NormalizeVariant(model.Variant) }, ModelPreferenceAction.VariantPicker),
                _catalog.Models, _catalog.Providers, token);
        }, throwErrors: true, cancellationToken: CancellationToken.None);
        CloseDialog();
    }

    private async Task CyclePreferredModel(ModelPreferenceAction action, int direction = 1)
    {
        _modelPreferenceInfo = _modelPreferenceError = null;
        try
        {
            await RunConfigurationAction(async token =>
            {
                if (_modelController is null || ModelPreferenceState is null || LoadCatalog is null)
                    throw new InvalidOperationException("Model preferences are not connected.");
                _catalog = await LoadCatalog(token);
                if (!ModelPreferenceState.Loaded) await ModelPreferenceState.LoadAsync(token);
                if (action == ModelPreferenceAction.FavoriteCycle && !ModelPreferenceState.Value.Favorite.Any(saved =>
                    _catalog.Models.Any(model => model.ProviderId.Value == saved.ProviderId && model.Id.Value == saved.Id)))
                { _modelPreferenceInfo = "Add a favorite model to use this shortcut."; return; }
                await _modelController.CycleAsync(CurrentModelSelection, _catalog.Models, _catalog.Providers, action, direction, token);
            }, throwErrors: true, cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _modelPreferenceError = exception.Message; }
        finally { _dirty = true; }
    }
}
