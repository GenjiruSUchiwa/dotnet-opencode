namespace OpenCode.Cli.Tui.Settings;

using System.Text.Json;
using OpenCode.Cli.Tui.Dialogs;

/// <summary>UI-dispatcher-owned registrations. Unregistered settings never appear in screens or palette search.</summary>
public sealed class CliSettingsController(CliSettingsStore store)
{
    private readonly List<SettingRegistration> _settings = [];
    private static readonly string[] SourceOrder = ["theme.name", "theme.mode", "session.sidebar", "session.thinking", "session.markdown", "session.grouping", "diffs.view", "diffs.wrap", "scroll.speed", "mouse", "leader.timeout"];
    private IEnumerable<SettingRegistration> Available => _settings.Where(setting => setting.Available).OrderBy(setting => Array.IndexOf(SourceOrder, setting.Id));
    private JsonElement _config = JsoncSettingsEditor.Parse("{}");
    public bool Loaded { get; private set; }
    public bool Saving { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    public IReadOnlyList<PaletteSetting> PaletteSettings => Available
        .Select(setting => new PaletteSetting(setting.Id, setting.Title, setting.Category, setting.Keywords)).ToArray();
    public IReadOnlyList<DialogSelectOption<string>> Options => Available
        .Select(setting => new DialogSelectOption<string>(setting.Id, setting.Title, Category: setting.Category,
            Footer: setting.Display(_config), SearchText: setting.Keywords)).ToArray();

    public void Register<T>(CliSetting<T> setting, Action<T> apply, Func<bool>? available = null,
        Func<IReadOnlyList<T>>? choices = null, Func<T>? fallback = null, Action<T>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        Register(setting, (value, _) => apply(value), available, choices, fallback,
            validate is null ? null : (value, _) => validate(value));
    }

    public void Register<T>(CliSetting<T> setting, Action<T, JsonElement> apply, Func<bool>? available = null,
        Func<IReadOnlyList<T>>? choices = null, Func<T>? fallback = null, Action<T, JsonElement>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (_settings.Any(item => item.Id == setting.Id)) throw new InvalidOperationException($"Setting '{setting.Id}' is already registered.");
        _settings.Add(new SettingRegistration<T>(setting, apply, available, choices, fallback, validate));
        Changed?.Invoke();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var config = await store.ReadAsync(cancellationToken);
        var registered = _settings.Where(setting => setting.Available).ToArray();
        foreach (var setting in registered) setting.Validate(setting.Value(config), config);
        foreach (var setting in registered) setting.Apply(setting.Value(config), config);
        _config = config;
        Loaded = true;
        Error = null;
        Changed?.Invoke();
    }

    internal void AcceptSnapshot(JsonElement config)
    {
        _config = config;
        Loaded = true;
        Error = null;
        Changed?.Invoke();
    }

    public Task<bool> ChangeAsync(string id, int direction, CancellationToken cancellationToken = default)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        return Save(id, direction, false, cancellationToken);
    }

    /// <summary>Explicit reset API for registered consumers. The source settings screen does not add a reset button.</summary>
    public Task<bool> ResetAsync(string id, CancellationToken cancellationToken = default) => Save(id, 0, true, cancellationToken);

    private async Task<bool> Save(string id, int direction, bool reset, CancellationToken cancellationToken)
    {
        if (Saving) return false;
        var setting = _settings.FirstOrDefault(setting => setting.Id == id && setting.Available)
            ?? throw new InvalidOperationException($"Setting '{id}' is unavailable.");
        Saving = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            var config = await store.ReadAsync(cancellationToken);
            var next = reset ? setting.DefaultValue : setting.Next(config, direction);
            var proposed = JsoncSettingsEditor.Parse(JsoncSettingsEditor.Set(config.GetRawText(), id.Split('.'), reset ? null : next));
            setting.Validate(next, proposed);
            if (!reset && setting.Value(config).GetRawText() == next.GetRawText())
            {
                _config = config;
                Loaded = true;
                setting.Apply(next, config);
                return true;
            }
            _config = await store.SetAsync(id, reset ? null : next, cancellationToken);
            Loaded = true;
            // Durable preference first, then apply through the real runtime consumer.
            // A failed save never presents a successful setting change.
            setting.Apply(next, _config);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { Error = exception.Message; return false; }
        finally { Saving = false; Changed?.Invoke(); }
    }
}
