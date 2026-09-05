namespace OpenCode.Cli.Tui.SystemDialogs;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Cli.Tui.Theme;

/// <summary>Source theme preview lifecycle over borrowed application state and its existing persistence subscription.</summary>
public partial class DialogThemeList : ComponentBase, IAsyncDisposable
{
    [Parameter, EditorRequired] public ThemeCatalog Catalog { get; set; } = null!;
    [Parameter, EditorRequired] public ThemeState State { get; set; } = null!;
    [Parameter, EditorRequired] public string Backdrop { get; set; } = null!;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    /// <summary>Optional root-owned ThemeSettingsPersistence.FlushAsync (or existing shared writer queue). Not another writer.</summary>
    [Parameter] public Func<Task>? FlushPersistence { get; set; }
    [Parameter] public string? PersistenceError { get; set; }
    [Parameter] public Action<Exception>? ReportError { get; set; }
    [Parameter] public EventCallback<string> OnConfirmed { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private ThemeCatalog _catalog = null!;
    private ThemeState _state = null!;
    private string _initial = "";
    private IReadOnlyList<DialogSelectOption<string>> _options = [];
    private string? _catalogError;
    private string? _error;
    private bool _confirmed;
    private bool _restored;
    private bool _closing;
    private bool _disposed;
    private ThemeTokens View => _state.View(ThemeContext.Elevated);
    private DialogTheme Colors => new(View.Text.Hex, View.Subdued.Hex, View.Background.Hex, Backdrop,
        View.Text.Hex, View.ActionBackground(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        View.ActionText(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex, View.FormfieldText(ThemeActionState.Selected).Hex,
        View.FormfieldBackground(ThemeActionState.Focused).Hex, View.FormfieldText(ThemeActionState.Focused).Hex);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Catalog and State are actual Blazor component parameters.")]
    protected override void OnInitialized()
    {
        ArgumentNullException.ThrowIfNull(Catalog);
        ArgumentNullException.ThrowIfNull(State);
        _catalog = Catalog;
        _state = State;
        _initial = State.Settings.Name;
        LoadCatalog();
        _catalog.Changed += CatalogChanged;
        _state.Changed += StateChanged;
        _state.Error += StateError;
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_state, State) || !ReferenceEquals(_catalog, Catalog))
            throw new InvalidOperationException("Remount the theme dialog to replace its application-owned ThemeState or ThemeCatalog.");
    }

    private void LoadCatalog()
    {
        try
        {
            // Real built-in/plugin/custom/system precedence and base-sensitivity sorting live in
            // ThemeCatalog. Do not replace this with a hardcoded 33-name list or assumed modes.
            _options = _catalog.List().Select(item => new DialogSelectOption<string>(item.Name, item.Name,
                Footer: string.Join(" / ", item.Modes.Select(mode => mode.ToString().ToLowerInvariant())))).ToArray();
            _catalogError = null;
        }
        catch (Exception error) { _options = []; _catalogError = error.Message; ReportError?.Invoke(error); }
    }

    private void Preview(string name)
    {
        if (_disposed || _closing) return;
        try
        {
            if (_state.Settings.Name != name && !_state.Set(name)) throw new InvalidOperationException("The selected theme is no longer in the catalog.");
            _error = null;
        }
        catch (Exception error) { _error = error.Message; ReportError?.Invoke(error); }
        StateHasChanged();
    }

    private Task FilterAsync(string query)
    {
        if (query.Length == 0) { Preview(_initial); return Task.CompletedTask; }
        // DialogSelect exposes callbacks rather than its private filtered array. Use its real
        // scorer and stable order, with the same title/category/search fields and zero threshold.
        var first = _options.Select((option, index) => (Option: option, Index: index,
                Score: DialogSearch.Score(query, option.Title, option.Category, option.SearchText)))
            .Where(item => item.Score > 0).OrderByDescending(item => item.Score).ThenBy(item => item.Index).FirstOrDefault().Option;
        if (first is not null) Preview(first.Value);
        return Task.CompletedTask;
    }

    private async Task ConfirmAsync(string name)
    {
        if (_disposed || _closing) return;
        _closing = true;
        try
        {
            if (!_state.Set(name)) throw new InvalidOperationException("The selected theme is no longer in the catalog.");
            _confirmed = true;
            if (FlushPersistence is not null) await FlushPersistence();
            if (_disposed) return;
            await OnConfirmed.InvokeAsync(name);
            await OnClose.InvokeAsync();
        }
        catch (Exception error)
        {
            _error = error.Message;
            ReportError?.Invoke(error);
            // Source confirmation is the successful local Set; an asynchronous save failure is
            // reported by the shared writer, not converted into an unconfirmed preview rollback.
            if (_confirmed && !_disposed) await OnClose.InvokeAsync();
        }
        finally { _closing = false; }
    }

    private void RestoreInitial()
    {
        if (_confirmed || _restored && _state.Settings.Name == _initial) return;
        // Restore only the source's captured selected NAME. Keep the current mode preference and
        // terminal mode; Set follows the existing resolve + PersistenceRequested path for both.
        if (!_state.Set(_initial)) throw new InvalidOperationException("The initial theme was removed; it could not be restored.");
        _restored = true;
    }

    public async Task CancelAsync()
    {
        if (_disposed || _closing) return;
        _closing = true;
        try
        {
            RestoreInitial();
            if (FlushPersistence is not null) await FlushPersistence();
            await OnClose.InvokeAsync();
        }
        catch (Exception error) { _error = error.Message; ReportError?.Invoke(error); }
        finally { _closing = false; }
    }

    private void CatalogChanged() { if (_disposed) return; LoadCatalog(); StateHasChanged(); }
    private void StateChanged() { if (!_disposed) StateHasChanged(); }
    private void StateError(Exception error) { if (_disposed) return; _error = error.Message; StateHasChanged(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _catalog.Changed -= CatalogChanged;
        _state.Changed -= StateChanged;
        _state.Error -= StateError;
        try
        {
            RestoreInitial();
            if (FlushPersistence is not null) await FlushPersistence();
        }
        catch (Exception error) { ReportError?.Invoke(error); }
        // The root, not this dialog, owns the state/catalog/persistence subscription.
    }
}
