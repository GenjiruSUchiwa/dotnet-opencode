namespace OpenCode.Cli.Tui.Settings;

using System.Text.Json;
using OpenCode.Cli.Tui.Theme;

/// <summary>Connects the theme owner's real resolver/state to the shared CLI writer without introducing another theme configuration.</summary>
public sealed class ThemeSettingsPersistence : IAsyncDisposable
{
    private readonly ThemeState _state;
    private readonly CliSettingsController _settings;
    private readonly CliSettingsStore _store;
    private readonly Action<Exception> _report;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;

    public ThemeSettingsPersistence(CliSettingsController settings, CliSettingsStore store, ThemeCatalog catalog, ThemeState state, Action<Exception> reportError)
    {
        _state = state;
        _settings = settings;
        _store = store;
        _report = reportError;
        settings.Register(CliSettings.ThemeName, (_, config) => state.ApplyConfig(CliThemeSettings.FromConfig(config)),
            choices: () => catalog.All().Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(), fallback: () => state.Settings.Name,
            validate: (_, config) => Validate(config));
        settings.Register(CliSettings.ColorMode, (_, config) => state.ApplyConfig(CliThemeSettings.FromConfig(config)),
            validate: (_, config) => Validate(config));
        state.PersistenceRequested += Save;

        void Validate(JsonElement config)
        {
            var selection = CliThemeSettings.FromConfig(config);
            catalog.Resolve(selection.Name, selection.Mode switch
            {
                ThemeModePreference.Light => ThemeMode.Light,
                ThemeModePreference.Dark => ThemeMode.Dark,
                _ => state.Current.Mode
            });
        }
    }

    private void Save(CliThemeSettings selection)
    {
        if (_disposed) return;
        _pending = Persist(_pending, selection);
    }

    private async Task Persist(Task previous, CliThemeSettings selection)
    {
        await previous;
        try
        {
            var config = await _store.SetManyAsync([
                new("theme.name", JsonSerializer.SerializeToElement(selection.Name)),
                new("theme.mode", JsonSerializer.SerializeToElement(selection.Mode.ToString().ToLowerInvariant()))
            ]);
            _settings.AcceptSnapshot(config);
        }
        catch (Exception exception) { _report(new IOException("Theme changed locally, but saving shared CLI preferences failed.", exception)); }
    }

    public Task FlushAsync() => _pending;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _state.PersistenceRequested -= Save;
        await _pending;
    }
}
