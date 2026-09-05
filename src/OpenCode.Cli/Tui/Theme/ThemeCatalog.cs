namespace OpenCode.Cli.Tui.Theme;

using System.Text.Json.Nodes;
using System.Globalization;

/// <summary>The source theme picker uses the exact registry name as both value and label.</summary>
public sealed record ThemeCatalogEntry(string Name, string Label, IReadOnlyList<ThemeMode> Modes);

/// <summary>Application-owned theme registry. Priority: built-in, plugin, custom, system.
/// The UI owner serializes calls on its renderer dispatcher.</summary>
public sealed class ThemeCatalog
{
    public static IReadOnlyList<string> BuiltinNames => BuiltinThemeData.Names;
    public static JsonObject BuiltinProvenance() => BuiltinThemeData.Provenance();
    private readonly Dictionary<string, JsonObject> _plugins = new(StringComparer.Ordinal);
    private Dictionary<string, JsonObject> _custom = new(StringComparer.Ordinal);
    private JsonObject? _system;
    public event Action? Changed;

    public IReadOnlyDictionary<string, JsonObject> All()
    {
        var result = BuiltinThemeData.All();
        foreach (var pair in _plugins) result[pair.Key] = pair.Value.DeepClone().AsObject();
        foreach (var pair in _custom) result[pair.Key] = pair.Value.DeepClone().AsObject();
        if (_system is not null) result["system"] = _system.DeepClone().AsObject();
        return result;
    }
    public bool Has(string name) => name.Length > 0 && All().ContainsKey(name);

    /// <summary>Source picker ordering and labels. Modes come from migration/resolution:
    /// equal opaque V1 backgrounds can yield a single mode despite light/dark variants.</summary>
    public IReadOnlyList<ThemeCatalogEntry> List()
    {
        var comparer = Comparer<string>.Create((left, right) => CultureInfo.CurrentCulture.CompareInfo.Compare(
            left, right, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace));
        return All().OrderBy(pair => pair.Key, comparer).Select(pair => new ThemeCatalogEntry(pair.Key, pair.Key,
            Array.AsReadOnly(ThemeResolver.Resolve(pair.Value).Modes.ToArray()))).ToArray();
    }

    public ThemeCatalogEntry Describe(string name) => new(name, name, Array.AsReadOnly(Resolve(name, ThemeMode.Light).Modes.ToArray()));
    public bool Add(string name, JsonObject document)
    {
        if (name.Length == 0 || Has(name) || !IsSource(document)) return false;
        _plugins[name] = document.DeepClone().AsObject(); Changed?.Invoke(); return true;
    }
    public bool Upsert(string name, JsonObject document)
    {
        if (name.Length == 0 || !IsSource(document)) return false;
        (_custom.ContainsKey(name) ? _custom : _plugins)[name] = document.DeepClone().AsObject(); Changed?.Invoke(); return true;
    }
    public void SetCustom(IReadOnlyDictionary<string, JsonObject> documents)
    {
        _custom = documents.Where(pair => IsSource(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value.DeepClone().AsObject(), StringComparer.Ordinal);
        Changed?.Invoke();
    }
    public void SetSystem(JsonObject? document) { _system = document?.DeepClone().AsObject(); Changed?.Invoke(); }
    public ResolvedTheme Resolve(string name, ThemeMode mode)
    {
        if (!All().TryGetValue(name, out var document)) throw new NotSupportedException($"Theme '{name}' is not installed in the native catalog. No fallback was substituted.");
        return ThemeResolver.Resolve(document, mode);
    }
    private static bool IsSource(JsonObject document) => document.ContainsKey("theme") || document.ContainsKey("version");
}

/// <summary>Renderer-independent theme context: mode lock, selection, semantic views and
/// explicit persistence handoff. Call UpdateTerminal with an already observed palette.</summary>
public sealed class ThemeState : IDisposable
{
    private readonly ThemeCatalog _catalog;
    private ThemeMode _terminalMode;
    private TerminalThemePalette? _palette;
    private bool _updating;
    public CliThemeSettings Settings { get; private set; }
    public ResolvedTheme Current { get; private set; }
    public bool Locked => Settings.Mode != ThemeModePreference.System;
    public bool Ready { get; private set; }
    public event Action? Changed;
    public event Action<Exception>? Error;
    public event Action<CliThemeSettings>? PersistenceRequested;

    public ThemeState(ThemeCatalog catalog, CliThemeSettings settings, ThemeMode terminalMode = ThemeMode.Dark)
    {
        _catalog = catalog; Settings = settings; _terminalMode = terminalMode;
        Current = catalog.Resolve(settings.Name, RequestedMode());
        _catalog.Changed += Reload;
    }
    public ThemeTokens View(ThemeContext context = ThemeContext.Base) => Current.ForContext(context);
    public void MarkReady() { Ready = true; Changed?.Invoke(); }
    public bool Set(string name)
    {
        if (!_catalog.Has(name)) return false;
        var resolved = _catalog.Resolve(name, RequestedMode());
        Settings = Settings with { Name = name }; Current = resolved;
        Changed?.Invoke(); PersistenceRequested?.Invoke(Settings); return true;
    }
    public bool SetMode(ThemeMode mode, bool persist = true)
    {
        if (!Current.Modes.Contains(mode)) return false;
        Change(Settings with { Mode = mode == ThemeMode.Light ? ThemeModePreference.Light : ThemeModePreference.Dark }, persist);
        return true;
    }
    public void Lock(bool persist = true) => SetMode(Current.Mode, persist);
    public void Unlock(bool persist = true) => Change(Settings with { Mode = ThemeModePreference.System }, persist);
    public void ApplyConfig(CliThemeSettings settings) => Change(settings, false);
    public void UpdateTerminal(ThemeMode mode, TerminalThemePalette? palette = null)
    {
        _terminalMode = palette is null ? mode : SystemTheme.DetectMode(palette) ?? mode;
        if (palette is not null) _palette = palette;
        Change(Settings, false);
    }
    private ThemeMode RequestedMode() => Settings.Mode switch
    { ThemeModePreference.Light => ThemeMode.Light, ThemeModePreference.Dark => ThemeMode.Dark, _ => _terminalMode };
    private void Change(CliThemeSettings settings, bool persist)
    {
        var previous = Settings;
        Settings = settings;
        try
        {
            _updating = true;
            if (_palette is not null) _catalog.SetSystem(SystemTheme.Generate(_palette, RequestedMode()));
            Current = _catalog.Resolve(settings.Name, RequestedMode());
        }
        catch { Settings = previous; throw; }
        finally { _updating = false; }
        Changed?.Invoke();
        if (persist) PersistenceRequested?.Invoke(Settings);
    }
    private void Reload()
    {
        if (_updating) return;
        try { Current = _catalog.Resolve(Settings.Name, RequestedMode()); Changed?.Invoke(); }
        catch (Exception error) { Error?.Invoke(error); throw; }
    }
    public void Dispose() => _catalog.Changed -= Reload;
}
