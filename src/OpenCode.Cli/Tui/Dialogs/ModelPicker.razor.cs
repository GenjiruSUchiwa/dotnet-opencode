namespace OpenCode.Cli.Tui.Dialogs;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Keymap;
using OpenCode.Cli.Tui.Models;
using OpenCode.Schema;
using OpenTui.Blazor.Components;
using OpenTui.Blazor.Keymap;

public partial class ModelPicker : IDisposable
{
    [Parameter] public IReadOnlyList<ModelInfo> Models { get; set; } = [];
    [Parameter] public IReadOnlyList<ProviderInfo> Providers { get; set; } = [];
    [Parameter] public ModelRef? Current { get; set; }
    [Parameter] public Func<ModelRef, string?>? PreferredVariant { get; set; }
    [Parameter] public ProviderId? Provider { get; set; }
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public ModalSize Size { get; set; }
    [Parameter] public bool Loading { get; set; }
    [Parameter] public string? Error { get; set; }
    [Parameter] public DialogTheme Theme { get; set; } = DialogTheme.Existing;
    [Parameter] public IReadOnlyList<DialogSelectAction<ModelRef>> Actions { get; set; } = [];
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public EventCallback<ModelRef> OnSelect { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public ModelPreferenceService Preferences { get; set; } = new();
    [Parameter] public IReadOnlyList<IntegrationInfo>? Integrations { get; set; }
    [Parameter] public TuiKeybindConfig? Keybindings { get; set; }
    [Parameter] public Func<string, string?>? Shortcut { get; set; }
    [Parameter] public Func<ProviderId?, CancellationToken, Task<ProviderId?>>? ConnectProvider { get; set; }
    [Parameter] public Func<ModelInfo, ProviderInfo?, string?>? UnavailableReason { get; set; }
    [Parameter] public Func<ModelRef, CancellationToken, Task>? AcceptSelection { get; set; }
    [Parameter] public EventCallback<AcceptedModelSelection> OnAccepted { get; set; }
    [Parameter] public Func<Func<string, Task<bool>>, IDisposable>? RegisterActionDispatcher { get; set; }
    private Func<Func<string, Task<bool>>, IDisposable>? _rootActionDispatcher;
    private Func<Func<string, Task<bool>>, IDisposable>? _registerModelActions;
    private ModelInfo? _variantModel;
    private readonly CancellationTokenSource _lifetime = new();
    private ModelPreferenceService? _subscribed;
    private TuiKeybindConfig? _defaults;
    private IReadOnlySet<string> _favoritePriority = new HashSet<string>(StringComparer.Ordinal);
    private string _query = "";
    private string? _preferenceError;
    private AcceptedModelSelection? _pendingPreference;
    private ModelInfo? _nextVariant;
    private ProviderId? _connectedProvider;
    private bool _disposed;
    private ModelRef? BaseCurrent => Current is null ? null : Current with { Variant = null };
    private ProviderId? Filter => _connectedProvider ?? Provider;
    private bool Connected => Integrations?.Any(integration => integration.Connections.Count > 0) == true;
    private string Title => Filter is { } id ? Providers.FirstOrDefault(provider => provider.Id == id)?.Name ?? "Select model" : "Select model";
    private string? DisplayError => _preferenceError ?? Error;

    protected override void OnParametersSet()
    {
        if (Equals(_rootActionDispatcher, RegisterActionDispatcher)) return;
        _rootActionDispatcher = RegisterActionDispatcher;
        _registerModelActions = RegisterActionDispatcher is not { } register ? null : dispatch => register(async command =>
        {
            if (_disposed || _variantModel is not null) return false;
            var handled = await dispatch(command);
            // External keymap calls are not Blazor events; refresh provider/filter state too.
            if (!_disposed) await InvokeAsync(StateHasChanged);
            return handled;
        });
    }

    protected override async Task OnInitializedAsync()
    {
        _subscribed = Preferences;
        _subscribed.Changed += PreferencesChanged;
        try { if (!Preferences.Loaded) await Preferences.LoadAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _preferenceError = $"Model preferences could not be loaded: {error.Message}"; }
        _favoritePriority = Preferences.Value.Favorite.Select(ModelPreferences.Key).ToHashSet(StringComparer.Ordinal);
    }

    private void PreferencesChanged() => _ = InvokeAsync(() => { if (!_disposed) StateHasChanged(); });

    private IReadOnlyList<DialogSelectOption<ModelRef>> Options() => ModelPreferenceCatalog.Options(Models, Providers,
        Preferences.Value, _query, Connected, Filter, _favoritePriority, UnavailableReason);

    private void FilterChanged(string query) => _query = query;

    private IReadOnlyList<DialogSelectOption<ModelRef>> VariantOptions(ModelInfo model) => model.Variants.Select(variant =>
        new DialogSelectOption<ModelRef>(new(model.ProviderId.Value, model.Id.Value, ModelPreferences.NormalizeVariant(variant.Id.Value)), variant.Id.Value)).ToArray();
    private ModelRef? VariantCurrent(ModelInfo model) => Current is { } current && current.ProviderId == model.ProviderId.Value && current.Id == model.Id.Value
        ? current with { Variant = ModelPreferences.NormalizeVariant(current.Variant) } : new(model.ProviderId.Value, model.Id.Value);

    private async Task Choose(ModelRef selection)
    {
        if (_pendingPreference is not null) throw new InvalidOperationException("Retry the pending preference save or close the dialog before choosing another model.");
        ModelPreferenceCatalog.RequireSelection(selection, Models, Providers, UnavailableReason);
        var model = Models.First(item => item.ProviderId.Value == selection.ProviderId && item.Id.Value == selection.Id);
        var retained = ModelPreferences.RetainVariant(selection, Current, Preferences.Value, Models, PreferredVariant);
        _nextVariant = retained.Variant is null && model.Variants.Count > 0 ? model : null;
        // Source selects the base model and records its recent entry before opening the variant
        // stage. Cancelling that stage does not roll back an already accepted model choice.
        await Accept(new(retained, ModelPreferenceAction.Picker));
        await ContinueSelection();
    }

    private async Task ChooseVariant(ModelRef selection)
    {
        if (_pendingPreference is not null) throw new InvalidOperationException("Retry the pending preference save or close the dialog before choosing another variant.");
        _nextVariant = null;
        await Accept(new(selection with { Variant = ModelPreferences.NormalizeVariant(selection.Variant) }, ModelPreferenceAction.VariantPicker));
        await OnClose.InvokeAsync();
    }

    private async Task Accept(AcceptedModelSelection selection)
    {
        ModelPreferenceCatalog.RequireSelection(selection.Model, Models, Providers, UnavailableReason);
        if (AcceptSelection is not null) await AcceptSelection(selection.Model, _lifetime.Token);
        else if (OnSelect.HasDelegate) await OnSelect.InvokeAsync(selection.Model);
        else throw new InvalidOperationException("Model selection is not connected to the controller.");
        _pendingPreference = selection;
        await PersistAccepted();
    }

    private async Task PersistAccepted()
    {
        if (_pendingPreference is not { } accepted) return;
        try
        {
            // The actual choice was accepted. Closing the dialog must not cancel that preference write.
            await Preferences.RecordAcceptedAsync(accepted, CancellationToken.None);
            _pendingPreference = null; _preferenceError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
        {
            var failure = new ModelPreferencePersistenceException(accepted.Model, error);
            _preferenceError = failure.Message;
            throw failure;
        }
        await OnAccepted.InvokeAsync(accepted);
    }

    private async Task ContinueSelection()
    {
        if (_nextVariant is { } model) { _variantModel = model; return; }
        await OnClose.InvokeAsync();
    }

    private IReadOnlyList<DialogSelectAction<ModelRef>> PreferenceRetryActions => _pendingPreference is null ? [] :
        [new("model.preferences.retry", "Retry preference save", "click", async _ => { await PersistAccepted(); await ContinueSelection(); }, RequiresSelection: false)];

    private IReadOnlyList<DialogSelectAction<ModelRef>> EffectiveActions
    {
        get
        {
            var actions = new List<DialogSelectAction<ModelRef>>();
            var connect = Actions.FirstOrDefault(action => action.Command == ModelPreferenceCommands.Provider);
            if (connect is not null) actions.Add(connect);
            else if (ConnectProvider is not null) actions.Add(new(ModelPreferenceCommands.Provider,
                Connected ? "Connect an integration" : "View all integrations", Label(ModelPreferenceCommands.Provider), async _ =>
                {
                    var provider = await ConnectProvider(Filter, _lifetime.Token);
                    if (provider is not null)
                    {
                        _connectedProvider = provider;
                        _query = "";
                        _favoritePriority = Preferences.Value.Favorite.Select(ModelPreferences.Key).ToHashSet(StringComparer.Ordinal);
                    }
                }, RequiresSelection: false));
            var favorite = Actions.FirstOrDefault(action => action.Command == ModelPreferenceCommands.Favorite);
            if (favorite is not null) actions.Add(favorite);
            else if (Connected) actions.Add(new(ModelPreferenceCommands.Favorite, "Favorite", Label(ModelPreferenceCommands.Favorite), async option =>
            {
                if (option is null) return;
                if (!Models.Any(model => model.ProviderId.Value == option.Value.ProviderId && model.Id.Value == option.Value.Id))
                    throw new InvalidOperationException("The model is no longer in the catalog.");
                await Preferences.ToggleFavoriteAsync(option.Value, _lifetime.Token);
            }));
            actions.AddRange(Actions.Where(action => action.Command is not (ModelPreferenceCommands.Provider or ModelPreferenceCommands.Favorite)));
            actions.AddRange(PreferenceRetryActions);
            return actions;
        }
    }

    private string Label(string command) => Shortcut?.Invoke(command) ??
        (Keybindings is null && ResolveCommand is not null ? "click" : string.Join(", ", (Keybindings ?? (_defaults ??= TuiKeybindConfig.Defaults())).Get(command)
            .Select(binding => string.Join(" ", binding.Sequence.Select(part => part.Stroke.ToString())))));

    private string? ResolveModelCommand(ConsoleKeyInfo key)
    {
        if (ResolveCommand?.Invoke(key) is { } command) return command;
        if (Keybindings is null && ResolveCommand is not null) return null;
        var name = key.Key switch
        {
            >= ConsoleKey.A and <= ConsoleKey.Z => ((char)('a' + key.Key - ConsoleKey.A)).ToString(),
            >= ConsoleKey.F1 and <= ConsoleKey.F24 => key.Key.ToString().ToLowerInvariant(),
            _ => null
        };
        if (name is null) return null;
        var stroke = new KeyStroke(name, key.Modifiers.HasFlag(ConsoleModifiers.Control), key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt));
        var config = Keybindings ?? (_defaults ??= TuiKeybindConfig.Defaults());
        return EffectiveActions.Select(action => action.Command).FirstOrDefault(id => config.Get(id).Any(binding => binding.Sequence.Count == 1 && binding.Sequence[0].Stroke == stroke));
    }

    private Task CloseModels() => OnClose.InvokeAsync();
    private Task CloseVariants() => OnClose.InvokeAsync();
    public void Dispose()
    {
        _disposed = true;
        if (_subscribed is not null) _subscribed.Changed -= PreferencesChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
